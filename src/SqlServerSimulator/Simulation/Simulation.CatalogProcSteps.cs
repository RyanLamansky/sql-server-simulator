using System.Text;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The DONEINPROCs of the statements real's system procedures run without
// NOCOUNT around their results: each IF closes in a count-less 0x00C0 and
// each assignment in a 0x00C1 counting its one row. Which statements run
// depends on the arguments, so each procedure builds its sequence from what it
// was given and found, as captured from SQL Server 2025 (2026-10-17) with a
// raw TDS client; NOCOUNT ON drops them all, as it drops any DONEINPROC
// carrying neither rows nor an error.
partial class Simulation
{
    /// <summary>
    /// The statement DONEs a step program describes, in order: <c>S</c> an
    /// assignment counting one row, <c>I</c> an <c>IF</c> or <c>WHILE</c>
    /// condition, <c>N</c> a cursor <c>DECLARE</c> or <c>FETCH</c> (a
    /// count-less <c>0x00C1</c>), <c>(</c> and <c>)</c> a cursor's
    /// <c>OPEN</c> and <c>DEALLOCATE</c>, <c>O</c> a <c>SET</c> turning an
    /// option off, <c>X</c> an <c>EXEC</c>, <c>P</c> a <c>PRINT</c>, <c>R</c>
    /// a <c>RAISERROR</c> of a message, <c>D</c> a <c>DBCC</c> command,
    /// <c>T</c> and <c>E</c> a <c>BEGIN TRY</c> and the end of its
    /// <c>CATCH</c>, <c>U</c> a <c>RETURN</c>, and <c>B</c> and <c>C</c> the
    /// <c>BEGIN TRANSACTION</c> and <c>COMMIT</c> of a transaction the
    /// procedure opens and commits with nothing of its own inside — nested in
    /// the caller's, whose count it raises meanwhile; any other character
    /// ignored.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ProcedureSteps(BatchContext batch, string program)
    {
        SimulatedDbTransaction? own = null;
        SimulatedDbTransaction? nested = null;
        foreach (var step in program)
        {
            switch (step)
            {
                case '(':
                    yield return StatementDone(batch, StatementDoneKind.OpenCursor);
                    break;
                case ')':
                    yield return StatementDone(batch, StatementDoneKind.DeallocateCursor);
                    break;
                case 'B':
                    if (batch.Connection.CurrentTransaction is { } caller)
                    {
                        caller.TranCount++;
                        nested = caller;
                    }
                    else
                    {
                        own = batch.Connection.StartTransaction(System.Data.IsolationLevel.Unspecified);
                    }
                    yield return StatementDone(batch, StatementDoneKind.BeginTransaction);
                    break;
                case 'C':
                    if (own is not null)
                        End(batch.Connection, own, commit: true);
                    else if (nested is not null)
                        Unnest(batch.Connection, nested);
                    own = nested = null;
                    yield return StatementDone(batch, StatementDoneKind.Commit);
                    break;
                case 'D':
                    yield return StatementDone(batch, StatementDoneKind.Dbcc);
                    break;
                case 'E':
                    yield return StatementDone(batch, StatementDoneKind.EndCatch);
                    break;
                case 'I':
                    yield return StatementDone(batch, StatementDoneKind.Condition);
                    break;
                case 'N':
                    yield return StatementDone(batch, StatementDoneKind.Select);
                    break;
                case 'O':
                    yield return StatementDone(batch, StatementDoneKind.SetOptionOff);
                    break;
                case 'P':
                    yield return StatementDone(batch, StatementDoneKind.Print);
                    break;
                case 'R':
                    yield return StatementDone(batch, StatementDoneKind.RaisError);
                    break;
                case 'S':
                    yield return StatementDone(batch, StatementDoneKind.Select, 1);
                    break;
                case 'T':
                    yield return StatementDone(batch, StatementDoneKind.BeginTry);
                    break;
                case 'U':
                    yield return StatementDone(batch, StatementDoneKind.Return);
                    break;
                case 'X':
                    yield return StatementDone(batch, StatementDoneKind.Execute);
                    break;
            }
        }
    }

    /// <summary>
    /// <paramref name="inside"/> between the <c>BEGIN TRANSACTION</c> and
    /// <c>COMMIT</c> real's procedure runs it in — a transaction of its own,
    /// whose ENVCHANGEs go out ahead of the two statements' DONEINPROCs, or
    /// one nested in the caller's (see <see cref="InProcedureTransaction"/>) —
    /// or the <c>ROLLBACK</c> it ends its own with after a failed statement or,
    /// with <paramref name="rollsBack"/>, a refused change (captured
    /// 2026-10-17 against SQL Server 2025: <c>sp_approlepassword</c> of a
    /// missing role).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ProcedureTransactionSteps(BatchContext batch, IEnumerable<SimulatedStatementOutcome> inside, bool rollsBack = false)
    {
        var own = batch.Connection.CurrentTransaction is null;
        var failed = false;
        foreach (var outcome in InProcedureTransaction(batch, Begun(batch, inside), rollsBack: rollsBack))
        {
            failed |= outcome is SimulatedErrorOutcome;
            yield return outcome;
        }
        yield return StatementDone(batch, rollsBack || (failed && own) ? StatementDoneKind.Rollback : StatementDoneKind.Commit);

        static IEnumerable<SimulatedStatementOutcome> Begun(BatchContext batch, IEnumerable<SimulatedStatementOutcome> inside)
        {
            yield return StatementDone(batch, StatementDoneKind.BeginTransaction);
            foreach (var outcome in inside)
                yield return outcome;
        }
    }

    /// <summary>
    /// A refusal <paramref name="systemProcName"/> raises with a
    /// <c>RAISERROR</c> and then goes on past, running
    /// <paramref name="after"/>'s statements before it returns 1: the error
    /// closes the <c>RAISERROR</c>'s DONEINPROC, and neither
    /// <c>XACT_ABORT</c> nor the error ends the batch. Inside a <c>TRY</c> the
    /// <c>RAISERROR</c>'s DONEINPROC goes out clear, then
    /// <paramref name="caughtAfter"/>'s — the <c>EXEC</c>s an inner procedure
    /// raising it returns through — and the error is caught there (captured
    /// 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> RaisedAndGoneOn(BatchContext batch, string systemProcName, SimulatedSqlException error, string after, string caughtAfter = "")
    {
        if (SystemProcedureErrorSite(systemProcName, error) is { } site)
            AttributeToSystemProcedure(error, site, systemProcName, systemProcName);
        return GoneOnPast(batch, error, after, caughtAfter);
    }

    /// <summary>
    /// <see cref="RaisedAndGoneOn"/> for an <paramref name="error"/> already
    /// attributed to its procedure.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> GoneOnPast(BatchContext batch, SimulatedSqlException error, string after, string caughtAfter = "")
    {
        error.RaisedBySystemProcedure = true;
        if (batch.Connection.OpenTryFrames > 0)
        {
            yield return StatementDone(batch, StatementDoneKind.RaisError);
            foreach (var step in ProcedureSteps(batch, caughtAfter))
                yield return step;
            // Caught there, it leaves the procedure's own transaction open, as
            // a failed statement's error does.
            error.RaisedByProcedureStatement = true;
            throw error;
        }
        yield return new SimulatedErrorOutcome(error)
        {
            DoneKind = error.RaisedByProcedureStatement ? StatementDoneKind.SecurityDdlFailed : StatementDoneKind.RaisError,
            InModule = batch.ProcFrame is not null || batch.TriggerFrame is not null,
            TransactionEventMark = batch.Connection.TransactionEventsRecorded,
        };
        foreach (var step in ProcedureSteps(batch, after))
            yield return step;
    }

    /// <summary>
    /// The qualifier check the ODBC catalog procedures make once
    /// <paramref name="before"/>'s statements ran: a qualifier other than the
    /// current database's name — compared as a name, so a pattern is refused
    /// too — is Msg 15250 at <paramref name="line"/>, after which the procedure
    /// returns -6, through a <c>RETURN</c> unless <paramref name="returns"/>
    /// is false (captured 2026-10-17 against SQL Server 2025); null when the
    /// qualifier passes.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome>? CatalogQualifierRefusal(BatchContext batch, string procedureName, string? qualifier, string before, int line, bool returns = true) =>
        qualifier is null || batch.CurrentDatabase.Collation.Equals(qualifier, batch.CurrentDatabase.Name)
            ? null
            : CatalogRefusal(batch, procedureName, SimulatedSqlException.HelpObjectNotInCurrentDatabase(), before, line, returns);

    /// <summary>
    /// A catalog procedure's <paramref name="refusal"/>, raised at
    /// <paramref name="line"/> once <paramref name="before"/>'s statements ran
    /// and answered -6, through a <c>RETURN</c> when <paramref name="returns"/>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> CatalogRefusal(BatchContext batch, string procedureName, SimulatedSqlException refusal, string before, int line, bool returns = true)
    {
        refusal.PreserveDiagnostics(line, procedureName);
        refusal.SystemProcedureReturnCode = -6;
        foreach (var step in ProcedureSteps(batch, before))
            yield return step;
        foreach (var outcome in GoneOnPast(batch, refusal, returns ? "U" : ""))
            yield return outcome;
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
    /// <c>sp_tables</c>' steps: four conditions — five with a qualifier — an
    /// assignment and a condition, then an assignment and a condition, or for
    /// a <c>@table_type</c> list a condition and an assignment, then three
    /// conditions with an assignment after the first when the list names
    /// <c>'TABLE'</c> and after the second when it names <c>'VIEW'</c>,
    /// quotes included; a name adds a condition, and a name holding no
    /// <c>%</c> with no owner looks the table up by its exact name, adding
    /// another, then assigns once more after the next two assignments when
    /// one has that name. Two conditions follow, then, matching by pattern, an
    /// assignment and a condition for a name that picked one object, a
    /// condition for anything else; under <c>@fUsePattern = 0</c> nothing.
    /// </summary>
    private static string SpTablesSteps(string? tableName, string? tableOwner, string? tableQualifier, string? tableType, bool usePattern, bool exactFound)
    {
        var program = new StringBuilder("IIII");
        if (tableQualifier is not null)
            _ = program.Append('I');
        _ = program.Append("SI");
        if (tableType is null)
        {
            _ = program.Append("SI");
        }
        else
        {
            _ = program.Append("ISI");
            if (tableType.Contains("'TABLE'", StringComparison.OrdinalIgnoreCase))
                _ = program.Append('S');
            _ = program.Append('I');
            if (tableType.Contains("'VIEW'", StringComparison.OrdinalIgnoreCase))
                _ = program.Append('S');
            _ = program.Append('I');
        }
        if (tableName is not null)
            _ = program.Append('I');
        var looksUpOwner = tableName is not null && !tableName.Contains('%', StringComparison.Ordinal) && tableOwner is null;
        if (looksUpOwner)
            _ = program.Append('I');
        _ = program.Append("SS");
        if (looksUpOwner && exactFound)
            _ = program.Append('S');
        _ = program.Append("II");
        if (!usePattern)
            return program.ToString();
        var exactName = tableName is not null && !HasCatalogWildcard(tableName);
        var exactOwner = tableOwner is null || !HasCatalogWildcard(tableOwner);
        return program.Append(exactName && exactOwner && exactFound ? "SI" : "I").ToString();
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
    /// unless an owner was given or the lookup found the routine; a name that
    /// brings in the system routines (<paramref name="listsSystem"/>) takes a
    /// condition, an assignment and three conditions in place of the four.
    /// </summary>
    private static string SpStoredProceduresSteps(string? name, string? owner, string? qualifier, bool usePattern, bool exactFound, bool oneRoutine, bool listsSystem)
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
            : name is not null ? program.Append(listsSystem ? "ISIII" : "IIII")
            : program.Append("ISIISI")).Append(lookedUp || owner is not null ? "" : "S").ToString();
    }
}
