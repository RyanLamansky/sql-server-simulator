using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Closed-list <c>SET</c> session / connection / planner options —
/// parse-and-discard for grammar compatibility (no underlying state
/// modeling). Probe-confirmed verbatim against SQL Server 2025
/// (2026-05-14): unknown option name followed by ON/OFF/value raises
/// Msg 195; unknown name with nothing parseable after falls through
/// to the generic Msg 102. <c>SET @v = expr</c> / <c>SET IDENTITY_INSERT</c>
/// have semantic effect and are tested elsewhere.
/// </summary>
[TestClass]
public sealed class SetOptionTests
{
    private static int RunBatch(string commandText) => new Simulation().ExecuteNonQuery(commandText);

    [TestMethod]
    // Bool toggles — every entry in the OnOff family of the closed list.
    [DataRow("SET ANSI_NULLS ON")]
    [DataRow("SET ANSI_NULLS OFF")]
    [DataRow("SET QUOTED_IDENTIFIER ON")]
    [DataRow("SET ANSI_WARNINGS ON")]
    [DataRow("SET ANSI_PADDING ON")]
    [DataRow("SET CONCAT_NULL_YIELDS_NULL ON")]
    [DataRow("SET NUMERIC_ROUNDABORT OFF")]
    [DataRow("SET ARITHABORT ON")]
    [DataRow("SET ARITHIGNORE OFF")]
    [DataRow("SET XACT_ABORT ON")]
    [DataRow("SET FMTONLY OFF")]
    [DataRow("SET NOEXEC OFF")]
    [DataRow("SET FORCEPLAN OFF")]
    [DataRow("SET PARSEONLY OFF")]
    [DataRow("SET CURSOR_CLOSE_ON_COMMIT OFF")]
    [DataRow("SET ANSI_DEFAULTS ON")]
    [DataRow("SET REMOTE_PROC_TRANSACTIONS ON")]
    [DataRow("SET NO_BROWSETABLE OFF")]
    [DataRow("SET SHOWPLAN_TEXT OFF")]
    [DataRow("SET SHOWPLAN_ALL OFF")]
    [DataRow("SET SHOWPLAN_XML OFF")]
    [DataRow("SET DISABLE_DEF_CNST_CHK ON")]
    [DataRow("SET NOCOUNT ON")]
    [DataRow("SET IMPLICIT_TRANSACTIONS OFF")]
    // Multi-option comma form — OnOff-restricted. The five-toggle row is the
    // canonical EF Core SqlServer-provider session-bootstrap shape and was the
    // original motivating case for the closed-list parser.
    [DataRow("SET ANSI_NULLS, QUOTED_IDENTIFIER, CONCAT_NULL_YIELDS_NULL, ANSI_WARNINGS, ANSI_PADDING ON")]
    [DataRow("SET ANSI_NULLS, QUOTED_IDENTIFIER OFF")]
    // Integer-value options (ROWCOUNT / TEXTSIZE tokenize as ReservedKeyword and
    // dispatch through the separate switch arm; the others come through
    // UnquotedString → closed-list lookup).
    [DataRow("SET LOCK_TIMEOUT 5000")]
    [DataRow("SET TEXTSIZE 4096")]
    [DataRow("SET DATEFIRST 7")]
    [DataRow("SET ROWCOUNT 100")]
    [DataRow("SET QUERY_GOVERNOR_COST_LIMIT 1000")]
    // Identifier-value options. LANGUAGE accepts both bare identifier and
    // quoted-string literal forms.
    [DataRow("SET DATEFORMAT mdy")]
    [DataRow("SET LANGUAGE us_english")]
    [DataRow("SET LANGUAGE N'us_english'")]
    // IntegerOrIdent options.
    [DataRow("SET DEADLOCK_PRIORITY LOW")]
    [DataRow("SET DEADLOCK_PRIORITY 5")]
    // Binary value.
    [DataRow("SET CONTEXT_INFO 0x12345678")]
    // SET TRANSACTION ISOLATION LEVEL — all five levels.
    [DataRow("SET TRANSACTION ISOLATION LEVEL READ COMMITTED")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL SNAPSHOT")]
    [DataRow("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE")]
    // SET STATISTICS sub-form.
    [DataRow("SET STATISTICS IO ON")]
    [DataRow("SET STATISTICS TIME OFF")]
    [DataRow("SET STATISTICS XML OFF")]
    [DataRow("SET STATISTICS PROFILE OFF")]
    public void Accepted_NoOp(string sql) => AreEqual(-1, RunBatch(sql));

    [TestMethod]
    [DataRow("SET BANANA ON", "BANANA")]
    [DataRow("SET ANSI_NULLS, BANANA, QUOTED_IDENTIFIER ON", "BANANA")]
    public void UnknownOption_RaisesMsg195(string sql, string unrecognizedName)
        => new Simulation().AssertSqlError(sql, 195, $"'{unrecognizedName}' is not a recognized SET option.");

    /// <summary>
    /// Msg 195's state says what followed the name: 5 for ON / OFF, 7 for a
    /// value (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("SET BANANA ON", 5)]
    [DataRow("SET BANANA OFF", 5)]
    [DataRow("SET ANSI_NULLS, BANANA ON", 5)]
    [DataRow("SET BANANA 5", 7)]
    [DataRow("SET BANANA -1", 7)]
    [DataRow("SET BANANA 'x'", 7)]
    [DataRow("SET BANANA foo", 7)]
    public void UnknownOption_StateSaysWhatFollowed(string sql, int state)
        => AreEqual((byte)state, new Simulation().AssertSqlError(sql, 195).State);

    [TestMethod]
    public void UnknownOption_NoTrailingTokens_RaisesMsg102()
    {
        // Probe-confirmed: SQL Server returns the generic Msg 102 here
        // (no dedicated Msg 195 because there's nothing to disambiguate
        // SET option from arbitrary token sequence).
        var ex = new Simulation().AssertSqlError("SET BANANA", 102);
        Contains("BANANA", ex.Message);
    }

    [TestMethod]
    public void Composed_WithSubsequentStatement_AcceptsBoth()
    {
        // The session-bootstrap-then-real-query pattern: SET options first,
        // then a SELECT. Statement-boundary handling must let the SET parser
        // hand off cleanly.
        AreEqual(1, new Simulation().ExecuteScalar("SET ANSI_NULLS ON; SELECT 1"));
    }

    [TestMethod]
    public void LockTimeout_NegativeOne_ParsesAndReadsBack()
        => AreEqual(-1, new Simulation().ExecuteScalar("SET LOCK_TIMEOUT 5000 SET LOCK_TIMEOUT -1 SELECT @@LOCK_TIMEOUT"));

    [TestMethod]
    public void Textsize_NegativeOne_Parses()
        => AreEqual(1, new Simulation().ExecuteScalar("SET TEXTSIZE -1 SELECT 1"));

    [TestMethod]
    public void SmoScriptingPreamble_Parses()
    {
        // The exact semicolon-less SET barrage SMO's scripting connection
        // sends before Script-As (harvested from the SSMS shakedown,
        // 2026-07-16): signed LOCK_TIMEOUT and ANSI_NULL_DFLT_ON were the
        // two gaps it surfaced.
        AreEqual(1, new Simulation().ExecuteScalar(
            "SET ROWCOUNT 0 SET TEXTSIZE 2147483647 SET NOCOUNT OFF SET CONCAT_NULL_YIELDS_NULL ON SET ARITHABORT ON"
            + " SET LOCK_TIMEOUT -1 SET QUERY_GOVERNOR_COST_LIMIT 0 SET DEADLOCK_PRIORITY NORMAL"
            + " SET TRANSACTION ISOLATION LEVEL READ COMMITTED  SET ANSI_NULLS ON SET ANSI_NULL_DFLT_ON ON"
            + " SET ANSI_PADDING ON SET ANSI_WARNINGS ON SET CURSOR_CLOSE_ON_COMMIT OFF SET IMPLICIT_TRANSACTIONS OFF"
            + " SET QUOTED_IDENTIFIER ON SELECT 1"));
    }
    // ---- probed 2026-10-02 against SQL Server 2025 ----

    [TestMethod]
    public void SetOfAnAtAtName_Msg137()
    {
        var ex = new Simulation().AssertSqlError("set @@x = 1", 137);
        AreEqual("Must declare the scalar variable \"@@x\".", ex.Errors[0].Message);
        AreEqual((byte)1, ex.Errors[0].State);
    }

    [TestMethod]
    [DataRow("set nocount maybe", "nocount")]
    [DataRow("set ansi_nulls 5", "ansi_nulls")]
    [DataRow("set xact_abort 'on'", "xact_abort")]
    public void OnOffOptionGivenAnotherValue_Msg102State4(string commandText, string option)
    {
        var ex = new Simulation().AssertSqlError(commandText, 102);
        AreEqual($"Incorrect syntax near '{option}'.", ex.Errors[0].Message);
        AreEqual((byte)4, ex.Errors[0].State);
    }

    [TestMethod]
    public void NegatingTheIntMinimum_UnderAnsiWarningsOff_AnswersNullWithMsg3606()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SimulatedError>().Select(error => error.Number));
        AreEqual(DBNull.Value, connection.CreateCommand("set ansi_warnings off; set arithabort off; select -cast(-2147483648 as int)").ExecuteScalar());
        CollectionAssert.AreEqual(new[] { 3606 }, messages);
    }

    /// <summary>
    /// A comma list sets every option it names, NOEXEC among them last, and
    /// STATISTICS takes a list too (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CommaList_SetsEveryOption()
    {
        var sim = new Simulation();
        AreEqual(5432 + 512 + 16384, sim.ExecuteScalar("set nocount, xact_abort on; select @@options"));
        AreEqual(5432 + 512, sim.ExecuteScalar("set noexec, nocount on; select 1; set noexec off; select @@options"));
        _ = sim.ExecuteNonQuery("set statistics io, time on; set statistics io, time off");
    }

    /// <summary>
    /// The value-taking options' refusals (probed 2026-10-04 against SQL
    /// Server 2025): a grammar with no slot for the value is Msg 102 — at the
    /// option's name in upper case, state 3, for LOCK_TIMEOUT and
    /// QUERY_GOVERNOR_COST_LIMIT — and a value of the wrong kind Msg 507 /
    /// 2743 / 2755, which end only the statement.
    /// </summary>
    [TestMethod]
    [DataRow("declare @v int = 3; set textsize @v", 102, (byte)1, "Incorrect syntax near '@v'.")]
    [DataRow("set lock_timeout '10'", 102, (byte)3, "Incorrect syntax near 'LOCK_TIMEOUT'.")]
    [DataRow("declare @v int = 3; set lock_timeout @v", 102, (byte)3, "Incorrect syntax near 'LOCK_TIMEOUT'.")]
    [DataRow("set lock_timeout 2147483648", 102, (byte)3, "Incorrect syntax near 'LOCK_TIMEOUT'.")]
    [DataRow("set query_governor_cost_limit 'x'", 102, (byte)3, "Incorrect syntax near 'QUERY_GOVERNOR_COST_LIMIT'.")]
    [DataRow("declare @s varchar(5) = '4'; set rowcount @s", 507, (byte)2, "Invalid argument for SET ROWCOUNT. Must be a non-null non-negative integer.")]
    [DataRow("declare @d decimal(5,2) = 2.5; set rowcount @d", 507, (byte)2, "Invalid argument for SET ROWCOUNT. Must be a non-null non-negative integer.")]
    [DataRow("set datefirst '3'", 2743, (byte)3, "SET DATEFIRST option requires integer parameter.")]
    [DataRow("set datefirst null", 2743, (byte)3, "SET DATEFIRST option requires integer parameter.")]
    [DataRow("set dateformat 1", 2743, (byte)2, "SET DATEFORMAT option requires character string parameter.")]
    [DataRow("set language null", 2743, (byte)2, "SET LANGUAGE option requires character string parameter.")]
    [DataRow("set deadlock_priority '5'", 2755, (byte)1, "SET DEADLOCK_PRIORITY option is invalid. Valid options are {HIGH | NORMAL | LOW | [-10 ... 10] of type integer}.")]
    [DataRow("set deadlock_priority null", 2755, (byte)1, "SET DEADLOCK_PRIORITY option is invalid. Valid options are {HIGH | NORMAL | LOW | [-10 ... 10] of type integer}.")]
    [DataRow("set @@rowcount = 5", 102, (byte)1, "Incorrect syntax near '@@rowcount'.")]
    [DataRow("set fips_flagger 'nope'", 102, (byte)3, "Incorrect syntax near 'nope'.")]
    public void ValueRefusals(string sql, int number, byte state, string message)
    {
        var error = new Simulation().AssertSqlError(sql, number);
        AreEqual((state, message), (error.Errors[0].State, error.Errors[0].Message));
    }

    /// <summary>OFFSETS and FIPS_FLAGGER parse and are discarded (probed 2026-10-04 against SQL Server 2025).</summary>
    [TestMethod]
    public void OffsetsAndFipsFlagger_Parse()
        => _ = RunBatch("set offsets select, from on; set fips_flagger 'entry'; set fips_flagger off");

    /// <summary>
    /// SET CONTEXT_INFO stores up to 128 bytes of any non-string value, which
    /// CONTEXT_INFO() pads and an empty one reads NULL, and the request DMV
    /// reports as set while the session DMV catches up when the batch ends
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ContextInfo_ValuesAndDmvs()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        object? Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        AreEqual(0, Scalar("set context_info 0x01; select datalength(context_info) from sys.dm_exec_sessions where session_id = @@spid"));
        AreEqual(1, Scalar("select datalength(context_info) from sys.dm_exec_sessions where session_id = @@spid"));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 2 }, ((byte[])Scalar("set context_info 258; select context_info()")!)[..4]);
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0 }, ((byte[])Scalar("set context_info -1; select context_info()")!)[..5]);
        AreEqual(DBNull.Value, Scalar("set context_info 0x; select context_info()"));
        AreEqual(2, Scalar("set context_info 0x0A0B; select datalength(context_info) from sys.dm_exec_requests where session_id = @@spid"));
        _ = sim.AssertSqlError("set context_info 'ab'", 2743);
        _ = sim.AssertSqlError("set context_info null", 2743);
        _ = sim.AssertSqlError("declare @b varbinary(200) = cast(replicate(cast(0x01 as varbinary(max)), 129) as varbinary(200)); set context_info @b", 2743);
    }

    /// <summary>
    /// SET IDENTITY_INSERT's refusals name the table as written and end the
    /// batch (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void IdentityInsertRefusals_EndTheBatch()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.e (a int); create table dbo.e1 (id int identity, a int); create table dbo.e2 (id int identity, a int)");
        var noIdentity = sim.AssertSqlError("set identity_insert dbo.e on; select 1", 8106);
        AreEqual("Table 'dbo.e' does not have the identity property. Cannot perform SET operation.", noIdentity.Errors[0].Message);
        _ = sim.AssertSqlError("set identity_insert dbo.nosuch on; select 1", 1088);
        Assert.EndsWith("for table 'dbo.e2'.", sim.AssertSqlError("set identity_insert dbo.e1 on; set identity_insert dbo.e2 on", 8107).Errors[0].Message);
        using var connection = sim.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "set identity_insert dbo.nosuch on; select 7";
        var rows = 0;
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.NextResult())
                rows++;
        }
        catch (SimulatedSqlException)
        {
        }
        AreEqual(0, rows);
    }

    /// <summary>
    /// A SHOWPLAN switch turned on must stand alone, in no module or list,
    /// while one turned off may sit anywhere outside a module (probed
    /// 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void Showplan_MustStandAlone()
    {
        var sim = new Simulation();
        AreEqual(2, sim.AssertSqlError("set showplan_text on; select 1", 1067).Errors[0].State);
        var afterOther = sim.AssertSqlError("select 1; set showplan_text on", 1067);
        AreEqual((1, 0), (afterOther.Errors[0].State, afterOther.Errors[0].LineNumber));
        _ = sim.AssertSqlError("set showplan_text, nocount on", 1067);
        _ = sim.AssertSqlError("create proc dbo.p as set showplan_text off", 1067);
        AreEqual(1, sim.ExecuteScalar("set showplan_text off; select 1"));
    }

    /// <summary>
    /// A function body may SET ANSI_NULLS and QUOTED_IDENTIFIER, which it
    /// ignores (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void FunctionBody_MaySetCapturedOptions()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.f() returns int as begin set ansi_nulls off; set quoted_identifier on; return case when null = null then 1 else 0 end end");
        AreEqual(0, sim.ExecuteScalar("select dbo.f()"));
        _ = sim.AssertSqlError("create function dbo.g() returns int as begin set nocount on; return 1 end", 443);
    }

    /// <summary>
    /// LANGUAGE, LOCK_TIMEOUT, the isolation level, FMTONLY and IDENTITY_INSERT
    /// revert when the procedure or dynamic batch that set them returns, and a
    /// SET LANGUAGE there sends no Msg 5703 (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void ModuleSets_RevertOnReturn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int identity, x int)",
            """
            create proc dbo.p as
            begin
                set language Deutsch; set lock_timeout 77; set transaction isolation level serializable;
                set identity_insert dbo.t on; set fmtonly on;
            end
            """);
        using var connection = sim.CreateDbConnection();
        connection.Open();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => error.Number));
        using var command = connection.CreateCommand();
        command.CommandText = "exec dbo.p; exec ('set language French'); select concat(@@language, '|', @@lock_timeout, '|', transaction_isolation_level) from sys.dm_exec_sessions where session_id = @@spid";
        AreEqual("us_english|-1|2", command.ExecuteScalar());
        IsEmpty(messages);
        command.CommandText = "insert dbo.t (id, x) values (5, 1)";
        AreEqual(544, Throws<SimulatedSqlException>(() => command.ExecuteNonQuery()).Number);
    }

    /// <summary>
    /// A table created under ANSI_NULLS OFF takes a persisted computed column,
    /// and an index over it is then Msg 1935 (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void AnsiNullsOffTable_RefusesComputedIndex()
    {
        var sim = new Simulation();
        var error = sim.AssertSqlError("set ansi_nulls off; create table dbo.e (a int, b as a + 1 persisted); create index ix on dbo.e(b)", 1935);
        AreEqual("Cannot create index. Object 'e' was created with the following SET options off: 'ANSI_NULLS'.", error.Errors[0].Message);
    }
}
