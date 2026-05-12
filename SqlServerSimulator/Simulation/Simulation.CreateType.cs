using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
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
    /// Scalar UDT form (<c>CREATE TYPE name FROM &lt;basetype&gt;</c>) is
    /// not modeled — only the <c>AS TABLE</c> form. Anything other than
    /// <c>AS TABLE</c> after the type name raises Msg 156.
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

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Only the AS TABLE shape is modeled; AS <basetype> (scalar UDT) is
        // a separate feature that's not in scope.
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Table })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var fullName = $"{schema.Name}.{typeName.Leaf}";
        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn)>();

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
                if (heapColumns[i] is { } existing && Collation.Default.Equals(existing.Name, reference.Leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, typeName.Leaf)
                        : existing.Type;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && Collation.Default.Equals(pending.Name, reference.Leaf))
                            throw SimulatedSqlException.ComputedColumnReferencedInComputed(pending.Name, typeName.Leaf);
                    }
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pending in pendingComputed)
        {
            var resolvedType = pending.Expression.GetSqlType(ResolveComputedReference);
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
                isPersisted: pending.Persisted);
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
                    if (!Collation.Default.Equals(name.Leaf, owningColumn))
                        throw SimulatedSqlException.InlineCheckReferencesAnotherColumn(owningColumn, typeName.Leaf);
                }));
        }

        if (context.Batch.IsSkipping)
            return true;

        if (schema.TableTypes.ContainsKey(typeName.Leaf))
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
}
