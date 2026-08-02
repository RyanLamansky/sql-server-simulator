using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_depends — the deprecated dependency report, projected from the same
// ModuleDependencies analysis sys.sql_expression_dependencies and the two
// dm_sql_referen*_entities DMVs read. Real's implementation reads sysdepends
// and prints its headers through raiserror at severity 10, so the two result
// sets arrive interleaved with info messages; shapes, wording and ordering
// probe-confirmed against SQL Server 2025 (2026-08-02).
partial class Simulation
{
    private static readonly SqlType[] SpDependsReferencesSchema =
    [
        SqlType.SystemName, NVarcharSqlType.Get(66, Collation.Baseline, Coercibility.Implicit),
        NVarcharSqlType.Get(7, Collation.Baseline, Coercibility.Implicit),
        NVarcharSqlType.Get(8, Collation.Baseline, Coercibility.Implicit),
        SqlType.SystemName,
    ];

    private static readonly string[] SpDependsReferencesColumnNames =
        ["name", "type", "updated", "selected", "column"];

    private static readonly SqlType[] SpDependsReferencedBySchema =
        [SqlType.SystemName, NVarcharSqlType.Get(66, Collation.Baseline, Coercibility.Implicit)];

    private static readonly string[] SpDependsReferencedByColumnNames = ["name", "type"];

    /// <summary>
    /// Real's <c>spt_values</c> type-<c>'O9T'</c> labels, keyed by the
    /// <c>sys.objects.type</c> code <c>sp_depends</c> joins on. Only the codes
    /// a dependency row can carry are listed; anything else reports its raw
    /// code, which is what the join's absence would leave real with.
    /// </summary>
    private static readonly FrozenDictionary<string, string> SpDependsTypeLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AF"] = "aggregate function",
            ["C "] = "check cns",
            ["D "] = "default (maybe cns)",
            ["FN"] = "scalar function",
            ["FS"] = "assembly scalar function",
            ["FT"] = "assembly table function",
            ["IF"] = "inline function",
            ["P "] = "stored procedure",
            ["SN"] = "synonym",
            ["SO"] = "sequence object",
            ["TF"] = "table function",
            ["TR"] = "trigger",
            ["U "] = "user table",
            ["V "] = "view",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly SqlValue SpDependsNo = SqlValue.FromNVarchar((NVarcharSqlType)SpDependsReferencesSchema[2], "no");
    private static readonly SqlValue SpDependsYes = SqlValue.FromNVarchar((NVarcharSqlType)SpDependsReferencesSchema[2], "yes");
    private static readonly SqlValue SpDependsNotSelected = SqlValue.FromNVarchar((NVarcharSqlType)SpDependsReferencesSchema[3], "no");
    private static readonly SqlValue SpDependsSelected = SqlValue.FromNVarchar((NVarcharSqlType)SpDependsReferencesSchema[3], "yes");

    /// <summary>
    /// Handles <c>EXEC sp_depends @objname</c>. Emits up to two result sets,
    /// each preceded by its own severity-10 header the way real's
    /// <c>raiserror</c> calls do: <strong>Msg 15459</strong> ahead of what the
    /// object references, then <strong>Msg 15460</strong> ahead of what
    /// references it. An object on neither side of the graph gets
    /// <strong>Msg 15461</strong> and no result set; a three-part name naming
    /// another database is <strong>Msg 15250</strong> and a name that resolves
    /// to nothing is <strong>Msg 15009</strong>, both raised before any output.
    /// </summary>
    /// <remarks>
    /// The "references" set is one row per referenced column, plus one row with
    /// a NULL <c>column</c> for a reference carrying no column detail (a
    /// function call, an <c>EXEC</c>, a synonym) — probe-confirmed against a
    /// procedure that both reads a table's column and calls a scalar UDF. The
    /// "referenced by" set is distinct on (schema-qualified name, type).
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDepends(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, _) = ParseHelpArgs(arguments, "sp_depends");
        var target = ResolveHelpTarget(batch, "sp_depends", objectName);
        var database = batch.CurrentDatabase;
        var entities = ModuleDependencies.Enumerate(database);
        var targetId = target.Object?.ObjectId ?? -1;

        var references = SpDependsReferenceRows(entities, targetId);
        var referencedBy = SpDependsReferencedByRows(database, entities, targetId);
        if (references.Count == 0 && referencedBy.Count == 0)
        {
            batch.AppendInfoError(@class: 10, state: 1, number: 15461,
                message: "Object does not reference any object, and no objects reference it.");
            yield break;
        }

        if (references.Count > 0)
        {
            batch.AppendInfoError(@class: 10, state: 1, number: 15459,
                message: "In the current database, the specified object references the following:");
            yield return new SimulatedSqlResultSet(SpDependsReferencesSchema, SpDependsReferencesColumnNames, references);
        }

        if (referencedBy.Count > 0)
        {
            batch.AppendInfoError(@class: 10, state: 1, number: 15460,
                message: "In the current database, the specified object is referenced by the following:");
            yield return new SimulatedSqlResultSet(SpDependsReferencedBySchema, SpDependsReferencedByColumnNames, referencedBy);
        }
    }

    /// <summary>What the named object references, one row per referenced column (or one column-less row).</summary>
    private static List<SqlValue[]> SpDependsReferenceRows(List<ModuleDependencies.Entity> entities, int targetId)
    {
        var rows = new List<SqlValue[]>();
        foreach (var entity in entities)
        {
            if (entity.ReferencingId != targetId || entity.ReferencingMinorId != 0)
                continue;
            foreach (var reference in entity.References)
            {
                if (reference.Resolved is not { } resolved)
                    continue;
                var name = SqlValue.FromSystemName($"{reference.SchemaName ?? Database.DefaultSchemaName}.{reference.EntityName}");
                var type = SpDependsTypeLabel(resolved.ObjectTypeCode);
                if (reference.Columns.Count == 0)
                {
                    rows.Add([name, type, SpDependsNo, SpDependsNotSelected, SqlValue.Null(SqlType.SystemName)]);
                    continue;
                }
                foreach (var column in reference.Columns)
                {
                    rows.Add([
                        name,
                        type,
                        column.Updated ? SpDependsYes : SpDependsNo,
                        // Real's "selected" cell is `readobj | selall`, so a
                        // column reached through a `*` reads as selected here
                        // even though the catalog view separates the two.
                        column.Selected || column.SelectAll ? SpDependsSelected : SpDependsNotSelected,
                        SqlValue.FromSystemName(column.Name),
                    ]);
                }
            }
        }
        return rows;
    }

    /// <summary>What references the named object, distinct on (name, type).</summary>
    private static List<SqlValue[]> SpDependsReferencedByRows(
        Database database, List<ModuleDependencies.Entity> entities, int targetId)
    {
        var rows = new List<SqlValue[]>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            if (entity.ReferencingId == targetId)
                continue;
            var referencesTarget = false;
            foreach (var reference in entity.References)
                referencesTarget |= reference.ReferencedId == targetId;
            if (!referencesTarget)
                continue;

            // A DDL trigger has no schema-qualified name real can report, and a
            // computed column reports under its own table's row rather than a
            // second one.
            if (entity.ReferencingClass != ModuleDependencies.ObjectOrColumnClass)
                continue;
            var qualified = $"{entity.SchemaName}.{entity.EntityName}";
            if (seen.Add(qualified))
                rows.Add([SqlValue.FromSystemName(qualified), SpDependsTypeLabel(entity.ObjectTypeCode)]);
        }
        _ = database;
        return rows;
    }

    private static SqlValue SpDependsTypeLabel(string typeCode) => SqlValue.FromNVarchar(
        (NVarcharSqlType)SpDependsReferencesSchema[1],
        SpDependsTypeLabels.TryGetValue(typeCode, out var label) ? label : typeCode.TrimEnd());
}
