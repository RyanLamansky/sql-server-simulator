using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Frozen;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// Computes <c>OBJECTPROPERTY(id, 'IsDeterministic')</c> for the module kinds
/// real SQL Server answers it for — views, scalar functions, inline TVFs and
/// multi-statement TVFs. Procedures, triggers, tables, sequences and synonyms
/// get NULL from the caller instead (probe-confirmed).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> Probe-confirmed against SQL Server 2025, a module is
/// deterministic when all three hold:
/// </para>
/// <list type="number">
/// <item><description>it was declared <c>WITH SCHEMABINDING</c> — a module
/// with a perfectly deterministic body reports 0 without it, so schema-binding
/// is a precondition rather than a contributing signal;</description></item>
/// <item><description>its body reaches no nondeterministic built-in (see
/// <see cref="NondeterministicBuiltIns"/>);</description></item>
/// <item><description>every module it references — user function, view or
/// TVF — is itself deterministic, transitively.</description></item>
/// </list>
/// <para>
/// Reading a table is deterministic (probe-confirmed: a schema-bound function
/// doing <c>SELECT COUNT(*) FROM dbo.t</c> reports 1), as are aggregates,
/// window functions and <c>TOP</c> without <c>ORDER BY</c>.
/// </para>
/// <para>
/// <b>Body walk.</b> Scalar-function and multi-statement-TVF bodies are stored
/// as source text and re-parsed per call, so there is no expression tree to
/// visit at CREATE time. The analysis therefore re-tokenizes the stored body
/// and scans the token stream, which reaches all four module kinds through one
/// mechanism. The scan runs per <c>OBJECTPROPERTY</c> read rather than being
/// cached at CREATE, so a referenced module that was later redefined can't
/// leave a stale answer behind — which takes a drop and recreate, since
/// altering it in place while the referencing module stands is Msg 3729 (see
/// <see cref="SchemaBinding"/>).
/// <see cref="SchemaBinding"/> walks the same token stream for the dependency
/// side; the two stay separate because this one bails at the first
/// nondeterministic built-in and needs no column or FROM-position detail.
/// </para>
/// <para>
/// <b>Conversions.</b> Real also classifies a <c>CAST</c> / <c>CONVERT</c>
/// between a date/time type and a character string as nondeterministic unless
/// an explicit style from the deterministic set is supplied; that half of the
/// rule lives in <c>ModuleDeterminism.Conversions.cs</c>, which carries both
/// the probed style set and what the token scan can and can't decide about the
/// converted expression's own type.
/// </para>
/// </remarks>
internal static partial class ModuleDeterminism
{
    /// <summary>
    /// Built-in scalar functions real SQL Server classifies nondeterministic,
    /// restricted to names the simulator's own <c>ResolveBuiltIn</c> catalog
    /// recognizes. Every entry was probed by wrapping the call in a
    /// <c>WITH SCHEMABINDING</c> scalar function and reading
    /// <c>OBJECTPROPERTY(…, 'IsDeterministic')</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The families: current-time readers, session / connection state, the
    /// server- and database-metadata lookups (a metadata answer can change
    /// without the module changing), the security and principal scalars, the
    /// <c>ERROR_*</c> family, and the language-dependent formatters
    /// (<c>FORMAT</c>, <c>DATENAME</c>, <c>ISDATE</c>, <c>PARSE</c>).
    /// </para>
    /// <para>
    /// Deliberate absences, all probe-confirmed <i>deterministic</i> despite
    /// looking otherwise: <c>CHECKSUM</c> / <c>BINARY_CHECKSUM</c> /
    /// <c>HASHBYTES</c>, <c>QUOTENAME</c>, <c>MIN_ACTIVE_ROWVERSION</c>,
    /// <c>DECOMPRESS</c>, <c>APPROX_COUNT_DISTINCT</c>, <c>ISNUMERIC</c>,
    /// <c>TEXTPTR</c>, <c>DATEDIFF</c> / <c>DATEADD</c> / <c>DATETRUNC</c> /
    /// <c>DATE_BUCKET</c> / <c>EOMONTH</c>, <c>SWITCHOFFSET</c> /
    /// <c>TODATETIMEOFFSET</c>, and every window function. <c>COMPRESS</c>
    /// is nondeterministic while <c>DECOMPRESS</c> is not — probed, not a typo.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> NondeterministicBuiltIns = new[]
    {
        "APPLOCK_MODE",
        "APPLOCK_TEST",
        "APP_NAME",
        "ASSEMBLYPROPERTY",
        "CERTENCODED",
        "CERTPRIVATEKEY",
        "COLLATIONPROPERTY",
        "COLUMNPROPERTY",
        "COLUMNS_UPDATED",
        "COL_LENGTH",
        "COL_NAME",
        "COMPRESS",
        "CONNECTIONPROPERTY",
        "CONTEXT_INFO",
        "CURRENT_REQUEST_ID",
        "CURRENT_TRANSACTION_ID",
        "CURSOR_STATUS",
        "DATABASEPROPERTYEX",
        "DATABASE_PRINCIPAL_ID",
        "DATENAME",
        "DB_ID",
        "DB_NAME",
        "ERROR_LINE",
        "ERROR_MESSAGE",
        "ERROR_NUMBER",
        "ERROR_PROCEDURE",
        "ERROR_SEVERITY",
        "ERROR_STATE",
        "EVENTDATA",
        "FILEGROUPPROPERTY",
        "FILEGROUP_ID",
        "FILEGROUP_NAME",
        "FILEPROPERTY",
        "FILE_ID",
        "FILE_IDEX",
        "FILE_NAME",
        "FORMAT",
        "FORMATMESSAGE",
        "FULLTEXTCATALOGPROPERTY",
        "FULLTEXTSERVICEPROPERTY",
        "GETANSINULL",
        "GETDATE",
        "GETUTCDATE",
        "GET_FILESTREAM_TRANSACTION_CONTEXT",
        "HAS_DBACCESS",
        "HAS_PERMS_BY_NAME",
        "HOST_NAME",
        "IDENT_CURRENT",
        "IDENT_INCR",
        "IDENT_SEED",
        "INDEXKEY_PROPERTY",
        "INDEXPROPERTY",
        "INDEX_COL",
        "ISDATE",
        "IS_MEMBER",
        "IS_ROLEMEMBER",
        "IS_SRVROLEMEMBER",
        "LOGINPROPERTY",
        "NEWID",
        "NEWSEQUENTIALID",
        "OBJECTPROPERTY",
        "OBJECTPROPERTYEX",
        "OBJECT_DEFINITION",
        "OBJECT_ID",
        "OBJECT_NAME",
        "OBJECT_SCHEMA_NAME",
        "ORIGINAL_DB_NAME",
        "ORIGINAL_LOGIN",
        "PARSE",
        "PARSENAME",
        "PERMISSIONS",
        "PWDCOMPARE",
        "PWDENCRYPT",
        "RAND",
        "ROWCOUNT_BIG",
        "SCHEMA_ID",
        "SCHEMA_NAME",
        "SCOPE_IDENTITY",
        "SERVERPROPERTY",
        "SESSIONPROPERTY",
        "SESSION_CONTEXT",
        "SID_BINARY",
        "SQL_VARIANT_PROPERTY",
        "STATS_DATE",
        "SUSER_ID",
        "SUSER_NAME",
        "SUSER_SID",
        "SUSER_SNAME",
        "SYSDATETIME",
        "SYSDATETIMEOFFSET",
        "SYSUTCDATETIME",
        "TEXTVALID",
        "TRIGGER_NESTLEVEL",
        "TRY_PARSE",
        "TYPEPROPERTY",
        "TYPE_ID",
        "TYPE_NAME",
        "USER_ID",
        "USER_NAME",
        "XACT_STATE",
        "XML_SCHEMA_NAMESPACE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>DATEPART</c> units whose answer moves with <c>SET DATEFIRST</c>,
    /// which is what makes <c>DATEPART(week, …)</c> nondeterministic while
    /// <c>DATEPART(year, …)</c> and even <c>DATEPART(iso_week, …)</c> are not
    /// (all three probed). <c>DATENAME</c> needs no such split — it is
    /// language-dependent for every unit and sits in
    /// <see cref="NondeterministicBuiltIns"/> outright.
    /// </summary>
    private static readonly FrozenSet<string> DateFirstDependentDateParts =
        new[] { "dw", "w", "week", "weekday", "wk", "ww" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Answers <c>IsDeterministic</c> for <paramref name="module"/>, or
    /// <see langword="null"/> when the object isn't one real reports the
    /// property for.
    /// </summary>
    internal static int? Evaluate(Database database, SchemaObject module) =>
        module is View or UserDefinedFunction ? (IsDeterministic(database, module, []) ? 1 : 0) : null;

    /// <summary>
    /// Answers <c>IsSchemaBound</c>: 1 / 0 for the schema-bindable module
    /// kinds, 0 for a procedure (a module that can never carry the option),
    /// and NULL for everything else — probe-confirmed that a table, trigger,
    /// sequence and synonym all return NULL.
    /// </summary>
    internal static int? EvaluateSchemaBound(SchemaObject module) => module switch
    {
        View view => view.IsSchemaBound ? 1 : 0,
        UserDefinedFunction function => function.IsSchemaBound ? 1 : 0,
        Procedure => 0,
        _ => null,
    };

    /// <summary>
    /// Whether a computed column's expression is deterministic — the question
    /// <c>PERSISTED</c> asks, since a persisted value is stored once and read
    /// back forever. Runs the same pipeline a schema-bound module's body takes:
    /// the nondeterministic-built-in table, the <c>CAST</c> / <c>CONVERT</c>
    /// style rule (which is what makes <c>CONVERT(varchar(20), &lt;datetime
    /// col&gt;, 112)</c> persistable and style 0 not), and the transitive walk
    /// into referenced functions and views. <paramref name="scopeColumns"/> is
    /// the column list the expression's names bind against, which the style rule
    /// needs to type them.
    /// </summary>
    internal static bool IsComputedColumnDeterministic(Database database, HeapColumn[] scopeColumns, string definition)
    {
        if (!Scan(definition, out var tokens, out var referencedModules))
            return false;
        var families = NameFamilies(database, module: null, tokens, referencedModules);
        AddColumns(families, scopeColumns);
        if (!ConversionsAreDeterministic(tokens, families))
            return false;

        var visited = new HashSet<int>();
        foreach (var (qualifier, leaf) in referencedModules)
        {
            if (!database.Schemas.TryGetValue(qualifier, out var schema))
                continue;
            if (schema.Functions.TryGetValue(leaf, out var referencedFunction)
                && !IsDeterministic(database, referencedFunction, visited))
            {
                return false;
            }
            if (schema.Views.TryGetValue(leaf, out var referencedView)
                && !IsDeterministic(database, referencedView, visited))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDeterministic(Database database, SchemaObject module, HashSet<int> visited)
    {
        // A reference cycle can only be reached through a module the walk is
        // already proving; treating the revisit as deterministic lets the
        // outer frame's own verdict stand.
        if (!visited.Add(module.ObjectId))
            return true;

        string body;
        switch (module)
        {
            case View view:
                if (!view.IsSchemaBound)
                    return false;
                body = view.BodyText;
                break;
            // A CLR function's determinism comes from its method's
            // SqlFunction(IsDeterministic:) attribute, which isn't modeled;
            // report the attribute's own default.
            case ClrScalarFunction:
                return false;
            case UserDefinedFunction function:
                if (!function.IsSchemaBound)
                    return false;
                body = function.BodyText;
                break;
            default:
                return false;
        }

        if (!Scan(body, out var tokens, out var referencedModules)
            || !ConversionsAreDeterministic(tokens, NameFamilies(database, module, tokens, referencedModules)))
        {
            return false;
        }

        foreach (var (qualifier, leaf) in referencedModules)
        {
            if (!database.Schemas.TryGetValue(qualifier, out var schema))
                continue;
            if (schema.Functions.TryGetValue(leaf, out var referencedFunction)
                && !IsDeterministic(database, referencedFunction, visited))
            {
                return false;
            }
            if (schema.Views.TryGetValue(leaf, out var referencedView)
                && !IsDeterministic(database, referencedView, visited))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Re-tokenizes <paramref name="body"/> and reports whether it stayed
    /// clear of nondeterministic built-ins, collecting the qualified names it
    /// mentions into <paramref name="referencedModules"/> as
    /// (immediate qualifier, leaf) pairs for the transitive walk.
    /// </summary>
    /// <remarks>
    /// Only qualified names are collected: SQL Server requires a schema
    /// qualifier on every user-function call, and a view or TVF can only be
    /// reached through one too. Pairs that resolve to nothing (a table alias
    /// in <c>t.col</c>, a base table in <c>FROM dbo.t</c>) are dropped by the
    /// caller's lookup. The body always tokenizes — the CREATE-time parser
    /// walked the same text to find its end.
    /// </remarks>
    private static bool Scan(string body, out List<Token> tokens, out List<(string Qualifier, string Leaf)> referencedModules)
    {
        referencedModules = [];
        tokens = [];
        var index = 0;
        while (Tokenizer.NextToken(body, ref index, Collation.Baseline) is { } token)
        {
            if (token is not (Whitespace or Comment))
                tokens.Add(token);
        }

        var lookup = NondeterministicBuiltIns.GetAlternateLookup<ReadOnlySpan<char>>();
        var dateParts = DateFirstDependentDateParts.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                // Every @@-constant reads session or server state — probed
                // across @@SPID / @@ROWCOUNT / @@ERROR / @@TRANCOUNT /
                // @@NESTLEVEL / @@VERSION / @@SERVERNAME / @@DBTS /
                // @@IDENTITY / @@LANGID / @@DATEFIRST, all nondeterministic.
                case DoubleAtPrefixedString:
                // The niladic keyword forms, which take no argument list.
                case ReservedKeyword
                {
                    Keyword: Keyword.Current_Date or Keyword.Current_Time or Keyword.Current_Timestamp
                        or Keyword.Current_User or Keyword.Session_User or Keyword.System_user or Keyword.User,
                }:
                    return false;
                // `expr AT TIME ZONE 'name'` reads the server's time-zone
                // table, which real treats as nondeterministic (probed 0)
                // even though SWITCHOFFSET / TODATETIMEOFFSET are not.
                case UnquotedString { ContextualKeyword: ContextualKeyword.At }
                    when i + 2 < tokens.Count
                        && tokens[i + 1] is UnquotedString { ContextualKeyword: ContextualKeyword.Time }
                        && tokens[i + 2] is UnquotedString { ContextualKeyword: ContextualKeyword.Zone }:
                    return false;
                case Name name:
                    {
                        // Walk the dotted chain `a[.b[.c[.d]]]` to its leaf.
                        var leafIndex = i;
                        while (leafIndex + 2 < tokens.Count
                            && tokens[leafIndex + 1] is Operator { Character: '.' }
                            && tokens[leafIndex + 2] is Name)
                        {
                            leafIndex += 2;
                        }
                        if (leafIndex > i)
                        {
                            referencedModules.Add((
                                ((Name)tokens[leafIndex - 2]).Value,
                                ((Name)tokens[leafIndex]).Value));
                        }
                        else if (leafIndex + 1 < tokens.Count && tokens[leafIndex + 1] is Operator { Character: '(' })
                        {
                            // A `type::Method(…)` static call is not the built-in
                            // of the same name — real persists a computed
                            // `geography::Parse('POINT(0 0)')` while refusing the
                            // scalar `PARSE(s AS int)` (probe-confirmed).
                            if (i > 0 && tokens[i - 1] is Operator { Character: ':' })
                                break;
                            // An unqualified call is always a built-in: real
                            // rejects a bare user-function call outright.
                            if (lookup.Contains(name.Span))
                                return false;
                            if (name.Span.Equals("DATEPART", StringComparison.OrdinalIgnoreCase)
                                && leafIndex + 2 < tokens.Count
                                && tokens[leafIndex + 2] is Name unit
                                && dateParts.Contains(unit.Span))
                            {
                                return false;
                            }
                        }
                        i = leafIndex;
                        break;
                    }
            }
        }
        return true;
    }
}
