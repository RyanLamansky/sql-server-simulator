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

    /// <summary>
    /// The three sibling refusals of Msg 13507: a period declared with no
    /// generated ROW START column at all (13504), none for ROW END (13505),
    /// and a start name that matches no generated column (13506). Probed
    /// against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void Ddl_PeriodWithoutMatchingGeneratedColumns_RaisesTheStartAndEndErrors()
    {
        new Simulation().AssertSqlError(
            "create table t (id int, Vt datetime2 generated always as row end hidden not null, period for system_time (Vf, Vt))",
            13504,
            "Temporal 'GENERATED ALWAYS AS ROW START' column definition missing.");
        new Simulation().AssertSqlError(
            "create table t (id int, Vf datetime2 generated always as row start hidden not null, period for system_time (Vf, Vt))",
            13505,
            "Temporal 'GENERATED ALWAYS AS ROW END' column definition missing.");
        new Simulation().AssertSqlError(
            "create table t (id int, Vf datetime2 generated always as row start hidden not null, Vt datetime2 generated always as row end hidden not null, Other int, period for system_time (Other, Vt))",
            13506,
            "System-versioned table SYSTEM_TIME period definition start column name not matching 'GENERATED ALWAYS AS ROW START' column name.");
    }

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
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; select * from Customers for system_time contained in '2020-01-01', '2022-01-01'",
            102,
            "Incorrect syntax near '2020-01-01'.");

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
            () => simulation.ExecuteNonQuery("alter table Customers switch partition 1 to Archive"));
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
    public void AlterSystemVersioningOn_BaseWithoutPeriod_RaisesMsg13510()
        => new Simulation().AssertSqlError("""
            create table base (Id int);
            create table h (Id int);
            alter table base set (system_versioning = on (history_table = dbo.h))
            """,
            13510,
            "Cannot set SYSTEM_VERSIONING to ON when SYSTEM_TIME period is not defined and the LEDGER=ON option is not specified.");

    [TestMethod]
    public void AlterSystemVersioningOn_AlreadyOnWithSameHistory_Succeeds()
    {
        // Re-issuing SET ON against the sibling the base already has is real's
        // supported way to change the retention period in place.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 5 weeks))");
        AreEqual((byte)2, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'Customers'"));
        AreEqual("WEEK", simulation.ExecuteScalar("select history_retention_period_unit_desc from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void AlterSystemVersioningOn_AlreadyOnWithoutHistoryTable_RaisesMsg13596()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; alter table Customers set (system_versioning = on)",
            13596,
            "SYSTEM_VERSIONING is already turned ON for table 'simulated.dbo.Customers'.");

    [TestMethod]
    public void AlterSystemVersioningOn_AlreadyOnWithDifferentHistory_RaisesMsg13595()
        => new Simulation().AssertSqlError($"""
            {CreateTemporalCustomers};
            create table Other (Id int not null, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null);
            alter table Customers set (system_versioning = on (history_table = dbo.Other))
            """,
            13595,
            "Temporal history table name 'simulated.dbo.Other' is not correct for current table 'simulated.dbo.Customers'.");

    [TestMethod]
    public void AlterSystemVersioningOn_AlreadyOnWithUnresolvableHistory_RaisesMsg13757()
        => new Simulation().AssertSqlError(
            $"{CreateTemporalCustomers}; alter table Customers set (system_versioning = on (history_table = dbo.tNoSuch))",
            13757,
            "Temporal table 'simulated.dbo.Customers' already has history table defined. Consider dropping system_versioning first if you want to use different history table.");

    [TestMethod]
    public void AlterSystemVersioningOn_HistoryAlreadyInUse_RaisesMsg13514()
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
            13514,
            "History table 'simulated.dbo.h' is already in use.");
    }

    [TestMethod]
    public void AlterSystemVersioningOn_MissingHistoryTable_CreatesIt()
    {
        // Real doesn't reject a history-table name that doesn't resolve — it
        // builds the table from the base's shape, same as the CREATE path.
        var simulation = new Simulation();
        simulation.ExecuteBatches("""
            create table base (Id int not null primary key,
                               Vf datetime2 generated always as row start not null,
                               Vt datetime2 generated always as row end not null,
                               period for system_time (Vf, Vt));
            alter table base set (system_versioning = on (history_table = dbo.BaseHistory))
            """);
        AreEqual((byte)1, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'BaseHistory'"));
        AreEqual(3, simulation.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.BaseHistory')"));
    }

    // -- Auto-named history tables --

    private const string CreateAutoNamedCustomers = """
        create table Customers (
            Id int not null primary key,
            Name nvarchar(30) not null,
            Vf datetime2 generated always as row start not null,
            Vt datetime2 generated always as row end not null,
            period for system_time (Vf, Vt)
        ) with (system_versioning = on)
        """;

    [TestMethod]
    public void AutoNamedHistory_TakesTheBaseObjectIdAndSchema()
    {
        // MSSQL_TemporalHistoryFor_<base object_id>, in the base's own schema
        // — probe-confirmed shape (the id itself is the simulator's, so the
        // name matches real's structure, not its value).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create schema app; {CreateAutoNamedCustomers.Replace("table Customers", "table app.Customers", StringComparison.Ordinal)}");
        AreEqual(1, simulation.ExecuteScalar("""
            select count(*) from sys.tables t join sys.schemas s on s.schema_id = t.schema_id
            where t.temporal_type = 1 and s.name = 'app'
              and t.name = 'MSSQL_TemporalHistoryFor_' + cast((select object_id from sys.tables where name = 'Customers') as varchar(20))
            """));
    }

    [TestMethod]
    public void AutoNamedHistory_MaintainsVersionsLikeANamedOne()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateAutoNamedCustomers,
            "insert Customers (Id, Name) values (1, 'a')",
            "waitfor delay '00:00:00.050'",
            "update Customers set Name = 'A' where Id = 1");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from Customers for system_time all"));
    }

    [TestMethod]
    public void AutoNamedHistory_CollidingName_TakesAHexSuffix()
    {
        // Turning versioning off leaves the old sibling behind under the
        // generated name, so turning it back on has to disambiguate. Real
        // appends a random 8-hex suffix; the simulator's is deterministic.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateAutoNamedCustomers,
            "alter table Customers set (system_versioning = off)",
            "alter table Customers set (system_versioning = on)");
        var second = (string)simulation.ExecuteScalar("select name from sys.tables where temporal_type = 1")!;
        var expectedPrefix = "MSSQL_TemporalHistoryFor_" + simulation.ExecuteScalar("select object_id from sys.tables where name = 'Customers'");
        StartsWith(expectedPrefix + "_", second);
        AreEqual(expectedPrefix.Length + 9, second.Length);
    }

    // -- HISTORY_RETENTION_PERIOD --

    /// <summary>
    /// The <see cref="VersionedCustomer"/> timeline with a retention period
    /// applied: <c>a1</c> ends in 2021 and <c>a2</c> in 2022, both long past
    /// any window measured from now, so a finite retention hides both while
    /// the current <c>a3</c> stays.
    /// </summary>
    private static Simulation AgedVersionedCustomer(string retention)
    {
        var simulation = VersionedCustomer();
        _ = simulation.ExecuteNonQuery($"alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = {retention}))");
        return simulation;
    }

    [TestMethod]
    public void HistoryRetention_DefaultsToInfinite()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        AreEqual(-1, simulation.ExecuteScalar("select history_retention_period from sys.tables where name = 'Customers'"));
        AreEqual(-1, simulation.ExecuteScalar("select history_retention_period_unit from sys.tables where name = 'Customers'"));
        AreEqual("INFINITE", simulation.ExecuteScalar("select history_retention_period_unit_desc from sys.tables where name = 'Customers'"));
        // History and non-temporal tables report NULL rather than any value.
        AreEqual(0, simulation.ExecuteScalar("select count(history_retention_period) from sys.tables where name = 'CustomersHistory'"));
    }

    [TestMethod]
    public void HistoryRetention_ProjectsUnitCodeAndDescription()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table Customers (
                Id int not null primary key,
                Vf datetime2 generated always as row start not null,
                Vt datetime2 generated always as row end not null,
                period for system_time (Vf, Vt)
            ) with (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))
            """);
        AreEqual(3, simulation.ExecuteScalar("select history_retention_period from sys.tables where name = 'Customers'"));
        AreEqual(5, simulation.ExecuteScalar("select history_retention_period_unit from sys.tables where name = 'Customers'"));
        AreEqual("MONTH", simulation.ExecuteScalar("select history_retention_period_unit_desc from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void HistoryRetention_AcceptsEveryUnitInBothNumberForms()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        foreach (var (spelling, unit) in new[] { ("7 day", 3), ("7 days", 3), ("2 week", 4), ("2 weeks", 4), ("1 month", 5), ("1 months", 5), ("4 year", 6), ("4 years", 6) })
        {
            _ = simulation.ExecuteNonQuery($"alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = {spelling}))");
            AreEqual(unit, simulation.ExecuteScalar("select history_retention_period_unit from sys.tables where name = 'Customers'"));
        }
        _ = simulation.ExecuteNonQuery("alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = infinite))");
        AreEqual(-1, simulation.ExecuteScalar("select history_retention_period from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void HistoryRetention_NonPositiveCount_RaisesMsg13743()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 0 days))",
            13743,
            "0 is not a valid value for system versioning history retention period.");
        simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = -1 days))",
            13743,
            "-1 is not a valid value for system versioning history retention period.");
    }

    [TestMethod]
    public void HistoryRetention_UnknownUnit_RaisesMsg13744()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        var error = simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 hours))",
            13744);
        AreEqual("'hours' is not a valid history retention period unit for system versioning.", error.Message);
        // Severity 15, unlike the rest of the temporal family's 16.
        AreEqual((byte)15, error.Class);
    }

    [TestMethod]
    public void HistoryRetention_MissingUnit_RaisesMsg102()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3))",
            102,
            "Incorrect syntax near ')'.");
    }

    [TestMethod]
    public void HistoryRetention_AgedVersions_AreInvisibleToEveryForm()
    {
        var simulation = AgedVersionedCustomer("10 days");
        AreEqual("a3", Versions(simulation, "all"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time as of '2020-06-01'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time between '2020-01-01' and '2022-01-01'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time from '2020-01-01' to '2022-01-01'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from Customers for system_time contained in ('2020-01-01', '2022-01-01')"));
    }

    [TestMethod]
    public void HistoryRetention_AgedVersions_StayInTheHistoryTable()
    {
        // Real prunes from a background task, so an aged row is filtered out
        // of FOR SYSTEM_TIME immediately but still readable directly. The
        // simulator has no background task, so its aged rows stay forever.
        var simulation = AgedVersionedCustomer("10 days");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from CustomersHistory"));
    }

    [TestMethod]
    public void HistoryRetention_WindowIsMeasuredFromTheStatementClock()
    {
        // A window reaching back to mid-2021 covers a2 (which stopped being
        // current at 2022-01-01) but not a1 (2021-01-01) — so the cutoff is
        // read off the clock, not treated as all-or-nothing.
        var now = DateTime.UtcNow;
        var monthsBackToMid2021 = ((now.Year - 2021) * 12) + now.Month - 7;
        var simulation = AgedVersionedCustomer($"{monthsBackToMid2021} months");
        AreEqual("a2,a3", Versions(simulation, "all"));
    }

    [TestMethod]
    public void HistoryRetention_Infinite_RestoresAgedVersions()
    {
        var simulation = AgedVersionedCustomer("10 days");
        _ = simulation.ExecuteNonQuery("alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = infinite))");
        AreEqual("a1,a2,a3", Versions(simulation, "all"));
    }

    // -- Base / history column-shape validation at SET (SYSTEM_VERSIONING = ON) --

    /// <summary>
    /// Links <see cref="CreateUnversionedTemporalPair"/>'s base to a history
    /// table declared by <paramref name="historyColumns"/> and returns the
    /// rejection. Real's check order is probe-confirmed: the history table's
    /// own period, then unique keys / foreign keys / constraints / IDENTITY,
    /// then the column count, then an ordinal walk over name, type, collation
    /// and nullability.
    /// </summary>
    private static void AssertHistoryShapeError(string historyColumns, int errorNumber, string expectedMessage)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table Customers (
                Id int not null primary key,
                Name nvarchar(30) not null,
                Vf datetime2 generated always as row start not null,
                Vt datetime2 generated always as row end not null,
                period for system_time (Vf, Vt)
            );
            create table CustomersHistory ({historyColumns});
            """);
        simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))",
            errorNumber,
            expectedMessage);
    }

    [TestMethod]
    public void HistoryShape_ColumnCountMismatch_RaisesMsg13523()
        => AssertHistoryShapeError(
            "Id int not null, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null, Extra int null",
            13523,
            "Setting SYSTEM_VERSIONING to ON failed because table 'simulated.dbo.Customers' has 4 columns and table 'simulated.dbo.CustomersHistory' has 5 columns.");

    [TestMethod]
    public void HistoryShape_ColumnNameMismatch_RaisesMsg13524()
        => AssertHistoryShapeError(
            "Id int not null, Nom nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null",
            13524,
            "Setting SYSTEM_VERSIONING to ON failed because column 'Nom' at ordinal 2 in history table 'simulated.dbo.CustomersHistory' has a different name than the column 'Name' at the same ordinal in table 'simulated.dbo.Customers'.");

    [TestMethod]
    public void HistoryShape_ColumnTypeMismatch_RaisesMsg13525()
        => AssertHistoryShapeError(
            "Id int not null, Name nvarchar(40) not null, Vf datetime2 not null, Vt datetime2 not null",
            13525,
            "Setting SYSTEM_VERSIONING to ON failed because column 'Name' has data type nvarchar(40) in history table 'simulated.dbo.CustomersHistory' which is different from corresponding column type nvarchar(30) in table 'simulated.dbo.Customers'.");

    [TestMethod]
    public void HistoryShape_PeriodColumnPrecisionMismatch_RaisesMsg13525()
        => AssertHistoryShapeError(
            "Id int not null, Name nvarchar(30) not null, Vf datetime2(3) not null, Vt datetime2(3) not null",
            13525,
            "Setting SYSTEM_VERSIONING to ON failed because column 'Vf' has data type datetime2(3) in history table 'simulated.dbo.CustomersHistory' which is different from corresponding column type datetime2(7) in table 'simulated.dbo.Customers'.");

    [TestMethod]
    public void HistoryShape_CollationMismatch_RaisesMsg13526()
        => AssertHistoryShapeError(
            "Id int not null, Name nvarchar(30) collate Latin1_General_BIN not null, Vf datetime2 not null, Vt datetime2 not null",
            13526,
            "Setting SYSTEM_VERSIONING to ON failed because column 'Name' does not have the same collation in tables 'simulated.dbo.Customers' and 'simulated.dbo.CustomersHistory'.");

    [TestMethod]
    public void HistoryShape_NullabilityMismatch_RaisesMsg13531()
        => AssertHistoryShapeError(
            "Id int not null, Name nvarchar(30) null, Vf datetime2 not null, Vt datetime2 not null",
            13531,
            "Setting SYSTEM_VERSIONING to ON failed because column 'Name' does not have the same nullability attribute in tables 'simulated.dbo.Customers' and 'simulated.dbo.CustomersHistory'.");

    [TestMethod]
    public void HistoryShape_UniqueKey_RaisesMsg13515()
        => AssertHistoryShapeError(
            "Id int not null primary key, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null",
            13515,
            "Setting SYSTEM_VERSIONING to ON failed because history table 'simulated.dbo.CustomersHistory' has custom unique keys defined. Consider dropping all unique keys and trying again.");

    [TestMethod]
    public void HistoryShape_ForeignKey_RaisesMsg13516()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table Customers (
                Id int not null primary key,
                Vf datetime2 generated always as row start not null,
                Vt datetime2 generated always as row end not null,
                period for system_time (Vf, Vt)
            );
            create table Ref (Id int not null primary key);
            create table CustomersHistory (Id int not null references Ref(Id), Vf datetime2 not null, Vt datetime2 not null);
            """);
        simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))",
            13516,
            "Setting SYSTEM_VERSIONING to ON failed because history table 'simulated.dbo.CustomersHistory' has foreign keys defined. Consider dropping all foreign keys and trying again.");
    }

    [TestMethod]
    public void HistoryShape_CheckConstraint_RaisesMsg13517()
        => AssertHistoryShapeError(
            "Id int not null check (Id > 0), Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null",
            13517,
            "Setting SYSTEM_VERSIONING to ON failed because history table 'simulated.dbo.CustomersHistory' has table or column constraints defined. Consider dropping all table and column constraints and trying again.");

    [TestMethod]
    public void HistoryShape_IdentityColumn_RaisesMsg13518()
        => AssertHistoryShapeError(
            "Id int identity(1,1) not null, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null",
            13518,
            "Setting SYSTEM_VERSIONING to ON failed because history table 'simulated.dbo.CustomersHistory' has IDENTITY column specification. Consider dropping all IDENTITY column specifications and trying again.");

    [TestMethod]
    public void HistoryShape_OwnPeriod_RaisesMsg13574()
        => AssertHistoryShapeError("""
            Id int not null, Name nvarchar(30) not null,
            Vf datetime2 generated always as row start not null,
            Vt datetime2 generated always as row end not null,
            period for system_time (Vf, Vt)
            """,
            13574,
            "Setting SYSTEM_VERSIONING to ON failed because temporal history table 'simulated.dbo.CustomersHistory' contains SYSTEM_TIME period.");

    [TestMethod]
    public void HistoryShape_DefaultConstraintAndNonUniqueIndex_AreAccepted()
    {
        // Probe-confirmed: real rejects unique keys, FKs and CHECK constraints
        // on a history table but accepts DEFAULTs and non-unique indexes.
        var simulation = new Simulation();
        simulation.ExecuteBatches("""
            create table Customers (
                Id int not null primary key,
                Name nvarchar(30) not null,
                Vf datetime2 generated always as row start not null,
                Vt datetime2 generated always as row end not null,
                period for system_time (Vf, Vt)
            );
            create table CustomersHistory (Id int not null default 0, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null);
            create index ix_CustomersHistory on CustomersHistory (Vt, Vf);
            """,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        AreEqual((byte)2, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void HistoryShape_ExistingMatchingTable_LinksFromCreateTable()
    {
        // CREATE TABLE naming an existing history table adopts it after the
        // same validation, rather than failing on the name collision.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table CustomersHistory (Id int not null, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null)",
            CreateTemporalCustomers.Replace("hidden ", "", StringComparison.Ordinal));
        AreEqual((byte)1, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'CustomersHistory'"));
        AreEqual((byte)2, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void HistoryShape_ExistingMismatchedTable_RejectsCreateTableAndLeavesNoBase()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table CustomersHistory (Id int not null, Nom nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null)");
        simulation.AssertSqlError(
            CreateTemporalCustomers.Replace("hidden ", "", StringComparison.Ordinal),
            13524,
            "Setting SYSTEM_VERSIONING to ON failed because column 'Nom' at ordinal 2 in history table 'simulated.dbo.CustomersHistory' has a different name than the column 'Name' at the same ordinal in table 'simulated.dbo.Customers'.");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 'Customers'"));
    }

    /// <summary>
    /// Real gives every engine-built history table a non-unique clustered
    /// index named <c>ix_&lt;history table&gt;</c> keyed on <c>(period end,
    /// period start)</c> — probe-confirmed on SQL Server 2025, including that
    /// the index takes <c>index_id 1</c> (so the history table projects no
    /// HEAP row) and that <c>key_ordinal</c> puts the end column first.
    /// </summary>
    [TestMethod]
    public void HistoryIndex_AutoCreatedSibling_IsClusteredOnPeriodEndThenStart()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        AreEqual("ix_CustomersHistory", simulation.ExecuteScalar("select name from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));
        AreEqual(1, simulation.ExecuteScalar("select index_id from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));
        AreEqual("CLUSTERED", simulation.ExecuteScalar("select type_desc from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));
        IsFalse((bool)simulation.ExecuteScalar("select is_unique from sys.indexes where object_id = object_id('dbo.CustomersHistory')")!);
        // Exactly one row: the clustered entry suppresses the HEAP row.
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));
        AreEqual("Vt", simulation.ExecuteScalar("select c.name from sys.index_columns ic join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = object_id('dbo.CustomersHistory') and ic.key_ordinal = 1"));
        AreEqual("Vf", simulation.ExecuteScalar("select c.name from sys.index_columns ic join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = object_id('dbo.CustomersHistory') and ic.key_ordinal = 2"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.index_columns where object_id = object_id('dbo.CustomersHistory') and (is_descending_key = 1 or is_included_column = 1)"));
    }

    [TestMethod]
    public void HistoryIndex_AutoNamedSibling_TakesIxPrefixedName()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table Customers (
                Id int not null primary key,
                Vf datetime2 generated always as row start not null,
                Vt datetime2 generated always as row end not null,
                period for system_time (Vf, Vt)
            ) with (system_versioning = on)
            """);
        var historyName = (string)simulation.ExecuteScalar("select name from sys.tables where temporal_type = 1")!;
        StartsWith("MSSQL_TemporalHistoryFor_", historyName);
        AreEqual($"ix_{historyName}", simulation.ExecuteScalar("select name from sys.indexes where object_id = (select object_id from sys.tables where temporal_type = 1)"));
    }

    [TestMethod]
    public void HistoryIndex_SiblingBuiltByAlter_GetsTheSameIndex()
    {
        // The ALTER path builds a named-but-missing history table from the
        // base's shape, so it carries the cleanup index too.
        var simulation = new Simulation();
        simulation.ExecuteBatches("""
            create table Customers (
                Id int not null primary key,
                Vf datetime2 generated always as row start not null,
                Vt datetime2 generated always as row end not null,
                period for system_time (Vf, Vt)
            );
            """,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        AreEqual("ix_CustomersHistory", simulation.ExecuteScalar("select name from sys.indexes where object_id = object_id('dbo.CustomersHistory') and type_desc = 'CLUSTERED'"));
    }

    [TestMethod]
    public void HistoryIndex_SurfacesThroughSpHelpindex()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateTemporalCustomers);
        using var reader = simulation.ExecuteReader("exec sp_helpindex 'dbo.CustomersHistory'");
        IsTrue(reader.Read());
        AreEqual("ix_CustomersHistory", reader.GetString(0));
        AreEqual("clustered located on PRIMARY", reader.GetString(1));
        AreEqual("Vt, Vf", reader.GetString(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void HistoryIndex_AdoptedExistingTable_StaysAHeap()
    {
        // Probe-confirmed: real only builds the index when it builds the
        // table — an adopted history table keeps whatever indexing it had.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateUnversionedTemporalPair,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        AreEqual("HEAP", simulation.ExecuteScalar("select type_desc from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));
    }

    [TestMethod]
    public void HistoryRetention_FiniteOnHeapHistory_RaisesMsg13765State1()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(CreateUnversionedTemporalPair);
        // Same ALTER that turns versioning on.
        var error = simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            13765);
        AreEqual("Setting finite retention period failed on system-versioned temporal table 'simulated.dbo.Customers' because the history table 'simulated.dbo.CustomersHistory' does not contain required clustered index. Consider creating a clustered columnstore or B-tree index starting with the column that matches end of SYSTEM_TIME period, on the history table.", error.Message);
        AreEqual((byte)1, error.State);
        // Nothing linked: the whole statement is refused.
        AreEqual((byte)0, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'Customers'"));

        // And the re-issue order — versioning already on, retention added later.
        _ = simulation.ExecuteNonQuery("alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        var reissue = simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            13765);
        AreEqual((byte)1, reissue.State);
        AreEqual("INFINITE", simulation.ExecuteScalar("select history_retention_period_unit_desc from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void HistoryRetention_FiniteWithNonclusteredEndIndex_RaisesMsg13765State1()
    {
        // State 1 is "no clustered index at all" — a nonclustered index on the
        // right columns doesn't count (probe-confirmed).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"{CreateUnversionedTemporalPair} create index ix_h on CustomersHistory (Vt, Vf);");
        var error = simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            13765);
        AreEqual((byte)1, error.State);
    }

    [TestMethod]
    public void HistoryRetention_FiniteWithWrongLeadingClustered_RaisesMsg13765State2()
    {
        // State 2 is "a clustered index that leads with another column"
        // (probe-confirmed, from both the versioning-on and re-issue paths).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"{CreateUnversionedTemporalPair} create clustered index ix_h on CustomersHistory (Vf, Vt);");
        var error = simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            13765);
        AreEqual((byte)2, error.State);

        _ = simulation.ExecuteNonQuery("alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory))");
        AreEqual((byte)2, simulation.AssertSqlError(
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            13765).State);
    }

    [TestMethod]
    public void HistoryRetention_FiniteWithEndLeadingClustered_IsAccepted()
    {
        // The requirement is the leading key column alone: the columns after
        // it and the ASC / DESC direction are both irrelevant.
        foreach (var keys in new[] { "Vt", "Vt desc", "Vt, Vf", "Vt desc, Vf", "Vt, Id" })
        {
            var simulation = new Simulation();
            simulation.ExecuteBatches(
                $"{CreateUnversionedTemporalPair} create clustered index ix_h on CustomersHistory ({keys});",
                "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))");
            AreEqual("MONTH", simulation.ExecuteScalar("select history_retention_period_unit_desc from sys.tables where name = 'Customers'"));
        }
    }

    [TestMethod]
    public void HistoryRetention_InfiniteOnHeapHistory_IsAccepted()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateUnversionedTemporalPair,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = infinite))");
        AreEqual("INFINITE", simulation.ExecuteScalar("select history_retention_period_unit_desc from sys.tables where name = 'Customers'"));
    }

    [TestMethod]
    public void HistoryRetention_CreateTableWithHeapHistory_RaisesMsg13765AndLeavesNoBase()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table CustomersHistory (Id int not null, Name nvarchar(30) not null, Vf datetime2 not null, Vt datetime2 not null)");
        var error = simulation.AssertSqlError(
            CreateTemporalCustomers.Replace("hidden ", "", StringComparison.Ordinal)
                .Replace("history_table = dbo.CustomersHistory", "history_table = dbo.CustomersHistory, history_retention_period = 3 months", StringComparison.Ordinal),
            13765);
        AreEqual((byte)1, error.State);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 'Customers'"));
        AreEqual((byte)0, simulation.ExecuteScalar("select temporal_type from sys.tables where name = 'CustomersHistory'"));
    }

    [TestMethod]
    public void HistoryIndex_DropWhileFiniteRetention_RaisesMsg13766()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))");
        simulation.AssertSqlError(
            "drop index ix_CustomersHistory on CustomersHistory",
            13766,
            "Cannot drop the clustered index 'dbo.CustomersHistory.ix_CustomersHistory' because it is being used for automatic cleanup of aged data. Consider setting HISTORY_RETENTION_PERIOD to INFINITE on the corresponding system-versioned temporal table if you need to drop this index.");
        // The deprecated two-part DROP INDEX form reports it identically.
        AreEqual(13766, simulation.AssertSqlError("drop index dbo.CustomersHistory.ix_CustomersHistory", 13766).Number);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.CustomersHistory') and type_desc = 'CLUSTERED'"));
    }

    [TestMethod]
    public void HistoryIndex_DropOnceRetentionRelaxed_Succeeds()
    {
        // Real releases the index the moment the base stops needing it —
        // retention back to INFINITE, or versioning off entirely.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = infinite))",
            "drop index ix_CustomersHistory on CustomersHistory");
        AreEqual("HEAP", simulation.ExecuteScalar("select type_desc from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));

        var versioningOff = new Simulation();
        versioningOff.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            "alter table Customers set (system_versioning = off)",
            "drop index ix_CustomersHistory on CustomersHistory");
        AreEqual("HEAP", versioningOff.ExecuteScalar("select type_desc from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));
    }

    [TestMethod]
    public void HistoryIndex_DropNonclusteredWhileFiniteRetention_Succeeds()
    {
        // Msg 13766 pins the clustered index alone; a secondary index on the
        // same history table drops as usual.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "create index ix_extra on CustomersHistory (Id)",
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, history_retention_period = 3 months))",
            "drop index ix_extra on CustomersHistory");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.CustomersHistory')"));
    }

    /// <summary>
    /// A plain table with two <c>datetime2</c> NOT NULL columns takes
    /// <c>ALTER TABLE … ADD PERIOD FOR SYSTEM_TIME</c>: both columns become
    /// GENERATED ALWAYS AS ROW START / END and <c>sys.periods</c> gains its
    /// row, which is the statement WideWorldImporters'
    /// <c>DataLoadSimulation.ReactivateTemporalTablesAfterDataLoad</c> re-arms
    /// its temporal tables with. Probed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void AddPeriod_MarksBothColumnsGeneratedAlways()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table q (Id int not null primary key, Vf datetime2 not null, Vt datetime2 not null)");
        _ = sim.ExecuteNonQuery("alter table q add period for system_time (Vf, Vt)");
        AreEqual("SYSTEM_TIME", sim.ExecuteScalar("select name from sys.periods where object_id = object_id('q')"));
        AreEqual((byte)1, sim.ExecuteScalar("select generated_always_type from sys.columns where object_id = object_id('q') and name = 'Vf'"));
        AreEqual((byte)2, sim.ExecuteScalar("select generated_always_type from sys.columns where object_id = object_id('q') and name = 'Vt'"));
    }

    /// <summary>
    /// The period is live immediately: an INSERT that omits both columns has
    /// them filled, the end at the maximum its precision carries.
    /// </summary>
    [TestMethod]
    public void AddPeriod_ThenInsert_FillsBothPeriodColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table q (Id int not null primary key, Vf datetime2 not null, Vt datetime2 not null);
            alter table q add period for system_time (Vf, Vt);
            insert q (Id) values (1)
            """);
        AreEqual(new DateTime(9999, 12, 31, 23, 59, 59).AddTicks(9999999), sim.ExecuteScalar("select Vt from q"));
    }

    /// <summary>
    /// <c>ADD PERIOD</c> is what <c>SET (SYSTEM_VERSIONING = ON)</c> needs, so
    /// the pair re-arms a table whose period was dropped — the
    /// deactivate-load-reactivate cycle WideWorldImporters scripts.
    /// </summary>
    [TestMethod]
    public void DropPeriod_ThenAddPeriod_ReArmsVersioning()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            CreateTemporalCustomers,
            "alter table Customers set (system_versioning = off)",
            "alter table Customers drop period for system_time",
            "alter table Customers add period for system_time (Vf, Vt)",
            "alter table Customers set (system_versioning = on (history_table = dbo.CustomersHistory, data_consistency_check = on))");
        AreEqual((byte)2, sim.ExecuteScalar("select temporal_type from sys.tables where object_id = object_id('Customers')"));
    }

    /// <summary>
    /// Real's own check order, one shape at a time: the period already being
    /// defined beats every column check, and per column the type check beats
    /// the nullability one, with the start column checked before the end.
    /// </summary>
    [TestMethod]
    [DataRow("Id int not null, Vf datetime2 not null, Vt datetime2 not null", "Vf, Vt", 13597, "Temporal SYSTEM_TIME period is already defined on table 'simulated.dbo.q'.", true)]
    [DataRow("Id int not null, Vf datetime2 not null, Vt datetime2 not null", "Vf, nosuch", 4924, "ADD PERIOD FOR SYSTEM_TIME failed because column 'nosuch' does not exist in table 'q'.", false)]
    [DataRow("Id int not null, Vf datetime not null, Vt datetime2 not null", "Vf, Vt", 13501, "Temporal generated always column 'Vf' has invalid data type.", false)]
    [DataRow("Id int not null, Vf datetime2 null, Vt int not null", "Vf, Vt", 13587, "Period column 'Vf' in a system-versioned temporal table cannot be nullable.", false)]
    [DataRow("Id int not null, Vf datetime2(3) not null, Vt datetime2(7) not null", "Vf, Vt", 13513, "SYSTEM_TIME period columns cannot have different datatype precision.", false)]
    [DataRow("Id int not null, Vf datetime2 not null, Vt datetime2 not null", "Vf, cc", 4924, "ADD PERIOD FOR SYSTEM_TIME failed because column 'cc' does not exist in table 'q'.", false)]
    public void AddPeriod_Refusals(string columns, string periodColumns, int expectedNumber, string expectedMessage, bool addPeriodFirst)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table q ({columns}, cc as Id + 1)");
        if (addPeriodFirst)
            _ = sim.ExecuteNonQuery("alter table q add period for system_time (Vf, Vt)");
        sim.AssertSqlError($"alter table q add period for system_time ({periodColumns})", expectedNumber, expectedMessage);
    }

    /// <summary>
    /// Real refuses the adoption when an existing row's end-of-period value
    /// falls short of the maximum its precision holds; an empty table and one
    /// already at the maximum both pass.
    /// </summary>
    [TestMethod]
    public void AddPeriod_RowWithEndShortOfMaxDatetime_Raises13575()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table q (Id int not null primary key, Vf datetime2 not null, Vt datetime2 not null);
            insert q values (1, '2020-01-01', '2021-01-01')
            """);
        sim.AssertSqlError(
            "alter table q add period for system_time (Vf, Vt)",
            13575,
            "ADD PERIOD FOR SYSTEM_TIME failed because table 'simulated.dbo.q' contains records where end of period is not equal to MAX datetime.");
    }

    [TestMethod]
    public void AddPeriod_RowAlreadyAtMaxDatetime_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table q (Id int not null primary key, Vf datetime2 not null, Vt datetime2 not null);
            insert q values (1, '2020-01-01', '9999-12-31 23:59:59.9999999');
            alter table q add period for system_time (Vf, Vt)
            """);
        AreEqual("SYSTEM_TIME", sim.ExecuteScalar("select name from sys.periods where object_id = object_id('q')"));
    }
}
