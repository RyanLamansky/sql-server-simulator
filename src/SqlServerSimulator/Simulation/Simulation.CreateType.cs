using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE TYPE schema.name AS TABLE (column_list)</c>. Stores
    /// the resulting <see cref="TableType"/> in the target
    /// <see cref="Schema.TableTypes"/> dict keyed by leaf name. Probed
    /// against SQL Server 2025 (2026-05-12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Column-list grammar mirrors <c>DECLARE @t TABLE</c> (both go through
    /// the shared <see cref="ParseColumnList"/> with <c>isTableType: true</c>).
    /// CONSTRAINT-named clauses and <c>REFERENCES</c> raise Msg 156 (probe-
    /// confirmed: real SQL Server's grammar disallows both inside
    /// <c>CREATE TYPE … AS TABLE</c>). Inline non-unique <c>INDEX</c> clauses
    /// are deferred — Msg 102 in v1; real SQL Server accepts them.
    /// </para>
    /// <para>
    /// Existence checks: a duplicate type name within the target schema
    /// raises Msg 219 ("The type '…' already exists, or you do not have
    /// permission to create it."). Cross-namespace collisions (a table /
    /// view / function / procedure already bearing the same leaf) do NOT
    /// raise — type names live in their own namespace (probe-confirmed).
    /// </para>
    /// <para>
    /// Scalar UDT form (<c>CREATE TYPE name FROM &lt;basetype&gt;[(N[, S])]
    /// [NULL | NOT NULL]</c>) is dispatched to
    /// <see cref="TryParseCreateAliasType"/>. Anything other than <c>AS</c>
    /// or <c>FROM</c> after the type name raises Msg 156.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateType(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var typeName = BatchContext.ParseObjectName(context);
        if (!context.Batch.TryResolveSchema(typeName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(typeName.ImmediateQualifier ?? Database.DefaultSchemaName);

        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.From }:
                return TryParseCreateAliasType(context, schema, typeName);
            case ReservedKeyword { Keyword: Keyword.As }:
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Table })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var fullName = $"{schema.Name}.{typeName.Leaf}";
        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)>();

        if (!ParseColumnList(context, typeName.Leaf, isTableVariable: false, isTableType: true, heapColumns, pendingKeys, pendingChecks, pendingComputed))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextOptional();

        // Pass 2: computed-column resolution. Same logic as DECLARE @t TABLE
        // (and CREATE TABLE) — resolve each pending computed expression
        // against the now-complete column set.
        SqlType ResolveComputedReference(MultiPartName reference)
        {
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } existing && context.Batch.CurrentDatabase.Collation.Equals(existing.Name, reference.Leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, typeName.Leaf)
                        : existing.Type;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && context.Batch.CurrentDatabase.Collation.Equals(pending.Name, reference.Leaf))
                            throw SimulatedSqlException.ComputedColumnReferencedInComputed(pending.Name, typeName.Leaf);
                    }
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pending in pendingComputed)
        {
            var resolvedType = pending.Expression.GetSqlType(context.Batch, ResolveComputedReference);
            int? computedMaxLength = resolvedType switch
            {
                VarcharSqlType v when v.length > 0 => v.length,
                NVarcharSqlType nv when nv.length > 0 => nv.length,
                VarbinarySqlType vb when vb.length > 0 => vb.length,
                _ => null,
            };
            heapColumns[pending.Index] = new HeapColumn(
                pending.Name,
                resolvedType,
                maxLength: computedMaxLength,
                nullable: pending.Nullable,
                computedExpression: pending.Expression,
                isPersisted: pending.Persisted,
                computedDefinition: pending.Definition);
        }

        // Inline-CHECK peer-ref check (Msg 8141) — same structural walk as
        // CREATE TABLE / DECLARE @t TABLE.
        foreach (var pending in pendingChecks)
        {
            if (pending.InlineColumn is not { } owningColumn)
                continue;
            pending.Predicate.VisitOperandExpressions(op =>
                op.VisitColumnReferences(name =>
                {
                    if (!context.Batch.CurrentDatabase.Collation.Equals(name.Leaf, owningColumn))
                        throw SimulatedSqlException.InlineCheckReferencesAnotherColumn(owningColumn, typeName.Leaf);
                }));
        }

        if (context.Batch.IsSkipping)
            return true;

        if (schema.TableTypes.ContainsKey(typeName.Leaf) || schema.AliasTypes.ContainsKey(typeName.Leaf))
            throw SimulatedSqlException.TypeAlreadyExists(fullName);

        var tableType = new TableType(
            schema,
            typeName.Leaf,
            typeTableObjectId: context.CurrentDatabase.AllocateObjectId(),
            userTypeId: context.CurrentDatabase.AllocateUserTypeId(),
            createDate: context.Batch.CurrentStatement.UtcNow,
            columns: [.. heapColumns!],
            pendingKeys: [.. pendingKeys],
            pendingChecks: [.. pendingChecks]);
        schema.TableTypes[typeName.Leaf] = tableType;
        return true;
    }

    /// <summary>
    /// Parses the scalar alias-type form of <c>CREATE TYPE</c>: <c>CREATE
    /// TYPE schema.name FROM &lt;builtin&gt;[(N[, S])] [NULL | NOT NULL]</c>.
    /// Entered with the cursor on the <c>FROM</c> keyword. Resolves the base
    /// type via the standard built-in lookup; unknown base raises
    /// <see cref="SimulatedSqlException.InvalidBaseTypeForAlias"/> (Msg 222).
    /// Stores the resulting <see cref="AliasType"/> in
    /// <see cref="Schema.AliasTypes"/>; duplicate type name in either
    /// <see cref="Schema.AliasTypes"/> or <see cref="Schema.TableTypes"/>
    /// raises Msg 219 (shared type-name namespace, probe-confirmed).
    /// </summary>
    /// <remarks>
    /// Nullability marker semantics — probe-confirmed against SQL Server 2025:
    /// bare <c>FROM int</c> and explicit <c>FROM int NULL</c> both set the
    /// alias's <see cref="AliasType.IsNullable"/> to true; <c>NOT NULL</c>
    /// sets false. The marker propagates as the column / variable default
    /// when the consumer omits its own nullability hint; an explicit
    /// <c>NULL</c> / <c>NOT NULL</c> at the consumer site overrides.
    /// </remarks>
    private static bool TryParseCreateAliasType(ParserContext context, Schema schema, MultiPartName typeName)
    {
        // Cursor on FROM; advance to the base-type name token. The base may
        // be a 1- or 2-part dotted name (e.g. `[sys].[int]`), matching real
        // SQL Server's grammar — but only the leaf is used for resolution,
        // since alias-of-alias isn't legal (probe-confirmed: Msg 222).
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        _ = BatchContext.ParseObjectName(context);
        if (context.Token is not Name baseLeafToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Optional (N[, S]) length / scale + trailing [NULL | NOT NULL] —
        // both pieces are optional, and either can land at end-of-batch
        // (`CREATE TYPE dbo.Probe FROM int` is a valid single-statement
        // batch). MoveNextOptional after the base-type-name leaf lets the
        // cursor walk off the end without raising.
        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.GetNextOptional() is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : lengthToken is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is Operator { Character: ',' })
            {
                var scaleToken = context.GetNextRequired();
                declaredScale = scaleToken is Numeric { Value: { IsNull: false } scaleNumeric }
                    ? scaleNumeric.AsInt32
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }

        // Optional [NULL | NOT NULL]. Bare and explicit NULL → nullable=true;
        // NOT NULL → nullable=false. Probe-confirmed.
        var isNullable = true;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Null }:
                isNullable = true;
                context.MoveNextOptional();
                break;
            case ReservedKeyword { Keyword: Keyword.Not }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                isNullable = false;
                context.MoveNextOptional();
                break;
        }

        // Resolve the base type. Real SQL Server's Msg 222 only fires when the
        // leaf isn't a recognized built-in; the simulator's GetByName raises a
        // different message family for invalid args. Catch the unknown-name
        // case and re-throw as Msg 222 verbatim.
        SqlType resolvedType;
        int? resolvedMaxLength;
        try
        {
            (resolvedType, resolvedMaxLength) = SqlType.GetByName(
                baseLeafToken, declaredMaxLength, declaredScale,
                index: 1, columnName: typeName.Leaf);
        }
        catch (SimulatedSqlException ex) when (ex.Number is 2715 or 243 or 102)
        {
            // GetByName routes unknown names through CannotFindDataType /
            // CannotFindDataTypeInCast / SyntaxErrorNear; for CREATE TYPE
            // FROM the canonical message is Msg 222 regardless of which path
            // the inner lookup took.
            throw SimulatedSqlException.InvalidBaseTypeForAlias(baseLeafToken.ToString());
        }

        if (context.Batch.IsSkipping)
            return true;

        var fullName = $"{schema.Name}.{typeName.Leaf}";
        if (schema.TableTypes.ContainsKey(typeName.Leaf) || schema.AliasTypes.ContainsKey(typeName.Leaf))
            throw SimulatedSqlException.TypeAlreadyExists(fullName);

        schema.AliasTypes[typeName.Leaf] = new AliasType(
            schema,
            typeName.Leaf,
            underlyingType: resolvedType,
            declaredMaxLength: resolvedMaxLength,
            declaredPrecision: null,
            declaredScale: declaredScale,
            isNullable: isNullable,
            userTypeId: context.CurrentDatabase.AllocateUserTypeId(),
            createDate: context.Batch.CurrentStatement.UtcNow);
        return true;
    }
}
