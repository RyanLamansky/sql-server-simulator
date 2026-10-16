using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The API server cursor procedures' work, shared by their two front doors: a
// TDS RPC request (Network/TdsSession.Cursors.cs) and EXEC text
// (Simulation.ApiCursorProcs.cs). Each operation acts on the session's
// handles and answers what the call reports — its errors, return status and
// output values — for the front door to send in its own form.
partial class Simulation
{
    /// <summary>What an <c>sp_cursoropen</c>, <c>sp_cursorexecute</c> or <c>sp_cursorprepexec</c> open reports.</summary>
    internal sealed class ApiCursorOpening(int scrollopt, int ccopt)
    {
        /// <summary>The errors the open raised, in the order real sends them; null when it opened.</summary>
        public SimulatedSqlException? Error;

        public int ReturnStatus;

        /// <summary>The handle reported, 0 when nothing opened.</summary>
        public int Handle;

        /// <summary>The scroll and concurrency options reported: the effective ones, or as sent on a refusal.</summary>
        public int Scroll = scrollopt, Cc = ccopt;

        /// <summary>The row count reported, or null when the call leaves the argument as sent.</summary>
        public int? RowCount;

        /// <summary>The cursor opened.</summary>
        public ApiServerCursor? Cursor;

        /// <summary>The rows an AUTO_FETCH open fetched, which its result set carries; null for a metadata-only announce.</summary>
        public List<SqlValue[]>? Fetched;
    }

    /// <summary>What an <c>sp_cursorfetch</c> reports.</summary>
    internal sealed class ApiCursorFetching
    {
        public SimulatedSqlException? Error;

        public int ReturnStatus;

        /// <summary>The cursor fetched from, whose result set carries <see cref="Rows"/>.</summary>
        public ApiServerCursor? Cursor;

        /// <summary>The rows fetched, each ending in its ROWSTAT; null when no result set goes out.</summary>
        public List<SqlValue[]>? Rows;

        /// <summary>An INFO fetch's position and row count.</summary>
        public int InfoRowNumber, InfoRowCount;
    }

    /// <summary>What an <c>sp_cursor</c> operation reports.</summary>
    internal sealed class ApiCursorOperating
    {
        public SimulatedSqlException? Error;

        public int ReturnStatus;

        /// <summary>The counts the operation's DONE tokens carry: one for an INSERT, two for an UPDATE or DELETE.</summary>
        public readonly List<int> Counts = [];
    }

    /// <summary>
    /// Opens <paramref name="statement"/> as an API server cursor through
    /// <paramref name="declareAndOpen"/>, which runs the synthesized
    /// <c>DECLARE … CURSOR … FOR</c> and <c>OPEN</c> it is handed — with the
    /// statement's parameter bindings — and answers the cursor it declared,
    /// or throws what failed. A STATIC or FAST_FORWARD request takes only
    /// READ_ONLY concurrency, else Msg 16966 with no handle; PARAMETERIZED_STMT
    /// (0x1000) on an <c>sp_cursoropen</c> without parameter definitions is
    /// Msg 16902 state 22, and an <c>sp_cursorprepexec</c>'s definitions
    /// without it state 50; a statement that fails reports its errors then
    /// Msg 16945, returning the first one's number with the requested option
    /// bits echoed and a zero row count; and CHECK_ACCEPTED_TYPES (0x8000)
    /// refuses a type the cursor settled on outside its <c>*_ACCEPTABLE</c>
    /// bits with Msg 16955 and Msg 16945 (each probed 2026-09-29 through
    /// 2026-10-16 against SQL Server 2025). An open that also prepares the
    /// statement — <c>sp_cursorprepexec</c>, and <c>sp_cursorprepare</c>'s
    /// check — reports a failing statement's errors then Msg 8180 instead,
    /// returning 8180.
    /// </summary>
    internal static ApiCursorOpening OpenApiCursor(
        SimulatedDbConnection connection,
        string procedure,
        string statement,
        int scrollopt,
        int ccopt,
        int rowcountSent,
        bool parameterizedWithoutDefinition,
        bool definitionWithoutParameterized,
        Func<string, string, Cursor> declareAndOpen,
        Func<SimulatedDbCommand, BatchContext> fetchBatch,
        bool preparing = false)
    {
        var opening = new ApiCursorOpening(scrollopt, ccopt);
        if ((scrollopt & 0x1F) is 0x8 or 0x10 && (ccopt & 0xF) is 0x2 or 0x4 or 0x8)
        {
            opening.Error = AtProcedureLine(SimulatedSqlException.ApiCursorConcurrencyIncompatible(ccopt & 0xF, procedure), procedure, 1);
            opening.ReturnStatus = 1;
            return opening;
        }
        if (((scrollopt & 0x1000) != 0 && parameterizedWithoutDefinition) || ((scrollopt & 0x1000) == 0 && definitionWithoutParameterized))
        {
            opening.Error = AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "scrollopt", parameterizedWithoutDefinition ? (byte)22 : (byte)50), procedure, 1);
            opening.ReturnStatus = 1;
            if (definitionWithoutParameterized)
            {
                opening.Handle = 0;
                opening.RowCount = 0;
            }
            return opening;
        }

        // The engine cursor takes the next handle as it is declared, which
        // becomes the API cursor's; its name is the handle it will take.
        var name = "sss_apicursor_" + (connection.LastCursorHandle + 2).ToString(CultureInfo.InvariantCulture);
        Cursor cursor;
        try
        {
            if (!IsSingleSelect(statement))
                throw SimulatedSqlException.ApiCursorStatementNotSelect().PinLine(1);
            cursor = declareAndOpen(name, $"DECLARE {name} CURSOR GLOBAL {CursorOptionKeywords(scrollopt, ccopt)} FOR {statement}\n;OPEN {name};");
        }
        catch (SimulatedSqlException failure)
        {
            _ = connection.Cursors.Remove(name);
            var notOpened = preparing ? SimulatedSqlException.StatementsCouldNotBePrepared() : SimulatedSqlException.ApiCursorNotDeclared();
            opening.Error = SimulatedSqlException.Aggregate([failure, AtProcedureLine(notOpened, procedure, 1)]);
            opening.ReturnStatus = preparing ? notOpened.Number : failure.Number;
            opening.Scroll = scrollopt & 0x1F;
            opening.Cc = ccopt & 0xF;
            opening.RowCount = 0;
            return opening;
        }

        var (effectiveScroll, effectiveCc, rowcount) = ResolveEffectiveOptions(cursor, scrollopt, ccopt, connection.LastCursorRows);
        if ((scrollopt & 0x8000) != 0 && (scrollopt & ((effectiveScroll & 0x1F) << 16)) == 0)
        {
            if (cursor.IsOpen)
                cursor.Close(connection);
            _ = connection.Cursors.Remove(name);
            opening.Error = SimulatedSqlException.Aggregate([
                AtProcedureLine(SimulatedSqlException.ApiCursorNotAcceptable(), procedure, 1),
                AtProcedureLine(SimulatedSqlException.ApiCursorNotDeclared(), procedure, 1)]);
            opening.ReturnStatus = 16955;
            opening.RowCount = 0;
            return opening;
        }

        var api = new ApiServerCursor(name, cursor, fetchBatch);
        connection.ApiCursors[api.Handle] = api;
        opening.Cursor = api;
        opening.Handle = api.Handle;
        opening.Scroll = effectiveScroll;
        opening.Cc = effectiveCc;
        opening.RowCount = rowcount;

        // AUTO_FETCH (0x2000) fetches the first rows with the open — as many as
        // the row count sent, else 20 — and reports how many it fetched as the
        // row count, whatever the cursor's type (probed 2026-10-06 against SQL
        // Server 2025).
        if ((scrollopt & 0x2000) != 0)
        {
            opening.Fetched = FetchIntoBuffer(connection, api, FetchDirection.Next, 0, rowcountSent > 0 ? rowcountSent : 20);
            opening.RowCount = opening.Fetched.Count;
        }
        return opening;
    }

    /// <summary>
    /// <c>sp_cursorfetch</c>: an INFO fetch (0x100) reports the position of
    /// the last fetch buffer's first row (-1 for an empty one) and the
    /// cursor's row count, any other fetches up to <paramref name="nrows"/>
    /// rows, the first in the fetch type's direction and the rest NEXT; a
    /// handle the session doesn't hold is Msg 16909, and an error the fetch
    /// itself raises — a direction the cursor's type refuses — returns 0
    /// with no result set (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static ApiCursorFetching FetchApiCursor(SimulatedDbConnection connection, int handle, int fetchType, int rownum, int nrows)
    {
        var fetching = new ApiCursorFetching();
        if (!connection.ApiCursors.TryGetValue(handle, out var api))
        {
            fetching.Error = AtProcedureLine(SimulatedSqlException.ApiCursorHandleInvalid("sp_cursorfetch", handle), "sp_cursorfetch", 1);
            fetching.ReturnStatus = 1;
            return fetching;
        }
        fetching.Cursor = api;
        if ((fetchType & 0x100) != 0)
        {
            fetching.InfoRowNumber = api.Cursor.ApiFetchBuffer?.Start ?? 0;
            fetching.InfoRowCount = connection.LastCursorRows;
            return fetching;
        }

        var (direction, offset) = (fetchType & 0xFF) switch
        {
            0x1 => (FetchDirection.First, 0L),
            0x4 => (FetchDirection.Prior, 0L),
            0x8 => (FetchDirection.Last, 0L),
            0x10 => (FetchDirection.Absolute, rownum),
            // RELATIVE counts from the first row of the last fetch buffer,
            // where the cursor sits on its last (probed 2026-10-16 against
            // SQL Server 2025).
            0x20 => (FetchDirection.Relative, rownum - Math.Max(0, (api.Cursor.ApiFetchBuffer?.Size ?? 0) - 1)),
            _ => (FetchDirection.Next, 0L),
        };
        // A forward-only cursor takes RELATIVE forward as so many NEXTs
        // (probed 2026-10-16 against SQL Server 2025).
        var skip = 0L;
        if (direction == FetchDirection.Relative && !api.Cursor.Scrollable && offset > 0)
            (direction, skip, offset) = (FetchDirection.Next, offset - 1, 0);
        try
        {
            fetching.Rows = FetchIntoBuffer(connection, api, direction, offset, nrows, skip);
        }
        catch (SimulatedSqlException refused)
        {
            fetching.Error = refused.PinLine(1);
        }
        return fetching;
    }

    /// <summary>
    /// Fetches up to <paramref name="nrows"/> rows into <paramref name="api"/>'s
    /// buffer, the first in <paramref name="firstDirection"/> and the rest
    /// NEXT, each carrying its ROWSTAT: 1 for a row, 2 for a keyset member
    /// deleted out from under the cursor, which fills its buffer row with the
    /// values a T-SQL <c>FETCH</c> reports for one and doesn't end the fetch
    /// (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static List<SqlValue[]> FetchIntoBuffer(SimulatedDbConnection connection, ApiServerCursor api, FetchDirection firstDirection, long offset, int nrows, long skip = 0)
    {
        api.Buffer.Clear();
        var rows = new List<SqlValue[]>();
        using var fetchCommand = connection.CreateCommand();
        fetchCommand.CommandText = " ";
        var batch = api.FetchBatch(fetchCommand);
        var start = -1;
        var status = 0;
        for (var i = 0L; i < skip && status == 0; i++)
            (status, _) = api.Cursor.Fetch(batch, FetchDirection.Next, 0);
        for (var i = 0; i < nrows && status is 0 or -2; i++)
        {
            (status, var values) = api.Cursor.Fetch(batch, i == 0 ? firstDirection : FetchDirection.Next, offset);
            if (i == 0 && status is 0 or -2)
                start = api.Cursor.FetchBufferStart;
            if (status == -2)
            {
                rows.Add(Cursor.WithRowStat(api.Cursor.DeletedMemberValues(), 2));
                api.Buffer.Add(new (int Page, int Slot)?[api.Cursor.BaseTables.Length]);
                continue;
            }
            if (status != 0 || values is null)
                break;
            rows.Add(Cursor.WithRowStat(values, 1));
            if (api.Cursor.CurrentRids is { } rids)
                api.Buffer.Add(rids);
        }
        api.Cursor.ApiFetchBuffer = (rows.Count, rows.Count == 0 ? -1 : start);
        // The fetch leaves @@ROWCOUNT at the rows it sent and
        // @@FETCH_STATUS at its last row's status (probed 2026-10-16 against
        // SQL Server 2025).
        connection.LastStatementRowCount = rows.Count;
        connection.LastFetchStatus = status;
        return rows;
    }

    /// <summary>
    /// <c>sp_cursor</c>: an INSERT (0x4) adds a row through the cursor's table
    /// from the named values, whatever the fetch buffer holds; UPDATE (0x1),
    /// DELETE (0x2) and SETPOSITION (0x20) act on fetch-buffer row
    /// <paramref name="rownum"/>, 1-based — an empty buffer Msg 16931 and a row
    /// past it Msg 16930, each followed by Msg 3621, and a read-only cursor
    /// Msg 16929 state 2. Each write runs through
    /// <paramref name="runWrite"/>, as a positioned <c>UPDATE</c> / <c>DELETE
    /// … WHERE CURRENT OF</c> or an <c>INSERT</c> whose values are the
    /// variables <paramref name="valueNames"/> name, and reports its row count
    /// twice for an UPDATE or DELETE, once for an INSERT (probed 2026-10-11 and
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static ApiCursorOperating OperateApiCursor(SimulatedDbConnection connection, int handle, int optype, int rownum, string table, List<string> valueNames, Func<string, int> runWrite)
    {
        var operating = new ApiCursorOperating();
        if (!connection.ApiCursors.TryGetValue(handle, out var api))
        {
            operating.Error = AtProcedureLine(SimulatedSqlException.ApiCursorHandleInvalid("sp_cursor", handle), "sp_cursor", 1);
            operating.ReturnStatus = 1;
            return operating;
        }
        if (table.Length == 0 && api.Cursor.BaseTables is [var only, ..])
            table = "[" + only.Name.Replace("]", "]]", StringComparison.Ordinal) + "]";

        try
        {
            if ((optype & 0x4) != 0)
            {
                var columns = string.Join(", ", valueNames.Select(static value => "[" + value.TrimStart('@').Replace("]", "]]", StringComparison.Ordinal) + "]"));
                operating.Counts.Add(runWrite(valueNames.Count == 0
                    ? $"INSERT INTO {table} DEFAULT VALUES;"
                    : $"INSERT INTO {table} ({columns}) VALUES ({string.Join(", ", valueNames)});"));
                return operating;
            }

            if ((optype & 0x3) != 0 && api.Cursor.ReadOnly)
            {
                operating.Error = AtProcedureLine(SimulatedSqlException.CursorIsReadOnly(state: 2), "sp_cursor", 1);
                operating.ReturnStatus = 1;
                return operating;
            }
            if (api.Buffer.Count == 0 || rownum < 1 || rownum > api.Buffer.Count)
            {
                var refusal = api.Buffer.Count == 0 ? SimulatedSqlException.CursorNoCurrentRow() : SimulatedSqlException.ApiCursorRowNotInBuffer();
                operating.Error = SimulatedSqlException.Aggregate([refusal.PinLine(1)], [SimulatedSqlException.AttentionStatementTerminatedMessage(connection)]);
                operating.ReturnStatus = refusal.Number;
                return operating;
            }

            // Position the engine cursor on the requested buffer row so its
            // WHERE CURRENT OF matching targets it; SETPOSITION stops there.
            api.Cursor.CurrentRids = api.Buffer[rownum - 1];
            if ((optype & 0x3) == 0)
                return operating;

            var affected = runWrite((optype & 0x2) != 0
                ? $"DELETE FROM {table} WHERE CURRENT OF {api.InternalName};"
                : $"UPDATE {table} SET {string.Join(", ", valueNames.Select(static value => "[" + value.TrimStart('@').Replace("]", "]]", StringComparison.Ordinal) + "] = " + value))} WHERE CURRENT OF {api.InternalName};");
            operating.Counts.Add(affected);
            operating.Counts.Add(affected);
        }
        catch (SimulatedSqlException failure)
        {
            operating.Counts.Clear();
            operating.Error = failure;
            operating.ReturnStatus = failure.Number;
        }
        return operating;
    }

    /// <summary><c>sp_cursorclose</c>: closes and releases the cursor; a handle the session doesn't hold is Msg 16909.</summary>
    internal static SimulatedSqlException? CloseApiCursor(SimulatedDbConnection connection, int handle)
    {
        if (!connection.ApiCursors.Remove(handle, out var api))
            return AtProcedureLine(SimulatedSqlException.ApiCursorHandleInvalid("sp_cursorclose", handle), "sp_cursorclose", 1);
        if (api.Cursor.IsOpen)
            api.Cursor.Close(connection);
        _ = connection.Cursors.Remove(api.InternalName);
        if (api.Alias is { } alias)
            _ = connection.Cursors.Remove(alias);
        return null;
    }

    /// <summary>
    /// <c>sp_cursoroption</c>: TEXTPTR_ONLY (1) and TEXTDATA (3) take a
    /// column number naming a <c>text</c>, <c>ntext</c> or <c>image</c> column
    /// (else Msg 16926), or 0; CURSOR_NAME (2) names the cursor, which T-SQL
    /// then reaches it by; SCROLLOPT, CCOPT and ROWCOUNT (4 – 6) refuse their
    /// value and any other code is Msg 16902 (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException? SetApiCursorOption(SimulatedDbConnection connection, int handle, int code, SqlValue value)
    {
        if (!connection.ApiCursors.TryGetValue(handle, out var api))
            return AtProcedureLine(SimulatedSqlException.ApiCursorHandleInvalid("sp_cursoroption", handle), "sp_cursoroption", 1);
        SimulatedSqlException? refusal = null;
        switch (code)
        {
            case 1 or 3:
                if (value.IsNull || value.Type != SqlType.Int32)
                {
                    refusal = SimulatedSqlException.ApiCursorParameterInvalid("sp_cursoroption", "value", 10);
                    break;
                }
                var column = value.AsInt32;
                var schema = api.Cursor.Selection.Schema;
                if (column != 0 && (column < 1 || column > schema.Length || !schema[column - 1].IsLegacyLob))
                    refusal = SimulatedSqlException.ApiCursorColumnNotText(column);
                break;
            case 2:
                if (value.IsNull || !SqlType.IsStringCategory(value.Type))
                {
                    refusal = SimulatedSqlException.ApiCursorParameterInvalid("sp_cursoroption", "cursorname", 11);
                    break;
                }
                if (api.Alias is { } previous)
                    _ = connection.Cursors.Remove(previous);
                api.Alias = value.CoerceTo(SqlType.NVarchar).AsString;
                connection.Cursors[api.Alias] = api.Cursor;
                break;
            case 4:
            case 5:
            case 6:
                refusal = SimulatedSqlException.ApiCursorParameterInvalid("sp_cursoroption", "value", (byte)(code + 8));
                break;
            default:
                refusal = SimulatedSqlException.ApiCursorParameterInvalid("sp_cursoroption", "code", 15);
                break;
        }
        return refusal is null ? null : AtProcedureLine(refusal, "sp_cursoroption", 1);
    }

    /// <summary>
    /// Whether <paramref name="statement"/> is a single statement — a query an
    /// API server cursor can be declared over — by finding no <c>;</c> that a
    /// further statement follows outside quotes, brackets and comments, and a
    /// <c>SELECT</c>, <c>WITH</c> or <c>(</c> leading it.
    /// </summary>
    private static bool IsSingleSelect(string statement)
    {
        var text = statement.AsSpan().Trim();
        var leading = text;
        while (leading.Length > 0 && leading[0] == '(')
            leading = leading[1..].TrimStart();
        if (!(leading.StartsWith("select", StringComparison.OrdinalIgnoreCase) || leading.StartsWith("with", StringComparison.OrdinalIgnoreCase))
            && !leading.StartsWith("exec", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\'':
                case '"':
                case '[':
                    var close = text[i] == '[' ? ']' : text[i];
                    for (i++; i < text.Length; i++)
                    {
                        if (text[i] != close)
                            continue;
                        if (i + 1 < text.Length && text[i + 1] == close)
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    break;
                case '-' when i + 1 < text.Length && text[i + 1] == '-':
                    while (i < text.Length && text[i] != '\n')
                        i++;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                    var end = text[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                    i = end < 0 ? text.Length : i + 3 + end;
                    break;
                case ';' when text[(i + 1)..].Trim().TrimEnd(';').Trim().Length > 0:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The options a cursor reports for the ones requested: a FAST_FORWARD
    /// request stays FAST_FORWARD READ_ONLY over any shape; a query no base
    /// table's rows identify settles STATIC READ_ONLY with its row count; a
    /// DYNAMIC or FORWARD_ONLY request the shape caps at KEYSET — a sort, a
    /// row limit — reports KEYSET with its row count; otherwise the requested
    /// low bits pass through, the row count -1 for the shapes that don't
    /// materialize their rows. UPDT_IN_PLACE (0x4000) comes back beside
    /// whatever concurrency the cursor settles on (probed 2026-09-29 against
    /// SQL Server 2025).
    /// </summary>
    private static (int Scroll, int Cc, int RowCount) ResolveEffectiveOptions(Cursor cursor, int scrollopt, int ccopt, int lastCursorRows)
    {
        var requestedScroll = scrollopt & 0x1F;
        var inPlace = ccopt & 0x4000;
        var requestedCc = ccopt & 0xF;
        if (requestedScroll == 0x10)
            return (0x10, 0x1 | inPlace, -1);
        if (cursor.BaseTables.Length == 0)
            return (0x8, 0x1 | inPlace, lastCursorRows);
        var cc = (requestedCc == 0 ? 0x1 : requestedCc) | inPlace;
        if (requestedScroll is 0x2 or 0x4 && cursor.Sensitivity == CursorSensitivity.Keyset)
            return (0x1, cc, lastCursorRows);
        return (requestedScroll, cc, requestedScroll is 0x2 or 0x4 ? -1 : lastCursorRows);
    }

    /// <summary>
    /// The <c>DECLARE CURSOR</c> keywords for the requested options. SCROLL_LOCKS
    /// and OPTIMISTIC keep the cursor updatable, their concurrency control not
    /// wired for an API cursor (whose optimistic conflicts real doesn't
    /// surface either), so only READ_ONLY says anything.
    /// </summary>
    private static string CursorOptionKeywords(int scrollopt, int ccopt)
    {
        var sensitivity = (scrollopt & 0x1F) switch
        {
            0x2 => "DYNAMIC",
            0x4 => "FORWARD_ONLY",
            0x8 => "STATIC",
            0x10 => "FAST_FORWARD",
            _ => "KEYSET",
        };
        return (ccopt & 0xF) == 0x1 ? sensitivity + " READ_ONLY" : sensitivity;
    }
}
