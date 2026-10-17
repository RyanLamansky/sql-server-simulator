using System.Text;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The DONEINPROCs of the statements real's ODBC catalog procedures run ahead
// of their result: they run without NOCOUNT, so each IF closes in a
// count-less 0x00C0 and each assignment in a 0x00C1 counting its one row.
// Which statements run depends on the arguments, so each procedure builds its
// sequence from what it was given and found, as captured from SQL Server 2025
// (2026-10-17) with a raw TDS client; NOCOUNT ON drops them all, as it drops
// any DONEINPROC carrying neither rows nor an error.
partial class Simulation
{
    /// <summary>
    /// The statement DONEs a step program describes, in order: <c>S</c> an
    /// assignment counting one row, <c>I</c> an <c>IF</c> condition, any
    /// other character ignored.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ProcedureSteps(BatchContext batch, string program)
    {
        foreach (var step in program)
        {
            switch (step)
            {
                case 'I':
                    yield return StatementDone(batch, StatementDoneKind.Condition);
                    break;
                case 'S':
                    yield return StatementDone(batch, StatementDoneKind.Select, 1);
                    break;
            }
        }
    }

    /// <summary>
    /// <c>sp_columns</c>' steps: the classic and <c>_170</c> spellings open
    /// with an assignment the <c>_90</c> / <c>_100</c> ones don't, an
    /// <c>@ODBCVer</c> other than 3 is one more, then six conditions — seven
    /// with a qualifier — among which a table, owner or column name of
    /// exactly <c>%</c> each assigns once more (after the second, third and
    /// fifth); two assignments and two conditions follow, and a name that
    /// picked one object closes with one more assignment and condition, where
    /// a pattern or a miss closes with a condition.
    /// </summary>
    private static string SpColumnsSteps(SpColumnsVersion version, int odbcVer, string? tableName, string? tableOwner, string? tableQualifier, string? columnName, bool oneObject)
    {
        var program = new StringBuilder();
        if (version is SpColumnsVersion.Classic or SpColumnsVersion.V170)
            _ = program.Append('S');
        _ = program.Append('I');
        if (odbcVer != 3 || version == SpColumnsVersion.V170)
            _ = program.Append('S');
        var conditions = tableQualifier is null ? 6 : 7;
        for (var i = 1; i <= conditions; i++)
        {
            _ = program.Append('I');
            if ((i == 2 && tableName == "%") || (i == 3 && tableOwner == "%") || (i == 5 && columnName == "%"))
                _ = program.Append('S');
        }
        return program.Append("SSII").Append(oneObject ? "SI" : "I").ToString();
    }

    /// <summary>
    /// <c>sp_tables</c>' steps with no <c>@table_type</c>: four conditions —
    /// five with a qualifier — an assignment, a condition, an assignment and
    /// a condition; a name adds a condition, and a name without a wildcard and
    /// no owner adds another, then assigns once more after the next two
    /// assignments when it found the object; two conditions follow, and a
    /// name that picked one object closes with an assignment and a condition,
    /// anything else with a condition.
    /// </summary>
    private static string SpTablesSteps(string? tableName, string? tableOwner, string? tableQualifier, bool found)
    {
        var exactName = tableName is not null && !HasCatalogWildcard(tableName);
        var exactOwner = tableOwner is null || !HasCatalogWildcard(tableOwner);
        var looksUpOwner = exactName && tableOwner is null;
        var program = new StringBuilder("IIII");
        if (tableQualifier is not null)
            _ = program.Append('I');
        _ = program.Append("SISI");
        if (tableName is not null)
            _ = program.Append('I');
        if (looksUpOwner)
            _ = program.Append('I');
        _ = program.Append("SS");
        if (looksUpOwner && found)
            _ = program.Append('S');
        return program.Append("II").Append(exactName && exactOwner && found ? "SI" : "I").ToString();
    }

    /// <summary><c>sp_pkeys</c>' steps: two conditions, one more each for an owner and a qualifier, then two assignments.</summary>
    private static string SpPkeysSteps(string? tableOwner, string? tableQualifier) =>
        "II" + (tableOwner is null ? "" : "I") + (tableQualifier is null ? "" : "I") + "SS";

    /// <summary>
    /// <c>sp_table_privileges</c>' steps: a condition when a qualifier is
    /// given, a condition, two assignments and two conditions; matching by
    /// pattern, three more conditions and, with no owner, an assignment.
    /// </summary>
    private static string SpTablePrivilegesSteps(string? owner, string? qualifier, bool usePattern) =>
        (qualifier is null ? "" : "I") + "ISSII" + (usePattern ? "III" + (owner is null ? "S" : "") : "");

    /// <summary>
    /// <c>sp_column_privileges</c>' steps: a condition, an assignment unless
    /// a column is named, two conditions, one more each for an owner and a
    /// qualifier, then an assignment.
    /// </summary>
    private static string SpColumnPrivilegesSteps(string? owner, string? qualifier, string? column) =>
        "I" + (column is null ? "S" : "") + "II" + (owner is null ? "" : "I") + (qualifier is null ? "" : "I") + "S";

    /// <summary>Whether a catalog procedure's name argument holds a LIKE wildcard.</summary>
    private static bool HasCatalogWildcard(string name) => name.AsSpan().IndexOfAny('%', '_', '[') >= 0;

    /// <summary>
    /// <c>sp_special_columns</c>' steps: two conditions — three under scope
    /// <c>T</c> — and an assignment, three conditions, one more each for an
    /// owner and a qualifier, two assignments, then two conditions for the
    /// row-version listing, three and an assignment for an object it found,
    /// or one for a miss.
    /// </summary>
    private static string SpSpecialColumnsSteps(string? owner, string? qualifier, bool connectionScope, bool rowVersions, bool found) =>
        (connectionScope ? "IIS" : "IIIS") + "III" + (owner is null ? "" : "I") + (qualifier is null ? "" : "I") + "SS"
        + (rowVersions ? "II" : found ? "IIIS" : "I");

    /// <summary>
    /// <c>sp_sproc_columns</c>' steps: two conditions, one more for a
    /// qualifier, an assignment, a condition, two assignments, a condition,
    /// one more for an owner, two assignments and two conditions; a name that
    /// picked one routine closes with an assignment and a condition, anything
    /// else with two conditions and an assignment.
    /// </summary>
    private static string SpSprocColumnsSteps(string? owner, string? qualifier, bool oneRoutine) =>
        "II" + (qualifier is null ? "" : "I") + "SISSI" + (owner is null ? "" : "I") + "SSII" + (oneRoutine ? "SI" : "IIS");

    /// <summary>
    /// <c>sp_stored_procedures</c>' steps: two conditions, an assignment for
    /// a name of exactly <c>%</c>, a condition, an assignment for an owner of
    /// exactly <c>%</c> — each of which the procedure then takes as none —
    /// and a condition; a name adds a condition, as does a qualifier, and a
    /// name holding no <c>%</c> with no owner looks the routine up by its
    /// exact name, adding another condition, then assigns once more after the
    /// next two assignments when one has that name. Under
    /// <c>@fUsePattern = 0</c> two conditions close it; matching by pattern,
    /// two conditions and an assignment follow, then an assignment and two
    /// conditions for a name that picked one routine, four conditions for
    /// another name, or a condition, an assignment, two conditions, an
    /// assignment and a condition for none, closing with one more assignment
    /// unless an owner was given or the lookup found the routine.
    /// </summary>
    private static string SpStoredProceduresSteps(string? name, string? owner, string? qualifier, bool usePattern, bool exactFound, bool oneRoutine)
    {
        var program = new StringBuilder("II");
        if (name == "%")
        {
            _ = program.Append('S');
            name = null;
        }
        _ = program.Append('I');
        if (owner == "%")
        {
            _ = program.Append('S');
            owner = null;
        }
        _ = program.Append('I');
        if (name is not null)
            _ = program.Append('I');
        if (qualifier is not null)
            _ = program.Append('I');
        var looksUpOwner = name is not null && !name.Contains('%', StringComparison.Ordinal) && owner is null;
        if (looksUpOwner)
            _ = program.Append('I');
        _ = program.Append("SS");
        var lookedUp = looksUpOwner && exactFound;
        if (lookedUp)
            _ = program.Append('S');
        if (!usePattern)
            return program.Append("II").ToString();
        _ = program.Append("IIS");
        return (oneRoutine ? program.Append("SII")
            : name is not null ? program.Append("IIII")
            : program.Append("ISIISI")).Append(lookedUp || owner is not null ? "" : "S").ToString();
    }
}
