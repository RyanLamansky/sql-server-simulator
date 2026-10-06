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
        if (!context.Batch.TryResolveCreateSchema(typeName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(typeName.ImmediateQualifier ?? Database.DefaultSchemaName);
        schema.Database.RejectWriteWhenReadOnly();
        // Dual DDL gate for both type forms, in real's probed order: the
        // database-scope CREATE TYPE permission (Msg 262 state 1), then ALTER on
        // the target schema (Msg 2760).
        if (!context.Batch.IsSkipping)
        {
            if (!PermissionEnforcement.HasDatabasePermission(context.Batch, schema.Database, Permission.CreateType))
                throw SimulatedSqlException.DatabasePermissionDenied("CREATE TYPE", schema.Database.Name);
            if (!PermissionEnforcement.HasSchemaAlter(context.Batch, schema))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
        }

        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.From }:
                return TryParseCreateAliasType(context, schema, typeName);
            case ReservedKeyword { Keyword: Keyword.External }:
                return ParseCreateClrType(context, schema, typeName);
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
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)>();

        var pendingIndexes = new List<PendingInlineIndex>();
        if (!ParseColumnList(context, typeName.Leaf, isTableVariable: false, isTableType: true, heapColumns, pendingKeys, pendingChecks, pendingComputed, pendingIndexes: pendingIndexes))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextOptional();
        var memoryOptimization = context.Token is ReservedKeyword { Keyword: Keyword.With }
            ? ParseTableTypeOptions(context)
            : default;

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
            if (Parser.Expressions.XmlMethodCall.AppearsIn(pending.Expression))
                throw SimulatedSqlException.XmlMethodInComputedColumn(pending.Name, typeName.Leaf, "CREATE TABLE", tableVariable: false);
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
                nullable: pending.Nullable && !IsPendingPrimaryKeyOrdinal(pendingKeys, pending.Index),
                computedExpression: pending.Expression,
                isPersisted: pending.Persisted,
                computedDefinition: pending.Definition);
        }

        // No CHECK predicate may read a non-persisted computed column —
        // Msg 1764, ahead of the Msg 8141 walk. Same as CREATE TABLE.
        BindCheckConstraints(context.Batch, heapColumns, pendingChecks);
        RejectChecksOverNonPersistedComputedColumns(context.Batch.CurrentDatabase.Collation, typeName.Leaf, heapColumns, pendingChecks);

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

        if (TypeNameIsTaken(context, schema, typeName))
            throw SimulatedSqlException.TypeAlreadyExists(typeName.ToString());
        // A memory-optimized table type needs an index, though no primary key
        // or container (probed 2026-10-02 against SQL Server 2025).
        ValidateTableDeclaration(database: null, typeName.Leaf, new(memoryOptimization.MemoryOptimized, durability: 1), heapColumns, pendingKeys, pendingIndexes);

        var typeTableObjectId = context.CurrentDatabase.AllocateObjectId();
        var createDate = context.Batch.CurrentStatement.UtcNow;
        var backingName = TableType.BackingTableNameOf(typeName.Leaf, typeTableObjectId);
        // A system-named DEFAULT is named after the backing type table, as
        // its keys and CHECKs are (probed 2026-09-26 against SQL Server 2025).
        foreach (var column in heapColumns)
        {
            if (column!.DefaultConstraint is { IsSystemNamed: true } columnDefault)
                columnDefault.Name = AutoDefaultName(backingName, column.Name);
        }
        var tableType = new TableType(
            schema,
            typeName.Leaf,
            typeTableObjectId,
            userTypeId: context.CurrentDatabase.AllocateUserTypeId(),
            createDate,
            columns: [.. heapColumns!],
            pendingKeys: [.. pendingKeys],
            pendingChecks: [.. pendingChecks],
            pendingIndexes: [.. pendingIndexes])
        {
            IsMemoryOptimized = memoryOptimization.MemoryOptimized,
        };
        // The catalog describes the type's own constraints and indexes on its
        // backing type table, resolved once so their ids hold; each @t clone
        // resolves copies of its own.
        var (keyObjectIds, indexObjectIds) = AllocateDeclarationObjectIds(context.CurrentDatabase, tableType.PendingKeys, tableType.PendingIndexes);
        var shape = new HeapTable(
            backingName, tableType.Columns, typeTableObjectId, Database.SysSchemaId, createDate,
            ResolveKeyConstraints(backingName, tableType.Columns, tableType.PendingKeys, context.CurrentDatabase, createDate, keyObjectIds),
            ResolveCheckConstraints(backingName, tableType.PendingChecks, context.CurrentDatabase, createDate),
            isTableVariable: true)
        {
            IsTypeTable = true,
            IsMemoryOptimized = memoryOptimization.MemoryOptimized,
        };
        AddInlineIndexes(context.Batch, shape, backingName, tableType.PendingIndexes, indexObjectIds);
        tableType.CatalogShape = shape;
        WarnOfOversizedMaximumRow(context.Batch, tableType.Columns, typeName.Leaf, state: 2);
        schema.TableTypes[typeName.Leaf] = tableType;
        RecordSlotUndo<TableType>(context, schema.TableTypes, typeName.Leaf, null);
        RecordDdlEvent(context, "CREATE_TYPE", schema.Name, typeName.Leaf, "TYPE");
        return true;
    }

    /// <summary>
    /// Parses a table type's <c>WITH (MEMORY_OPTIMIZED = ON | OFF)</c> option
    /// list, entered on <c>WITH</c> and leaving the cursor past its <c>)</c>.
    /// A table type takes no <c>DURABILITY</c> (Msg 10788, raised compiling
    /// the batch — probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static MemoryOptimizationOptions ParseTableTypeOptions(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        bool memoryOptimized;
        while (true)
        {
            var option = context.GetNextRequired();
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var value = context.GetNextRequired();
            memoryOptimized = option switch
            {
                StringToken name when name.Span.Equals("MEMORY_OPTIMIZED", StringComparison.OrdinalIgnoreCase) => value is ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle
                    ? toggle.Keyword == Keyword.On
                    : throw SimulatedSqlException.SyntaxErrorNear(context),
                StringToken name when name.Span.Equals("DURABILITY", StringComparison.OrdinalIgnoreCase) => throw SimulatedSqlException.DurabilityOnTableType(),
                _ => throw SimulatedSqlException.SyntaxErrorNear(option),
            };
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new(memoryOptimized, durability: 0);
    }

    /// <summary>
    /// Whether a type by this name exists already: an alias or table type of
    /// the schema, or — in <c>dbo</c> — one of the system types, whose names
    /// the shared type namespace holds (probed 2026-09-30: <c>CREATE TYPE
    /// dbo.int</c> is Msg 219).
    /// </summary>
    private static bool TypeNameIsTaken(ParserContext context, Schema schema, MultiPartName typeName) =>
        schema.TableTypes.ContainsKey(typeName.Leaf) || schema.AliasTypes.ContainsKey(typeName.Leaf)
        || (schema.SchemaId == Database.DboSchemaId && Array.Exists(BuiltInResources.SystypesRowData, row => context.CurrentDatabase.Collation.Equals((string)row[0]!, typeName.Leaf)));

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
    private static bool IsTwoArgumentBase(ReadOnlySpan<char> name)
        => name.Equals("decimal", StringComparison.OrdinalIgnoreCase)
            || name.Equals("numeric", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dec", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vector", StringComparison.OrdinalIgnoreCase);

    private static bool IsFractionalSecondBase(ReadOnlySpan<char> name)
        => name.Equals("datetime2", StringComparison.OrdinalIgnoreCase)
            || name.Equals("time", StringComparison.OrdinalIgnoreCase)
            || name.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseCreateAliasType(ParserContext context, Schema schema, MultiPartName typeName)
    {
        // Cursor on FROM; advance to the base-type name. The base is a
        // built-in, optionally qualified by sys; a multi-word synonym folds to
        // its canonical name. A base real refuses is refused when the
        // statement runs, so earlier statements of the batch run, and xml,
        // json and vector are
        // refused by name whatever their arguments say (probed 2026-09-26
        // against SQL Server 2025).
        context.MoveNextRequired();
        var (baseName, baseLeafToken) = TypeNameSynonyms.ReadTypeName(context);
        var refusal = baseName.Count == 1
            || (baseName.Count == 2 && string.Equals(baseName.ImmediateQualifier, "sys", StringComparison.OrdinalIgnoreCase))
            ? null
            : SimulatedSqlException.InvalidBaseTypeForAlias(baseName.ToString());
        if (refusal is null && string.Equals(baseName.Leaf, "json", StringComparison.OrdinalIgnoreCase))
            refusal = SimulatedSqlException.AliasTypeFromJson();
        if (refusal is null && string.Equals(baseName.Leaf, "vector", StringComparison.OrdinalIgnoreCase))
            refusal = SimulatedSqlException.AliasTypeFromVector();
        if (refusal is null && string.Equals(baseName.Leaf, "xml", StringComparison.OrdinalIgnoreCase))
            refusal = SimulatedSqlException.AliasTypeFromXml();

        // Optional (N[, S]) length / scale + trailing [NULL | NOT NULL] —
        // both pieces are optional, and either can land at end-of-batch
        // (`CREATE TYPE dbo.Probe FROM int` is a valid single-statement
        // batch). MoveNextOptional after the base-type-name leaf lets the
        // cursor walk off the end without raising.
        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.GetNextOptional() is Operator { Character: '(' } && refusal is not null)
        {
            Selection.SkipBalancedParens(context);
            context.MoveNextOptional();
        }
        else if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : lengthToken is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (declaredMaxLength == SqlType.MaxLengthSentinel && context.Token is Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
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

        // Resolve the base type. GetByName reports an unknown name in its own
        // message families; CREATE TYPE FROM names it in Msg 222 instead.
        SqlType resolvedType = SqlType.Int32;
        int? resolvedMaxLength = null;
        if (refusal is null)
        {
            // A size past 8000 is refused for its own reason before nvarchar's
            // 4000 ceiling is (Msg 131, probed 2026-09-30 against SQL Server 2025).
            if (declaredMaxLength is > 8000 and not SqlType.MaxLengthSentinel)
            {
                if (baseLeafToken.Span.Equals("nvarchar", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.SizeExceedsMaximumCast("nvarchar", declaredMaxLength.Value, 8000);
                if (baseLeafToken.Span.Equals("nchar", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.SizeExceedsMaximumCast("nchar", declaredMaxLength.Value, 8000);
            }
            // A second argument only decimal, numeric and vector take is refused
            // when the statement runs — after Msg 192 for a scale past the
            // first argument, which the parse settles — its state 5 for the
            // fractional-second types, 4 for the rest (probed 2026-09-30
            // against SQL Server 2025).
            if (declaredScale is { } extraScale
                && !IsTwoArgumentBase(baseLeafToken.Span))
            {
                if (extraScale > declaredMaxLength)
                    throw SimulatedSqlException.ScaleExceedsPrecision();
                refusal = SimulatedSqlException.FollowedByUdtParametersInvalid(
                    SimulatedSqlException.AliasBaseNeedsLength(baseLeafToken.Value, IsFractionalSecondBase(baseLeafToken.Span) ? (byte)5 : (byte)4), typeName.ToString());
            }
            if (refusal is null)
            {
                try
                {
                    (resolvedType, resolvedMaxLength) = SqlType.GetByName(
                        baseLeafToken, declaredMaxLength, declaredScale,
                        index: 0, TypeSpecSite.Scalar, columnName: baseLeafToken.Value);
                }
                catch (SimulatedSqlException ex) when (ex.Number is 2750 or 2716 || (ex.Number == 2717 && ex.State == 2))
                {
                    refusal = SimulatedSqlException.FollowedByUdtParametersInvalid(ex, typeName.ToString());
                }
                catch (SimulatedSqlException ex) when (ex.Number is 2715 or 243 or 102)
                {
                    refusal = SimulatedSqlException.InvalidBaseTypeForAlias(baseName.ToString());
                }
            }

            // A base whose length a column would default to takes none here
            // (probed 2026-09-30 against SQL Server 2025).
            if (refusal is null && declaredMaxLength is null && resolvedType is CharSqlType or VarcharSqlType or NCharSqlType or NVarcharSqlType or BinarySqlType or VarbinarySqlType)
                refusal = SimulatedSqlException.FollowedByUdtParametersInvalid(SimulatedSqlException.AliasBaseNeedsLength(baseLeafToken.Value), typeName.ToString());
        }

        // The CLR system types, rowversion and sysname (itself a system
        // alias) resolve but can't be an alias's base; the message names the
        // type by its canonical name.
        if (refusal is null && resolvedType is HierarchyIdSqlType or GeographySqlType or GeometrySqlType or RowVersionSqlType or SystemNameSqlType)
        {
            refusal = SimulatedSqlException.InvalidBaseTypeForAlias(
                baseName.Count == 2 ? $"{baseName.ImmediateQualifier}.{resolvedType.SqlServerName}" : resolvedType.SqlServerName);
        }

        // Anything left after the nullability is refused before the type exists
        // (`FROM int IDENTITY` is Msg 156).
        if (context.Token is not null && !IsStatementBoundary(context.Token))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.Batch.IsSkipping)
            return true;
        if (refusal is not null)
            throw refusal;

        if (TypeNameIsTaken(context, schema, typeName))
            throw SimulatedSqlException.TypeAlreadyExists(typeName.ToString());

        schema.AliasTypes[typeName.Leaf] = new AliasType(
            schema,
            typeName.Leaf,
            underlyingType: resolvedType,
            declaredMaxLength: resolvedMaxLength,
            isNullable: isNullable,
            userTypeId: context.CurrentDatabase.AllocateUserTypeId(),
            createDate: context.Batch.CurrentStatement.UtcNow,
            spelledNumeric: SqlType.IsNumericSpelling(baseName, null));
        RecordSlotUndo<AliasType>(context, schema.AliasTypes, typeName.Leaf, null);
        RecordDdlEvent(context, "CREATE_TYPE", schema.Name, typeName.Leaf, "TYPE");
        return true;
    }
}
