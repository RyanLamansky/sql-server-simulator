using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DECLARE &lt;name&gt; [INSENSITIVE] [SCROLL] CURSOR
    /// [LOCAL|GLOBAL] [FORWARD_ONLY|SCROLL] [STATIC|KEYSET|DYNAMIC|FAST_FORWARD]
    /// [READ_ONLY|SCROLL_LOCKS|OPTIMISTIC] [TYPE_WARNING] FOR &lt;select&gt;
    /// [FOR {READ ONLY | UPDATE [OF cols]}]</c> — both the SQL-92 and T-SQL
    /// extended forms. The effective sensitivity is resolved here: a
    /// non-updatable query (anything but a single base table with a unique
    /// key) is forced to STATIC, matching SQL Server's silent conversion.
    /// Cursor variables (<c>DECLARE @c CURSOR</c>) aren't modeled. On entry the
    /// cursor is on the <c>DECLARE</c> keyword.
    /// </summary>
    private static void ParseDeclareCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume DECLARE

        // DECLARE @c CURSOR (cursor variable) routes through TryParseDeclare;
        // this path is the bare-identifier form `DECLARE name … CURSOR … FOR …`.
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var cursorName = nameToken.Value;
        context.MoveNextRequired();

        var reqStatic = false;   // INSENSITIVE (pre-CURSOR)
        var scroll = false;

        // SQL-92 pre-CURSOR options: [INSENSITIVE] [SCROLL].
        while (context.Token is UnquotedString)
        {
            if (IsWord(context.Token, "INSENSITIVE"))
                reqStatic = true;
            else if (IsWord(context.Token, "SCROLL"))
                scroll = true;
            else
                break;
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Cursor })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var built = BuildCursorDefinition(batch, cursorName, reqStatic, scroll);
        if (built is not { } definition)
            return; // skipping — tokens consumed, nothing registered
        batch.CountedStatementLine = batch.CurrentStatement.StartLine;
        DeclareCursorInScope(batch, cursorName, definition.Cursor, definition.Local);
    }

    /// <summary>
    /// Shared parser for a cursor definition — <c>CURSOR [options] FOR
    /// &lt;select&gt; [FOR {READ ONLY | UPDATE [OF cols]}]</c> — used by both
    /// <c>DECLARE name CURSOR …</c> and <c>SET @c = CURSOR …</c>. On entry the
    /// cursor sits on the <c>CURSOR</c> keyword; <paramref name="reqStatic"/> /
    /// <paramref name="scroll"/> carry any SQL-92 pre-CURSOR INSENSITIVE / SCROLL
    /// flags. Resolves effective sensitivity, scrollability, read-only,
    /// concurrency (SCROLL_LOCKS / OPTIMISTIC) and the FOR UPDATE OF list, and
    /// emits the TYPE_WARNING info message. Returns the built <see cref="Cursor"/>
    /// plus whether LOCAL scope was requested, or null under
    /// <see cref="BatchContext.IsSkipping"/> (tokens consumed, nothing built).
    /// </summary>
    private static (Cursor Cursor, bool Local)? BuildCursorDefinition(BatchContext batch, string cursorName, bool reqStatic, bool scroll)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume CURSOR
        batch.BeginImplicitTransaction();

        // The SQL-92 form's INSENSITIVE / SCROLL come before CURSOR and take no
        // T-SQL option after it; its INSENSITIVE is a snapshot that scrolls
        // only when SCROLL is written too.
        var sql92 = reqStatic || scroll;
        var insensitive92 = reqStatic;
        var options = CursorOptions.None;

        // T-SQL post-CURSOR options, in any order, until FOR.
        Span<char> buffer = stackalloc char[12];
        while (context.Token is not ReservedKeyword { Keyword: Keyword.For })
        {
            if (context.Token is not UnquotedString option || option.Span.Length > buffer.Length)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var upper = buffer[..option.Span.Length];
            _ = option.Span.ToUpperInvariant(upper);
            var named = upper switch
            {
                "DYNAMIC" => CursorOptions.Dynamic,
                "FAST_FORWARD" => CursorOptions.FastForward,
                "FORWARD_ONLY" => CursorOptions.ForwardOnly,
                "GLOBAL" => CursorOptions.Global,
                "INSENSITIVE" => CursorOptions.None,
                "KEYSET" => CursorOptions.Keyset,
                "LOCAL" => CursorOptions.Local,
                "OPTIMISTIC" => CursorOptions.Optimistic,
                "READ_ONLY" => CursorOptions.ReadOnly,
                "SCROLL" => CursorOptions.Scroll,
                "SCROLL_LOCKS" => CursorOptions.ScrollLocks,
                "STATIC" => CursorOptions.Static,
                "TYPE_WARNING" => CursorOptions.TypeWarning,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            // INSENSITIVE belongs to the SQL-92 form only, and any option at
            // all after CURSOR mixes the two forms (probed 2026-09-29 against
            // SQL Server 2025).
            if (named == CursorOptions.None)
                throw SimulatedSqlException.InvalidCursorOption("insensitive");
            if (sql92)
                throw SimulatedSqlException.CursorSyntaxMixed().PinLine(0);
            options |= named;
            context.MoveNextRequired();
        }

        var reqDynamic = (options & CursorOptions.Dynamic) != 0;
        var reqFastForward = (options & CursorOptions.FastForward) != 0;
        var forwardOnly = (options & CursorOptions.ForwardOnly) != 0;
        var reqKeyset = (options & CursorOptions.Keyset) != 0;
        // Naming neither scope takes the database's CURSOR_DEFAULT (probed
        // 2026-10-02 against SQL Server 2025).
        var localScope = (options & CursorOptions.Local) != 0
            || ((options & CursorOptions.Global) == 0 && (context.CurrentDatabase.Switches & DatabaseSwitches.LocalCursorDefault) != 0);
        var optimistic = (options & CursorOptions.Optimistic) != 0;
        var readOnlyOption = (options & CursorOptions.ReadOnly) != 0;
        var scrollLocks = (options & CursorOptions.ScrollLocks) != 0;
        var typeWarning = (options & CursorOptions.TypeWarning) != 0;
        scroll |= (options & CursorOptions.Scroll) != 0;
        reqStatic |= (options & CursorOptions.Static) != 0;

        context.MoveNextRequired(); // consume FOR
        Selection selection;
        var reads = new List<SchemaObject>();
        using (ParserScope.Enter(ref context.CursorStatement, true))
        using (ParserScope.Enter(ref context.PartitionedWriteReads, reads))
        {
            batch.CursorDeclarationSnapshot = new(BatchContext.VariableNameComparer);
            try
            {
                selection = ParseBodyQuery(context);
            }
            finally
            {
                batch.CursorDeclarationSnapshot = null;
            }
        }
        // A cursor's query can't create a table (probed 2026-10-02 against
        // SQL Server 2025).
        if (selection.IntoTarget is not null)
            throw SimulatedSqlException.IntoNotAllowedInCursorDeclaration();

        // Trailing SQL-92 updatability clause: FOR READ ONLY | FOR UPDATE [OF cols].
        List<string>? forUpdateColumns = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.For })
        {
            context.MoveNextRequired();
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Read }:
                    context.MoveNextRequired();
                    if (!IsWord(context.Token, "ONLY"))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    readOnlyOption = true;
                    options |= CursorOptions.ForReadOnly;
                    context.MoveNextOptional();
                    break;
                case ReservedKeyword { Keyword: Keyword.Update }:
                    options |= CursorOptions.ForUpdate;
                    context.MoveNextOptional();
                    // Optional OF <col> [, <col>]… — captured so a positioned
                    // UPDATE of a column outside the list raises Msg 16932.
                    if (context.Token is ReservedKeyword { Keyword: Keyword.Of })
                    {
                        forUpdateColumns = [];
                        do
                        {
                            if (context.GetNextRequired() is not Name ofColumn)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            // A qualified column (`t.v`) names its leaf.
                            while (context.GetNextOptional() is Operator { Character: '.' })
                            {
                                if (context.GetNextRequired() is not Name leaf)
                                    throw SimulatedSqlException.SyntaxErrorNear(context);
                                ofColumn = leaf;
                            }
                            forUpdateColumns.Add(ofColumn.Value);
                        } while (context.Token is Operator { Character: ',' });
                        RejectUnboundUpdateColumns(batch, selection, forUpdateColumns);
                    }
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        if (ConflictingCursorOptionsError(options, insensitive92) is { } conflict)
        {
            // Real reports it at the token after the declaration, or at the
            // declaration's last line when the batch ends there.
            if (context.Token is null && context.LastToken is { } last)
            {
                foreach (var error in conflict.Errors)
                    error.LineNumber = last.LineNumber;
            }
            throw conflict;
        }

        if (batch.IsSkipping)
            return null;

        // Resolve updatability + effective sensitivity. A query whose FROM the
        // cursor can't re-fold (no CursorSourcePlan) is forced to STATIC, as is
        // an explicit STATIC / INSENSITIVE / FAST_FORWARD request. Building the
        // plan here rather than at SELECT-parse time is what lets a view slot
        // be followed into its body: the body is parsed once, at DECLARE, the
        // point real SQL Server fixes the cursor's plan at too. Otherwise
        // honor the requested type; an unspecified type defaults to KEYSET when
        // SCROLL was asked for and DYNAMIC for the forward-only default. A JOIN
        // is updatable exactly like a single table (probe-confirmed).
        var cursorPlan = Selection.TryBuildCursorPlan(selection, batch);
        var updatable = cursorPlan is not null;
        // Two shapes cap sensitivity at KEYSET, both probe-confirmed against
        // sys.dm_exec_cursors and both applying to the bare forward-only
        // default as well as an explicit DYNAMIC. A TOP / OFFSET / FETCH limit
        // — on the statement or inside a body the cursor follows — chooses
        // which rows are members at OPEN, so there is no live set for DYNAMIC
        // to walk. An ORDER BY no index delivers has to be sorted, and a sorted
        // result is a materialized one.
        var keysetOnly = cursorPlan is { HasRowLimit: true } or { OrderBySuppliedByIndex: false };

        // A forward-only read-only cursor naming no sensitivity is a
        // FAST_FORWARD one, which sys.dm_exec_cursors reports it as (probed
        // 2026-09-29 against SQL Server 2025) — READ_ONLY or FOR READ ONLY with
        // neither SCROLL nor STATIC / INSENSITIVE / KEYSET / DYNAMIC.
        // An API server cursor's FORWARD_ONLY READ_ONLY keeps its own
        // negotiation (a sort converts it to KEYSET), so only the T-SQL form
        // reads the pair that way.
        var fastForward = reqFastForward || (readOnlyOption && !scroll && !reqStatic && !reqKeyset && !reqDynamic && !batch.ApiServerCursor);

        // The type the keywords ask for, before the shape has its say: naming
        // one takes it, and naming none means KEYSET for SCROLL and DYNAMIC for
        // the forward-only default.
        var requested = reqStatic
            ? CursorSensitivity.Static
            : reqKeyset
                ? CursorSensitivity.Keyset
                : reqDynamic
                    ? CursorSensitivity.Dynamic
                    : scroll ? CursorSensitivity.Keyset : CursorSensitivity.Dynamic;

        // FAST_FORWARD runs its plan as the fetches go, so it reads live rows
        // — inserts ahead appear, deletes ahead vanish, updates show — unless
        // the plan has to finish a sort or a row limit first, which settles
        // the rows at OPEN (probed 2026-09-29 against SQL Server 2025); either
        // way it never needs a row locator.
        var sensitivity = !updatable
            ? CursorSensitivity.Static
            : fastForward
                ? keysetOnly ? CursorSensitivity.Static : CursorSensitivity.Dynamic
                : requested == CursorSensitivity.Dynamic && keysetOnly
                    ? CursorSensitivity.Keyset
                    : requested;
        if (fastForward)
            requested = sensitivity;

        // A T-SQL KEYSET keys on a row locator every participating base table
        // has to carry, so a table with none converts the cursor to a read-only
        // snapshot — probe-confirmed for every route to KEYSET (explicit,
        // through SCROLL, through either cap) and for a join with one keyless
        // side. Dropping the plan alongside puts the cursor on exactly the
        // STATIC path a non-navigable query takes, so nothing downstream has to
        // tell the two apart.
        // The API server cursors sp_cursoropen drives are exempt: real keeps
        // those Keyset over a keyless table and takes positioned DML through
        // them, keying on the row address the way this engine always does
        // (probe-confirmed, and the split sys.dm_exec_cursors reports as
        // "API | Keyset" against the T-SQL cursor's "TSQL | Snapshot").
        if (sensitivity == CursorSensitivity.Keyset && !batch.ApiServerCursor && cursorPlan is { SupportsKeyset: false })
        {
            sensitivity = CursorSensitivity.Static;
            cursorPlan = null;
        }

        var readOnly = sensitivity == CursorSensitivity.Static || fastForward || readOnlyOption;
        // Naming a sensitivity implies SCROLL unless FORWARD_ONLY says
        // otherwise — probe-confirmed for DYNAMIC as well as STATIC / KEYSET.
        // A cursor that names none is forward-only whatever it resolved to:
        // probe-confirmed that a bare cursor over a DISTINCT query (converted
        // to a snapshot) and a bare cursor over a TOP query (converted to
        // Keyset) both report Msg 16911 for a scrolling direction. So the test
        // is on the requested keyword, never the resolved sensitivity.
        var scrollable = scroll
            || (((reqStatic && !insensitive92) || reqKeyset || reqDynamic) && !forwardOnly && !fastForward);

        // Concurrency model (updatable cursors only): SCROLL_LOCKS holds a
        // cursor-scoped U lock on the fetched row; OPTIMISTIC detects out-of-
        // band modification at positioned-DML time. A read-only cursor ignores
        // both (positioned DML is rejected with Msg 16929 anyway).
        var concurrency = readOnly
            ? CursorConcurrency.Default
            : optimistic
                ? CursorConcurrency.Optimistic
                : scrollLocks ? CursorConcurrency.ScrollLocks : CursorConcurrency.Default;

        // TYPE_WARNING: emit Msg 16956 (info, severity 10) at DECLARE when the
        // requested sensitivity was silently converted to a lesser one —
        // DYNAMIC / KEYSET over a non-updatable shape forced to STATIC, or
        // DYNAMIC over a row limit or an unindexed ORDER BY forced to KEYSET
        // (probe-confirmed the warning fires at DECLARE, not OPEN). The
        // *implied* request counts too: a cursor naming no sensitivity warns
        // for a converted DYNAMIC and a plain SCROLL one for a converted
        // KEYSET, both probe-confirmed.
        if (typeWarning && sensitivity != requested)
        {
            batch.AppendInfoError(@class: 0, state: 1, number: 16956, "The created cursor is not of the requested type.");
        }

        var cursor = new Cursor(
            cursorName,
            selection,
            sensitivity,
            scrollable,
            readOnly,
            cursorPlan,
            concurrency,
            forUpdateColumns,
            fastForward)
        {
            Handle = batch.Connection.LastCursorHandle += 2,
            CreationTime = batch.CurrentStatement.UtcNow,
            DeclaringText = batch.Parser.Command.CommandText,
            DeclaringStart = batch.CurrentStatement.StartIndex,
            DeclaringEnd = Math.Max(batch.CurrentStatement.StartIndex, batch.Parser.PreviousTokenEnd - 1),
            DeclaredLocal = localScope,
            PartitionedViewsRead = [.. reads.OfType<View>().Select(view => view.PartitionedBase).OfType<View>().Distinct()],
        };
        cursor.StatementIdentity = QueryStoreStatementIdentity(batch, cursor.DeclaringText[cursor.DeclaringStart..(cursor.DeclaringEnd + 1)]);

        // Scope: an explicit LOCAL or GLOBAL wins; otherwise the database's
        // CURSOR_DEFAULT, GLOBAL unless ALTER DATABASE set it LOCAL.
        return (cursor, localScope);
    }

    /// <summary>
    /// Binds a <c>FOR UPDATE OF</c> list against the query's FROM sources as
    /// real does while compiling the batch, whatever the cursor resolves to:
    /// a name no source carries is Msg 207 — an output alias counts for
    /// nothing — and one only a constructed rowset such as <c>VALUES</c>
    /// carries is Msg 412 (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static void RejectUnboundUpdateColumns(BatchContext batch, Selection selection, List<string> columns)
    {
        if (selection.BranchFromSources is not { } sources)
            return;
        var collation = batch.CurrentDatabase.Collation;
        foreach (var column in columns)
        {
            FromSource? owner = null;
            foreach (var source in sources)
            {
                if (Array.Exists(source.ColumnNames, name => collation.Equals(name, column)))
                {
                    owner = source;
                    break;
                }
            }
            if (owner is null)
                throw SimulatedSqlException.InvalidColumnName(column);
            if (owner is { BackingTable: null, BackingView: null, LateralIsQueryBody: false })
                throw SimulatedSqlException.ColumnDerivedOrConstant(column);
        }
    }

    /// <summary>The options a cursor declaration writes, as the conflict
    /// check reads them.</summary>
    [Flags]
    private enum CursorOptions
    {
        None = 0,
        Local = 1 << 0,
        Global = 1 << 1,
        ForwardOnly = 1 << 2,
        Scroll = 1 << 3,
        Static = 1 << 4,
        Keyset = 1 << 5,
        Dynamic = 1 << 6,
        FastForward = 1 << 7,
        ReadOnly = 1 << 8,
        ScrollLocks = 1 << 9,
        Optimistic = 1 << 10,
        TypeWarning = 1 << 11,
        ForUpdate = 1 << 12,
        ForReadOnly = 1 << 13,
    }

    /// <summary>
    /// The option pairs real refuses together, each named in real's order,
    /// in the order real checks them — the first pair present is the one
    /// reported (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static readonly (CursorOptions First, string FirstName, CursorOptions Second, string SecondName)[] ConflictingCursorOptions =
    [
        (CursorOptions.Local, "LOCAL", CursorOptions.Global, "GLOBAL"),
        (CursorOptions.Scroll, "SCROLL", CursorOptions.ForwardOnly, "FORWARD_ONLY"),
        (CursorOptions.Scroll, "SCROLL", CursorOptions.FastForward, "FAST_FORWARD"),
        (CursorOptions.Static, "STATIC", CursorOptions.Keyset, "KEYSET"),
        (CursorOptions.Static, "STATIC", CursorOptions.Dynamic, "DYNAMIC"),
        (CursorOptions.Static, "STATIC", CursorOptions.FastForward, "FAST_FORWARD"),
        (CursorOptions.Static, "STATIC", CursorOptions.ScrollLocks, "SCROLL_LOCKS"),
        (CursorOptions.Keyset, "KEYSET", CursorOptions.Dynamic, "DYNAMIC"),
        (CursorOptions.Keyset, "KEYSET", CursorOptions.FastForward, "FAST_FORWARD"),
        (CursorOptions.Dynamic, "DYNAMIC", CursorOptions.FastForward, "FAST_FORWARD"),
        (CursorOptions.FastForward, "FAST_FORWARD", CursorOptions.ScrollLocks, "SCROLL_LOCKS"),
        (CursorOptions.FastForward, "FAST_FORWARD", CursorOptions.Optimistic, "OPTIMISTIC"),
        (CursorOptions.ReadOnly, "READ_ONLY", CursorOptions.ScrollLocks, "SCROLL_LOCKS"),
        (CursorOptions.Optimistic, "OPTIMISTIC", CursorOptions.ReadOnly, "READ_ONLY"),
        (CursorOptions.ScrollLocks, "SCROLL_LOCKS", CursorOptions.Optimistic, "OPTIMISTIC"),
        (CursorOptions.ForUpdate, "FOR UPDATE", CursorOptions.Static, "STATIC"),
        (CursorOptions.FastForward, "FAST_FORWARD", CursorOptions.ForUpdate, "FOR UPDATE"),
        (CursorOptions.ForUpdate, "FOR UPDATE", CursorOptions.ReadOnly, "READ_ONLY"),
    ];

    /// <summary>
    /// The refusal of a declaration whose options contradict each other —
    /// Msg 1048 naming the pair, or Msg 1058 for READ_ONLY beside FOR READ
    /// ONLY, which otherwise counts as READ_ONLY — or null. Real raises these
    /// while compiling the batch, so they end it before anything runs.
    /// </summary>
    private static SimulatedSqlException? ConflictingCursorOptionsError(CursorOptions options, bool insensitive92)
    {
        if ((options & (CursorOptions.ReadOnly | CursorOptions.ForReadOnly)) == (CursorOptions.ReadOnly | CursorOptions.ForReadOnly))
            return SimulatedSqlException.CursorReadOnlyTwice();
        if ((options & CursorOptions.ForReadOnly) != 0)
            options |= CursorOptions.ReadOnly;
        if (insensitive92 && (options & CursorOptions.ForUpdate) != 0)
            return SimulatedSqlException.ConflictingCursorOptions("FOR UPDATE", "INSENSITIVE");
        foreach (var (first, firstName, second, secondName) in ConflictingCursorOptions)
        {
            if ((options & first) != 0 && (options & second) != 0)
                return SimulatedSqlException.ConflictingCursorOptions(firstName, secondName);
        }
        return null;
    }

    /// <summary>Parses and runs <c>OPEN [GLOBAL] &lt;cursor&gt;</c> /
    /// <c>OPEN @cursorvar</c>.</summary>
    private static void ParseOpenCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume OPEN
        var reference = ReadCursorReference(context);
        if (batch.IsSkipping)
            return;
        var cursor = ResolveCursor(batch, reference, missingAtPriorLine: true);
        batch.BeginImplicitTransaction();
        cursor.Open(batch);
    }

    /// <summary>Parses and runs <c>CLOSE [GLOBAL] &lt;cursor&gt;</c> /
    /// <c>CLOSE @cursorvar</c>.</summary>
    private static void ParseCloseCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume CLOSE
        var reference = ReadCursorReference(context);
        if (batch.IsSkipping)
            return;
        ResolveCursor(batch, reference).Close(batch.Connection);
        // @@CURSOR_ROWS reads 0 once the cursor is closed, and after a
        // DEALLOCATE (probed 2026-10-02 against SQL Server 2025).
        batch.Connection.LastCursorRows = 0;
    }

    /// <summary>
    /// Parses and runs <c>DEALLOCATE [GLOBAL] &lt;cursor&gt;</c> /
    /// <c>DEALLOCATE @cursorvar</c>. For a named cursor, unbinds the name from
    /// its scope (LOCAL preferred over GLOBAL when unqualified; GLOBAL-qualified
    /// targets the global map) and tears the cursor down if no variable still
    /// references it. For a cursor variable, drops that variable's reference
    /// (decrementing the shared cursor's refcount) and returns it to the
    /// unallocated state. Msg 16916 / 16950 on a miss.
    /// </summary>
    private static void ParseDeallocateCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume DEALLOCATE
        var reference = ReadCursorReference(context);
        if (batch.IsSkipping)
            return;

        if (reference.IsVariable)
        {
            // An unallocated variable is Msg 16950 here too (probed
            // 2026-09-29 against SQL Server 2025).
            if (!batch.CursorVariables.TryGetValue(reference.Name, out var bound) || bound is null)
                throw SimulatedSqlException.CursorVariableNotAllocated(reference.Name);
            ReleaseVariableReference(batch, bound);
            batch.CursorVariables[reference.Name] = null;
            return;
        }

        // Named cursor: unqualified removes from LOCAL first, then GLOBAL;
        // GLOBAL-qualified only touches the global map.
        Cursor removed;
        if (!reference.GlobalQualified && batch.LocalCursors.TryGetValue(reference.Name, out removed!))
            _ = batch.LocalCursors.Remove(reference.Name);
        else if (batch.Connection.Cursors.TryGetValue(reference.Name, out removed!))
            _ = batch.Connection.Cursors.Remove(reference.Name);
        else
            throw SimulatedSqlException.CursorDoesNotExist(reference.Name);

        // Drop the name; only destroy the object if no cursor variable still
        // holds it (refcount keeps a variable-referenced cursor alive).
        if (removed.VariableRefCount == 0)
            DestroyCursor(batch, removed);
        batch.Connection.LastCursorRows = 0;
    }

    /// <summary>
    /// Parses and runs <c>FETCH [&lt;direction&gt; [&lt;n&gt;]] [FROM]
    /// &lt;cursor&gt; [INTO @v [, @v]…]</c>. With INTO, assigns the projected
    /// columns to the variables (Msg 16924 on a count mismatch) and yields no
    /// result set; without INTO, yields a single-row result set when the FETCH
    /// lands on a row. Sets <c>@@FETCH_STATUS</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ParseFetchCursor(BatchContext batch)
    {
        var context = batch.Parser;
        var connection = context.Connection;
        context.MoveNextRequired(); // consume FETCH

        var direction = FetchDirection.Next;
        long offset = 0;
        UnquotedString? directionToken = null;
        if (context.Token is UnquotedString dirToken && TryParseFetchDirection(dirToken.Span, out direction))
        {
            directionToken = dirToken;
            context.MoveNextRequired();
            if (direction is FetchDirection.Absolute or FetchDirection.Relative)
            {
                var offsetExpr = Expression.Parse(context);
                if (!batch.IsSkipping)
                {
                    var offsetValue = offsetExpr.Run(new RuntimeContext(NoColumnResolver, batch));
                    offset = offsetValue.IsNull ? 0 : offsetValue.CoerceTo(SqlType.BigInt).AsInt64;

                    // An offset *literal* outside int range is a grammar-level
                    // failure (Msg 1080, class 15) where the same value through
                    // a variable is accepted and simply positions past the end
                    // — probe-confirmed 2026-07-31.
                    if (offsetExpr is Value && offset is < int.MinValue or > int.MaxValue)
                        throw SimulatedSqlException.IntegerValueOutOfRange(offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            context.MoveNextRequired();
        else if (directionToken is not null)
            throw SimulatedSqlException.SyntaxErrorNear(directionToken); // a direction takes FROM (probed 2026-10-02)

        var reference = ReadCursorReference(context);

        List<string>? intoVariables = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Into })
        {
            intoVariables = [];
            do
            {
                if (context.GetNextRequired() is not AtPrefixedString variable)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                intoVariables.Add(variable.Value);
                context.MoveNextOptional();
            } while (context.Token is Operator { Character: ',' });
        }

        if (batch.IsSkipping)
            yield break;

        var cursor = ResolveCursor(batch, reference, missingAtPriorLine: true);
        batch.BeginImplicitTransaction();

        // The INTO-list cardinality check fires regardless of whether the
        // FETCH lands on a row (probe-confirmed Msg 16924).
        if (intoVariables is not null && intoVariables.Count != cursor.Selection.Schema.Length)
            throw SimulatedSqlException.CursorFetchVariableCountMismatch();

        // A fetch that fails — a direction the cursor refuses, a row whose
        // projection raises — reads -1 afterwards (probed 2026-10-02 against
        // SQL Server 2025).
        int status;
        SqlValue[]? values;
        try
        {
            (status, values) = cursor.Fetch(batch, direction, offset);
        }
        catch (SimulatedSqlException)
        {
            connection.LastFetchStatus = -1;
            throw;
        }
        // A principal without UNMASK fetches masked values, as its SELECT
        // would read them (probed 2026-09-27 against SQL Server 2025).
        if (values is not null && status == 0 && DataMasking.Applying(batch, cursor.Selection.ColumnMasks) is { } masking)
            values = DataMasking.MaskRowForStorage(values, masking, cursor.Selection.Schema);
        connection.LastFetchStatus = status;
        connection.LastStatementRowCount = status == 0 ? 1 : 0;
        if (status == -2)
            values = cursor.DeletedMemberValues();

        if (intoVariables is not null)
        {
            // Variables are written by a fetch that lands on a row, even a
            // deleted keyset member's zero-and-NULL row; on -1 (past end)
            // they retain their prior value (probe-confirmed).
            if (values is not null)
            {
                for (var i = 0; i < intoVariables.Count; i++)
                {
                    if (!batch.Variables.TryGetValue(intoVariables[i], out var slot))
                        throw SimulatedSqlException.MustDeclareScalarVariable(intoVariables[i]);
                    slot.Value = Cast.ApplyCoercion(values[i], slot.DeclaredType, slot.DeclaredMaxLength);
                }
            }
            yield break;
        }

        // No INTO: every fetch answers a result set, empty past either end.
        yield return cursor.FetchResult(values is null ? [] : [Cursor.WithRowStat(values, status == 0 ? 1 : 2)]);
    }

    /// <summary>
    /// A parsed cursor reference at a use site (<c>OPEN</c> / <c>CLOSE</c> /
    /// <c>DEALLOCATE</c> / <c>FETCH … FROM</c> / <c>WHERE CURRENT OF</c>):
    /// either a named cursor (optionally <c>GLOBAL</c>-qualified) or a cursor
    /// variable (<c>@c</c>). Names are stored with the leading <c>@</c> stripped
    /// for variables.
    /// </summary>
    private readonly struct CursorReference(string name, bool isVariable, bool globalQualified)
    {
        public readonly string Name = name;
        public readonly bool IsVariable = isVariable;
        public readonly bool GlobalQualified = globalQualified;
    }

    /// <summary>
    /// Reads a cursor reference at the current position — <c>@c</c> (variable),
    /// <c>GLOBAL name</c>, or bare <c>name</c> — advancing past it.
    /// </summary>
    private static CursorReference ReadCursorReference(ParserContext context)
    {
        if (context.Token is AtPrefixedString variable)
        {
            context.MoveNextOptional();
            return new CursorReference(variable.Value, isVariable: true, globalQualified: false);
        }
        var globalQualified = IsWord(context.Token, "GLOBAL");
        if (globalQualified)
            context.MoveNextRequired();
        if (context.Token is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new CursorReference(name.Value, isVariable: false, globalQualified);
    }

    /// <summary>
    /// Resolves a parsed <see cref="CursorReference"/> to its live
    /// <see cref="Cursor"/>. A variable reference reads
    /// <see cref="BatchContext.CursorVariables"/> (Msg 16950 when unallocated).
    /// A named reference resolves LOCAL-first then GLOBAL when unqualified, or
    /// GLOBAL-only when <c>GLOBAL</c>-qualified (Msg 16916 on a miss).
    /// </summary>
    /// <remarks>
    /// <c>OPEN</c> and <c>FETCH</c> report a missing cursor or an unallocated
    /// variable at the line of the statement that ran before them, where
    /// <c>CLOSE</c> and <c>DEALLOCATE</c> report their own line (probed
    /// 2026-09-29 against SQL Server 2025); <paramref name="missingAtPriorLine"/>
    /// says which.
    /// </remarks>
    private static Cursor ResolveCursor(BatchContext batch, CursorReference reference, bool missingAtPriorLine = false) =>
        reference.IsVariable
            ? batch.CursorVariables.TryGetValue(reference.Name, out var bound) && bound is not null
                ? bound
                : missingAtPriorLine
                    ? throw AtPriorStatementLine(batch, SimulatedSqlException.CursorVariableNotAllocated(reference.Name))
                    : throw SimulatedSqlException.CursorVariableNotAllocated(reference.Name)
            : !reference.GlobalQualified && batch.LocalCursors.TryGetValue(reference.Name, out var local)
                ? local
                : batch.Connection.Cursors.TryGetValue(reference.Name, out var global)
                    ? global
                    : missingAtPriorLine
                        ? throw AtPriorStatementLine(batch, SimulatedSqlException.CursorDoesNotExist(reference.Name))
                        : throw SimulatedSqlException.CursorDoesNotExist(reference.Name);

    /// <summary>
    /// Stamps <paramref name="error"/> with the line of the statement that ran
    /// before this one, 0 when none did — where real reports a missing cursor
    /// or an unallocated cursor variable met by <c>OPEN</c> or <c>FETCH</c>,
    /// unless the statement before failed, which leaves the error at its own
    /// line (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException AtPriorStatementLine(BatchContext batch, SimulatedSqlException error)
    {
        if (batch.CurrentStatement.PriorStatementLine < 0)
            return error;
        if (batch.CurrentStatement.PriorStatementLine == 0)
            return error.PinLine(0);
        foreach (var entry in error.Errors)
            entry.LineNumber = batch.CurrentStatement.PriorStatementLine;
        return error;
    }

    /// <summary>
    /// Registers a freshly-declared named cursor in its scope — the batch/proc-
    /// local map (<paramref name="local"/> true) or the connection-global map.
    /// Msg 16915 when a cursor of that name already exists in the same scope
    /// (LOCAL and GLOBAL are independent namespaces — a name may exist in both).
    /// </summary>
    private static void DeclareCursorInScope(BatchContext batch, string name, Cursor cursor, bool local)
    {
        var scope = local ? batch.LocalCursors : batch.Connection.Cursors;
        if (scope.ContainsKey(name))
            throw SimulatedSqlException.CursorAlreadyExists(name);
        scope[name] = cursor;
        if (!local)
            (batch.Connection.CursorsDeclaredInExecution ??= []).Add(cursor);
    }

    /// <summary>
    /// Tears a cursor down when its last reference goes away: releases any
    /// SCROLL_LOCKS locks it holds. Closing the materialized state is implicit
    /// (the object becomes unreachable). Idempotent for a closed cursor.
    /// </summary>
    private static void DestroyCursor(BatchContext batch, Cursor cursor) =>
        cursor.ReleaseScrollLocks(batch.Connection);

    /// <summary>
    /// Drops one cursor-variable reference to <paramref name="cursor"/>: if the
    /// decrement leaves an unnamed cursor with no remaining references, the
    /// object is destroyed (SCROLL_LOCKS locks released). Named cursors persist
    /// through their name binding regardless of the count.
    /// </summary>
    private static void ReleaseVariableReference(BatchContext batch, Cursor cursor)
    {
        cursor.VariableRefCount--;
        if (cursor.VariableRefCount <= 0 && cursor.IsUnnamed)
            DestroyCursor(batch, cursor);
    }

    /// <summary>
    /// Resolves a named cursor (LOCAL-first then GLOBAL) for binding to a cursor
    /// variable via <c>SET @c = named_cursor</c>. Msg 16916 on a miss.
    /// </summary>
    private static Cursor ResolveNamedCursor(BatchContext batch, string name) =>
        batch.LocalCursors.TryGetValue(name, out var local)
            ? local
            : batch.Connection.Cursors.TryGetValue(name, out var global)
                ? global
                : throw SimulatedSqlException.CursorDoesNotExist(name);

    /// <summary>
    /// Rebinds cursor variable <paramref name="variableName"/> to
    /// <paramref name="newCursor"/>: releases the reference it previously held
    /// and increments the new cursor's refcount. Assigning the same cursor a
    /// variable already holds is a no-op net of the release/re-take.
    /// </summary>
    private static void RebindCursorVariable(BatchContext batch, string variableName, Cursor? newCursor)
    {
        if (batch.CursorVariables.TryGetValue(variableName, out var previous) && previous is not null)
            ReleaseVariableReference(batch, previous);
        if (newCursor is not null)
            newCursor.VariableRefCount++;
        batch.CursorVariables[variableName] = newCursor;
    }

    /// <summary>
    /// Frame-exit teardown of the batch / procedure / trigger cursor scope:
    /// LOCAL named cursors and cursor variables are implicitly deallocated when
    /// their frame ends (GLOBAL cursors, living on the connection, are
    /// untouched). Each cursor-variable reference is dropped; each LOCAL cursor
    /// with no surviving variable reference is destroyed (releasing SCROLL_LOCKS
    /// locks). A LOCAL cursor handed out through a cursor OUTPUT parameter has a
    /// non-zero refcount here, so it survives into the caller's variable.
    /// </summary>
    internal static void TeardownFrameCursors(BatchContext batch)
    {
        foreach (var bound in batch.CursorVariables.Values)
        {
            if (bound is not null)
                ReleaseVariableReference(batch, bound);
        }
        batch.CursorVariables.Clear();

        foreach (var cursor in batch.LocalCursors.Values)
        {
            if (cursor.VariableRefCount == 0)
                DestroyCursor(batch, cursor);
        }
        batch.LocalCursors.Clear();
    }

    /// <summary>
    /// Parses the <c>CURRENT OF &lt;cursor&gt;</c> tail of a positioned
    /// <c>WHERE CURRENT OF</c> clause (cursor on entry: the <c>CURRENT</c>
    /// keyword) and validates the cursor against the DML target
    /// <paramref name="table"/>: Msg 16929 when the cursor is read-only,
    /// Msg 16933 when the cursor doesn't read that table (or a
    /// <c>FOR UPDATE OF</c> list names none of its columns), Msg 16931 when it
    /// isn't positioned on a live row (before the first FETCH or past the end),
    /// Msg 16947 when the table's slot is the NULL-extended side of an outer
    /// join or the cursor sits on a keyset hole, Msg 16932 when a positioned UPDATE
    /// assigns a column outside the cursor's <c>FOR UPDATE OF</c> list
    /// (<paramref name="assignedColumns"/>, null for DELETE), and the
    /// optimistic-conflict chain (Msg 16947 + 16934) when an OPTIMISTIC cursor's
    /// current row was modified out-of-band. Returns the validated cursor and
    /// the identity slot whose address in <see cref="Cursor.CurrentRids"/>
    /// identifies the row to mutate, or <see langword="null"/> in skip mode:
    /// the cursor is looked up when the statement runs, so compiling a batch
    /// that declares it doesn't find it missing.
    /// </summary>
    /// <remarks>
    /// The target is matched by the reference <em>as written</em>:
    /// <paramref name="sourceView"/> is the view the statement named (the
    /// UPDATE / DELETE parsers already resolved it to its base
    /// <paramref name="table"/>), and it must equal the view the cursor's own
    /// FROM was written through. Probe-confirmed against SQL Server 2025: a
    /// cursor over <c>v</c> accepts <c>UPDATE v … WHERE CURRENT OF</c> and
    /// reports Msg 16933 for the base table under it — and for the inner view
    /// of a view-over-view — while a derived table or CTE is transparent and
    /// the statement must name the base table.
    /// </remarks>
    internal static PositionedCursorTarget? ParseWhereCurrentOf(
        ParserContext context,
        HeapTable table,
        IReadOnlyList<string>? assignedColumns = null,
        View? sourceView = null)
    {
        context.MoveNextRequired(); // consume CURRENT
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Of })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired(); // consume OF
        var reference = ReadCursorReference(context);

        var batch = context.Batch;
        if (batch.IsSkipping)
            return null;
        var cursor = ResolveCursor(batch, reference);
        if (cursor.ReadOnly)
            throw SimulatedSqlException.CursorIsReadOnly();

        // The DML target must be one of the cursor's own FROM sources — real
        // resolves positioned DML against the cursor's tables, so naming an
        // unrelated table (or the base table behind a view the cursor reads) is
        // Msg 16933, not "no current row". A FOR UPDATE OF list further narrows
        // the updatable tables to those owning a listed column.
        var slot = cursor.IndexOfTarget(table, sourceView);
        if (slot < 0 || !cursor.IsSlotUpdatable(slot, batch))
            throw SimulatedSqlException.CursorTableNotIncluded();

        // A self-join reaches the same table through several slots; real binds
        // the first one and says so through the severity-0 Msg 16961.
        if (cursor.CountTarget(table, sourceView) > 1)
            batch.AppendInfoError(@class: 0, state: 1, number: 16961, "One or more FOR UPDATE columns have been adjusted to the first instance of their table in the query.");

        // Off a live row: real splits by cause (probe-confirmed). Before the
        // first FETCH or past the end is Msg 16931 "no rows in the current
        // fetch buffer"; a keyset hole — the last FETCH reported -2 because the
        // member was deleted or its key changed — is Msg 16947 + 3621, since
        // the row the statement means is simply gone.
        if (cursor.CurrentRids is not { } rids)
        {
            throw cursor.OnKeysetHole
                ? SimulatedSqlException.CursorNoRowsAffected()
                : SimulatedSqlException.CursorNoCurrentRow();
        }
        // The cursor is on a row, but this table's slot is NULL-extended (the
        // unmatched side of an outer join), so there is nothing to mutate.
        if (rids[slot] is null)
            throw SimulatedSqlException.CursorNoRowsAffected();

        // FOR UPDATE OF (…): every assigned column must be in the list.
        if (assignedColumns is not null)
        {
            foreach (var column in assignedColumns)
            {
                if (!cursor.IsColumnUpdatable(column, batch))
                    throw SimulatedSqlException.CursorColumnNotInForUpdateList();
            }
        }

        // OPTIMISTIC: raise the conflict chain if the row changed since fetch.
        cursor.CheckOptimisticConflict(batch);
        cursor.NotePositionedWrite(delete: assignedColumns is null);
        return new PositionedCursorTarget(cursor, slot);
    }

    /// <summary>
    /// True when the row at <paramref name="rid"/> is the one the positioned
    /// cursor is sitting on — i.e. the stable address recorded for the bound
    /// identity slot in <see cref="Cursor.CurrentRids"/>.
    /// </summary>
    internal static bool CursorRowMatches(PositionedCursorTarget target, (int Page, int Slot) rid) =>
        target.Cursor.CurrentRids is { } rids && rids[target.Slot] is { } current && current.Equals(rid);

    private static readonly (string Word, FetchDirection Direction)[] FetchDirectionWords =
    [
        ("NEXT", FetchDirection.Next),
        ("PRIOR", FetchDirection.Prior),
        ("FIRST", FetchDirection.First),
        ("LAST", FetchDirection.Last),
        ("ABSOLUTE", FetchDirection.Absolute),
        ("RELATIVE", FetchDirection.Relative),
    ];

    private static bool TryParseFetchDirection(ReadOnlySpan<char> span, out FetchDirection direction)
    {
        foreach (var (word, dir) in FetchDirectionWords)
        {
            if (span.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                direction = dir;
                return true;
            }
        }
        direction = FetchDirection.Next;
        return false;
    }

    private static bool IsWord(Token? token, string word) =>
        token is UnquotedString u && u.Span.Equals(word, StringComparison.OrdinalIgnoreCase);
}
