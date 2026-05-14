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

    [TestMethod]
    public void ForSystemTimeAll_UnionsCurrentAndHistory()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            CreateTemporalCustomers,
            "insert Customers (Id, Name) values (1, 'a'), (2, 'b')",
            "update Customers set Name = 'A' where Id = 1");
        AreEqual(3, simulation.ExecuteScalar("select count(*) from Customers for system_time all"));
    }

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
}
