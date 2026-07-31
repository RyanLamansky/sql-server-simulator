using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for system-versioned temporal tables: <c>CREATE TABLE … PERIOD FOR
/// SYSTEM_TIME (start, end) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))</c>,
/// INSERT / UPDATE / DELETE history maintenance, and <c>FOR SYSTEM_TIME</c>
/// query syntax. Probe-confirmed verbatim against SQL Server 2025 on
/// 2026-05-13: <c>sys.tables.temporal_type</c> shape, period-column types
/// (datetime2(7)), engine-populated period values on INSERT (ROW START =
/// SYSUTCDATETIME(), ROW END = max datetime2), Msg 13501 / 13504 / 13505 /
/// 13506 / 13507 / 13509 / 13536 / 13537 / 13552 / 13559 / 13587 error paths.
/// </summary>
[TestClass]
public sealed class TemporalTableTests
{
    private const string CreateTemporalCustomers = """
        create table Customers (
            Id int not null primary key,
            Name nvarchar(30) not null,
            Vf datetime2 generated always as row start hidden not null,
            Vt datetime2 generated always as row end hidden not null,
            period for system_time (Vf, Vt)
        ) with (system_versioning = on (history_table = dbo.CustomersHistory))
        """;

    [TestMethod]
    public void Ddl_CreatesParentAndHistory()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from sys.tables where name in ('Customers', 'CustomersHistory')"));
    }

    [TestMethod]
    public void Insert_AutoPopulatesPeriodColumns()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'alice')");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from Customers"));
        // Explicit projection — hidden columns reachable by name.
        var maxEnd = simulation.ExecuteScalar("select Vt from Customers where Id = 1");
        AreEqual(DateTime.MaxValue.Date, ((DateTime)maxEnd!).Date);
    }

    [TestMethod]
    public void SelectStar_ExcludesHiddenPeriodColumns()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'a')");
        using var con = simulation.CreateOpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select * from Customers";
        using var r = cmd.ExecuteReader();
        AreEqual(2, r.FieldCount);
        AreEqual("Id", r.GetName(0));
        AreEqual("Name", r.GetName(1));
    }

    [TestMethod]
    public void Insert_ExplicitGeneratedAlwaysValue_RaisesMsg13536()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; insert Customers (Id, Name, Vf) values (1, 'a', sysutcdatetime())",
            13536,
            "Cannot insert an explicit value into a GENERATED ALWAYS column in table 'simulated.dbo.Customers'. Use INSERT with a column list to exclude the GENERATED ALWAYS column, or insert a DEFAULT into GENERATED ALWAYS column.");

    [TestMethod]
    public void Insert_IntoHistoryTable_RaisesMsg13559()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; insert CustomersHistory (Id, Name, Vf, Vt) values (1, 'a', sysutcdatetime(), sysutcdatetime())",
            13559,
            "Cannot insert rows in a temporal history table 'simulated.dbo.CustomersHistory'.");

    [TestMethod]
    public void Ddl_GeneratedColumnWithoutPeriod_RaisesMsg13509()
        => new Simulation().AssertSqlError(
            "create table t (id int, Vf datetime2 generated always as row start hidden not null)",
            13509,
            "Cannot create generated always column when SYSTEM_TIME period is not defined.");

    [TestMethod]
    public void Ddl_PeriodEndDoesntMatch_RaisesMsg13507()
        => new Simulation().AssertSqlError(
            "create table t (id int, Vf datetime2 generated always as row start hidden not null, Vt datetime2 generated always as row end hidden not null, Other int, period for system_time (Vf, Other))",
            13507,
            "System-versioned table SYSTEM_TIME period definition end column name not matching 'GENERATED ALWAYS AS ROW END' column name.");

    [TestMethod]
    public void Ddl_PeriodColumnNullable_RaisesMsg13587()
        => new Simulation().AssertSqlError(
            "create table t (id int, Vf datetime2 generated always as row start hidden null, Vt datetime2 generated always as row end hidden null, period for system_time (Vf, Vt))",
            13587,
            "Period column 'Vf' in a system-versioned temporal table cannot be nullable.");

    /// <summary>
    /// One row with a hand-built version timeline: <c>a1</c> over
    /// [2020, 2021), <c>a2</c> over [2021, 2022), plus the engine-timed
    /// current version <c>a3</c> whose period runs from the INSERT's
    /// statement time to max datetime2. Writing the two history rows
    /// directly — versioning off, insert, versioning back on, the shape
    /// SqlPackage emits — pins the boundaries to literals, so the range
    /// tests assert against exact endpoints instead of racing the clock.
    /// </summary>
    private static Simulation VersionedCustomer()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'a3')",
            "alter table Customers set (system_versioning = off)",
            """
            insert CustomersHistory (Id, Name, Vf, Vt) values
                (1, 'a1', '2020-01-01', '2021-01-01'),
                (1, 'a2', '2021-01-01', '2022-01-01')
            """,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        return simulation;
    }

    /// <summary>
    /// Runs a <c>FOR SYSTEM_TIME</c> clause over <see cref="VersionedCustomer"/>
    /// and returns the matched versions' names, comma-separated in name
    /// order (null when nothing matches).
    /// </summary>
    private static object? Versions(Simulation simulation, string forSystemTime)
        => simulation.ExecuteScalar($"select string_agg(Name, ',') within group (order by Name) from Customers for system_time {forSystemTime}");

    [TestMethod]
    public void ForSystemTimeAll_UnionsCurrentAndHistory()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'a'), (2, 'b')",
            // Separate the INSERT's statement time from the UPDATE's so the
            // history row it writes has a non-zero duration: versions that
            // start and end on the same tick are invisible to every
            // FOR SYSTEM_TIME form, and DateTime.UtcNow advances in ~15.6ms
            // steps on Windows.
            "waitfor delay '00:00:00.050'",
            "update Customers set Name = 'A' where Id = 1");
        AreEqual(3, simulation.ExecuteScalar("select count(*) from Customers for system_time all"));
    }

    [TestMethod]
    public void ForSystemTimeBetween_IsInclusiveOfBothEndpoints()
    {
        var simulation = VersionedCustomer();
        // A point range on the boundary two versions share takes the one
        // starting there (start <= t2) and not the one ending there
        // (end > t1 fails).
        AreEqual("a2", Versions(simulation, "between '2021-01-01' and '2021-01-01'"));
        AreEqual("a1,a2", Versions(simulation, "between '2020-01-01' and '2021-01-01'"));
        // The current version starts after 2022, so a range ending there
        // leaves it out; one reaching max datetime2 takes it.
        AreEqual("a1,a2", Versions(simulation, "between '2020-01-01' and '2022-01-01'"));
        AreEqual("a1,a2,a3", Versions(simulation, "between '2020-01-01' and '9999-12-31 23:59:59.9999999'"));
    }

    [TestMethod]
    public void ForSystemTimeFromTo_ExcludesTheUpperEndpoint()
    {
        var simulation = VersionedCustomer();
        // Same lower-bound rule as BETWEEN, but a version starting exactly
        // at the upper endpoint is out — so the shared-boundary point range
        // matches nothing at all.
        AreEqual("a1", Versions(simulation, "from '2020-01-01' to '2021-01-01'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time from '2021-01-01' to '2021-01-01'"));
        AreEqual("a1,a2", Versions(simulation, "from '2020-01-01' to '2022-01-01'"));
    }

    [TestMethod]
    public void ForSystemTimeContainedIn_RequiresTheWholePeriodInsideTheRange()
    {
        var simulation = VersionedCustomer();
        // Both endpoints are inclusive against the version's own endpoints:
        // [2020, 2021) fits exactly inside (2020, 2021).
        AreEqual("a1", Versions(simulation, "contained in ('2020-01-01', '2021-01-01')"));
        AreEqual("a1,a2", Versions(simulation, "contained in ('2020-01-01', '2022-01-01')"));
        // One tick inside a version's own start drops that version.
        AreEqual("a2", Versions(simulation, "contained in ('2020-01-01 00:00:00.0000001', '2022-01-01')"));
        // The current version's period ends at max datetime2, so only a
        // range reaching that far contains it.
        AreEqual("a1,a2,a3", Versions(simulation, "contained in ('2020-01-01', '9999-12-31 23:59:59.9999999')"));
    }

    [TestMethod]
    public void ForSystemTimeRanges_AcceptVariableArguments()
    {
        var simulation = VersionedCustomer();
        AreEqual("a1,a2", simulation.ExecuteScalar("""
            declare @from datetime2(7) = '2020-01-01', @to datetime2(7) = '2022-01-01';
            select string_agg(Name, ',') within group (order by Name)
            from Customers for system_time between @from and @to
            """));
    }

    [TestMethod]
    public void ForSystemTimeRanges_MisorderedEndpoints_ReturnNoRows()
    {
        var simulation = VersionedCustomer();
        // Real doesn't reject t2 < t1 — the predicate simply can't hold.
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time between '2022-01-01' and '2020-01-01'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time from '2022-01-01' to '2020-01-01'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time contained in ('2022-01-01', '2020-01-01')"));
    }

    [TestMethod]
    public void ForSystemTimeRanges_NullArgument_ReturnsNoRows()
        => AreEqual(0, VersionedCustomer().ExecuteScalar("select count(*) from Customers for system_time between null and null"));

    [TestMethod]
    public void ForSystemTime_ZeroDurationVersion_IsInvisibleToEveryForm()
    {
        // A row updated twice inside one transaction leaves a history row
        // whose period start equals its end. Real stores it — a direct
        // SELECT against the history table returns it — but hides it from
        // every FOR SYSTEM_TIME form.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'current')",
            "alter table Customers set (system_versioning = off)",
            "insert CustomersHistory (Id, Name, Vf, Vt) values (9, 'zero', '2020-01-01', '2020-01-01')",
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from CustomersHistory where Id = 9"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time all where Id = 9"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time as of '2020-01-01' where Id = 9"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time between '2000-01-01' and '2030-01-01' where Id = 9"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time from '2000-01-01' to '2030-01-01' where Id = 9"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time contained in ('2000-01-01', '2030-01-01') where Id = 9"));
    }

    [TestMethod]
    public void ForSystemTimeRange_NumericArgument_RaisesMsg206()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; select * from Customers for system_time between 42 and 99",
            206,
            "Operand type clash: datetime2 is incompatible with int");

    [TestMethod]
    public void ForSystemTimeRange_TimeArgument_RaisesMsg402()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; declare @t time = '10:00'; select * from Customers for system_time from @t to '2030-01-01'",
            402,
            "The data types datetime2 and time are incompatible in the greater than operator.");

    [TestMethod]
    public void ForSystemTimeRange_UnparseableStringArgument_RaisesMsg241()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; select * from Customers for system_time contained in ('nonsense', '2030-01-01')",
            241,
            "Conversion failed when converting date and/or time from character string.");

    [TestMethod]
    public void ForSystemTime_FunctionArgument_RaisesMsg102()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; select * from Customers for system_time as of sysutcdatetime()",
            102,
            "Incorrect syntax near 'sysutcdatetime'.");

    [TestMethod]
    public void ForSystemTime_UnknownForm_RaisesMsg102()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; select * from Customers for system_time garbage",
            102,
            "Incorrect syntax near 'garbage'.");

    [TestMethod]
    public void ForSystemTimeContainedIn_WithoutParentheses_RaisesMsg102()
        // Number only: the message names the offending literal, and the
        // simulator renders a string literal with the quotes it was written
        // with where real strips them — a token-rendering difference that
        // predates this clause and is not what the test is about.
        => _ = new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; select * from Customers for system_time contained in '2020-01-01', '2022-01-01'",
            102);

    [TestMethod]
    public void ForSystemTimeBetween_WithToSeparator_RaisesMsg156()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; select * from Customers for system_time between '2020-01-01' to '2022-01-01'",
            156,
            "Incorrect syntax near the keyword 'to'.");

    [TestMethod]
    public void ForSystemTimeRange_NonTemporal_RaisesMsg13544()
        => new Simulation().AssertSqlError(
            "create table dbo.NotTemp (id int); select * from dbo.NotTemp for system_time contained in ('2020-01-01', '2022-01-01')",
            13544,
            "Temporal FOR SYSTEM_TIME clause can only be used with system-versioned tables. 'simulated.dbo.NotTemp' is not a system-versioned table.");

    [TestMethod]
    public void ForSystemTimeAsOf_ReturnsPriorState()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'before')");
        // Capture the insert's ROW START + 1 tick as the AS-OF time point.
        // Sleep before the UPDATE so its statement-frozen UtcNow advances
        // strictly past betweenTime on hosts with coarse clock granularity
        // (Windows DateTime.UtcNow ticks at ~15.6ms — without the sleep,
        // T_insert and T_update can land on the same tick, leaving no
        // (T_insert, T_update) window for the AS-OF filter to find).
        var betweenTime = ((DateTime)simulation.ExecuteScalar("select Vf from Customers where Id = 1")!).AddTicks(1);
        System.Threading.Thread.Sleep(50);
        _ = simulation.ExecuteNonQuery("update Customers set Name = 'after' where Id = 1");
        var asOf = simulation.ExecuteScalar($"select Name from Customers for system_time as of '{betweenTime:O}' where Id = 1");
        AreEqual("before", asOf);
    }

    [TestMethod]
    public void Update_CopiesOldRowToHistory()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'alice')",
            "update Customers set Name = 'ALICE' where Id = 1");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from Customers where Name = 'ALICE'"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from CustomersHistory where Name = 'alice'"));
    }

    [TestMethod]
    public void Update_SetGeneratedAlwaysColumn_RaisesMsg13537()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; insert Customers (Id, Name) values (1, 'a'); update Customers set Vf = sysutcdatetime() where Id = 1",
            13537,
            "Cannot update GENERATED ALWAYS columns in table 'simulated.dbo.Customers'.");

    [TestMethod]
    public void Delete_MovesRowToHistory()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'alice')",
            "delete from Customers where Id = 1");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from CustomersHistory where Name = 'alice'"));
    }

    [TestMethod]
    public void DropTable_SystemVersionedParent_RaisesMsg13552()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; drop table Customers",
            13552,
            "Drop table operation failed on table 'simulated.dbo.Customers' because it is not a supported operation on system-versioned temporal tables.");

    [TestMethod]
    public void DropTable_HistorySibling_RaisesMsg13552()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; drop table CustomersHistory",
            13552,
            "Drop table operation failed on table 'simulated.dbo.CustomersHistory' because it is not a supported operation on system-versioned temporal tables.");

    [TestMethod]
    public void Ddl_GeneratedColumnNotDatetime2_RaisesMsg13501()
        => new Simulation().AssertSqlError(
            "create table t (id int, Vf datetime generated always as row start hidden not null, Vt datetime generated always as row end hidden not null, period for system_time (Vf, Vt))",
            13501,
            "Temporal generated always column 'Vf' has invalid data type.");

    [TestMethod]
    public void AlterSystemVersioningOff_PermitsDropOnParentAndHistory()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'a')",
            "update Customers set Name = 'A' where Id = 1",
            "alter table Customers set (system_versioning = off)",
            "drop table Customers",
            "drop table CustomersHistory");
        // Both tables gone from sys.tables after the flip-then-drop.
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name in ('Customers', 'CustomersHistory')"));
    }

    [TestMethod]
    public void Insert_AfterAlterOff_StillAutoPopulatesPeriodColumns()
    {
        // Probed 2026-05-13: real SQL Server keeps auto-populating the period
        // columns on INSERT after SET OFF — the GENERATED ALWAYS markers on
        // the parent's columns persist independently of the versioning link,
        // and the engine respects them. Without this regression the simulator
        // would diverge: a post-SET-OFF INSERT would leave Vf/Vt as raw
        // NULLs (failing NOT NULL) or unset zeros (depending on path).
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = off)",
            "insert Customers (Id, Name) values (1, 'alice')");
        AreEqual(DateTime.MaxValue.Date, ((DateTime)simulation.ExecuteScalar("select Vt from Customers where Id = 1")!).Date);
    }

    [TestMethod]
    public void Insert_AfterAlterOff_RejectsExplicitGeneratedColumn()
    {
        // Probed: Msg 13536 still fires post-SET-OFF (GENERATED ALWAYS marker
        // persists on the column, independently of the versioning link).
        // Pre-existing simulator path keys off column-level GeneratedAs and
        // not on table-level SystemVersioning, so this should pass without
        // changes — pin it down so future refactors can't regress.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = off)");
        simulation.AssertSqlError(
            "insert Customers (Id, Name, Vf) values (1, 'a', sysutcdatetime())",
            13536,
            "Cannot insert an explicit value into a GENERATED ALWAYS column in table 'simulated.dbo.Customers'. Use INSERT with a column list to exclude the GENERATED ALWAYS column, or insert a DEFAULT into GENERATED ALWAYS column.");
    }

    [TestMethod]
    public void AlterSystemVersioningOff_PreservesHiddenColumns()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = off)");
        // Probed: GENERATED ALWAYS + HIDDEN column metadata persists after SET
        // OFF on the parent. SELECT * still excludes the hidden period
        // columns and they're still reachable by explicit name.
        using var con = simulation.CreateOpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "select * from Customers";
        using var r = cmd.ExecuteReader();
        AreEqual(2, r.FieldCount);
        AreEqual("Id", r.GetName(0));
        AreEqual("Name", r.GetName(1));
    }

    [TestMethod]
    public void AlterSystemVersioningOff_OnRegularTable_RaisesMsg13591()
        => new Simulation().AssertSqlError(
            "create table t (id int); alter table t set (system_versioning = off)",
            13591,
            "SYSTEM_VERSIONING is not turned ON for table 'simulated.dbo.t'.");

    [TestMethod]
    public void AlterSystemVersioningOff_OnHistorySibling_RaisesMsg13591()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; alter table CustomersHistory set (system_versioning = off)",
            13591,
            "SYSTEM_VERSIONING is not turned ON for table 'simulated.dbo.CustomersHistory'.");

    [TestMethod]
    public void AlterTable_NonexistentTarget_RaisesMsg4902()
        => new Simulation().AssertSqlError(
            "alter table dbo.tNoSuch set (system_versioning = off)",
            4902,
            "Cannot find the object \"dbo.tNoSuch\" because it does not exist or you do not have permissions.");

    [TestMethod]
    public void AlterTable_UnsupportedShape_RaisesNotSupported()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(CreateTemporalCustomers);
        _ = ThrowsExactly<NotSupportedException>(
            () => simulation.ExecuteNonQuery("alter table Customers rebuild"));
    }

    [TestMethod]
    public void ForSystemTime_NonTemporal_RaisesMsg13544()
        => new Simulation().AssertSqlError(
            "create table dbo.NotTemp (id int); select * from dbo.NotTemp for system_time all",
            13544,
            "Temporal FOR SYSTEM_TIME clause can only be used with system-versioned tables. 'simulated.dbo.NotTemp' is not a system-versioned table.");

    [TestMethod]
    public void ForSystemTime_NonTemporalTempTable_RaisesMsg13544()
        => new Simulation().AssertSqlError(
            "create table #nt (id int); select * from #nt for system_time as of '2020-01-01'",
            13544,
            "Temporal FOR SYSTEM_TIME clause can only be used with system-versioned tables. 'tempdb.dbo.#nt' is not a system-versioned table.");

    [TestMethod]
    public void AlterColumn_PeriodColumn_RaisesMsg13599()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(CreateTemporalCustomers);
        simulation.AssertSqlError(
            "alter table Customers alter column Vf datetime2(3) not null",
            13599,
            "Period column 'Vf' in a system-versioned temporal table cannot be altered.");
    }

    [TestMethod]
    public void CreateTable_SystemVersioningOnWithoutPeriod_RaisesMsg13510()
        => new Simulation().AssertSqlError(
            "create table dbo.NoPeriod (id int primary key) with (system_versioning = on (history_table = dbo.h))",
            13510,
            "Cannot set SYSTEM_VERSIONING to ON when SYSTEM_TIME period is not defined and the LEDGER=ON option is not specified.");

    // -- ALTER … SET (SYSTEM_VERSIONING = ON …) tests --
    // Inverse of the OFF tests above. SqlPackage emits this shape post-CREATE
    // for system-versioned tables; the loader's phase-5 wire-up step relies
    // on this grammar to link base and history siblings after both exist.

    private const string CreateUnversionedTemporalPair = """
        create table Customers (
            Id int not null primary key,
            Name nvarchar(30) not null,
            Vf datetime2 generated always as row start not null,
            Vt datetime2 generated always as row end not null,
            period for system_time (Vf, Vt)
        );
        create table CustomersHistory (
            Id int not null,
            Name nvarchar(30) not null,
            Vf datetime2 not null,
            Vt datetime2 not null
        );
        """;

    [TestMethod]
    public void AlterSystemVersioningOn_LinksBaseAndHistory()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateUnversionedTemporalPair,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        // temporal_type 2 = SYSTEM_VERSIONED_TEMPORAL_TABLE on the base,
        // 1 = HISTORY_TABLE on the sibling.
        AreEqual((byte)2, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'Customers'"));
        AreEqual((byte)1, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'CustomersHistory'"));
    }

    [TestMethod]
    public void AlterSystemVersioningOn_WithDataConsistencyCheck_Parses()
    {
        // SQL Server's full grammar accepts an optional comma-separated
        // DATA_CONSISTENCY_CHECK = ON|OFF after HISTORY_TABLE. The simulator
        // parses but doesn't enforce the consistency check (history rows
        // are caller-trusted in the loader path).
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateUnversionedTemporalPair,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, data_consistency_check = off))");
        AreEqual((byte)2, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void AlterSystemVersioningOn_BaseWithoutPeriod_RaisesMsg13558()
        => new Simulation().AssertSqlError("""
            create table base (Id int);
            create table h (Id int);
            alter table base set (system_versioning = on (history_table = dbo.h))
            """,
            13558,
            "Setting SYSTEM_VERSIONING to ON failed because table 'simulated.dbo.base' does not have a PERIOD FOR SYSTEM_TIME declaration.");

    [TestMethod]
    public void AlterSystemVersioningOn_AlreadyOn_RaisesMsg13530()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))",
            13530,
            "Setting SYSTEM_VERSIONING to ON failed because table 'simulated.dbo.Customers' already has SYSTEM_VERSIONING turned ON.");

    [TestMethod]
    public void AlterSystemVersioningOn_HistoryAlreadyInUse_RaisesMsg13533()
    {
        // First pair establishes h as one base's history; second base then
        // tries to link the same h — must fail.
        var simulation = new Simulation();
        simulation.ExecuteBatches("""
            create table base1 (Id int not null primary key,
                                Vf datetime2 generated always as row start not null,
                                Vt datetime2 generated always as row end not null,
                                period for system_time (Vf, Vt)) with (system_versioning = on (history_table = dbo.h));
            create table base2 (Id int not null primary key,
                                Vf datetime2 generated always as row start not null,
                                Vt datetime2 generated always as row end not null,
                                period for system_time (Vf, Vt))
            """);
        simulation.AssertSqlError(
            "alter table base2 set (system_versioning = on (history_table = dbo.h))",
            13533,
            "Setting SYSTEM_VERSIONING to ON failed because history table 'simulated.dbo.h' is already in use as a temporal table sibling.");
    }

    [TestMethod]
    public void AlterSystemVersioningOn_MissingHistoryTable_RaisesMsg4902()
        => new Simulation().AssertSqlError("""
            create table base (Id int not null primary key,
                               Vf datetime2 generated always as row start not null,
                               Vt datetime2 generated always as row end not null,
                               period for system_time (Vf, Vt));
            alter table base set (system_versioning = on (history_table = dbo.tNoSuch))
            """,
            4902,
            "Cannot find the object \"dbo.tNoSuch\" because it does not exist or you do not have permissions.");
}
