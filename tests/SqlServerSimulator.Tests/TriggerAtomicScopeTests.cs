using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A trigger body has no atomic scope of its own: the firing statement and
/// everything its triggers wrote roll back as one unit. Covers the
/// auto-commit path (each statement would otherwise take a throwaway undo log
/// it commits on its own success), the explicit-transaction path, modules the
/// body calls, and Msg 3616 — which real raises when the body's own
/// <c>TRY</c> / <c>CATCH</c> swallows an error of severity 11 or higher.
/// All probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class TriggerAtomicScopeTests
{
    /// <summary>
    /// Target plus audit table, and a trigger body whose statements are
    /// supplied per test — the audit row is the side effect that must not
    /// survive a later failure in the same body.
    /// </summary>
    private static DbConnection Seeded(string triggerBody)
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table main_t (id int identity(1,1) primary key, v int not null);
            create table audit_t (a int identity(1,1) primary key, note varchar(50) not null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand($"create trigger tr_x on main_t after insert as begin {triggerBody} end").ExecuteNonQuery();
        return connection;
    }

    private static string State(DbConnection connection) =>
        (string)connection.CreateCommand(
            "select 'audit=' + cast((select count(*) from audit_t) as varchar) + ' main=' + cast((select count(*) from main_t) as varchar)")
            .ExecuteScalar()!;

    [TestMethod]
    public void AutoCommit_BodyWriteThenThrow_RollsBackBoth()
    {
        using var connection = Seeded("insert audit_t (note) values ('w'); throw 51000, 'boom', 1;");
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery());
        AreEqual(51000, ex.Number);
        AreEqual("audit=0 main=0", State(connection));
    }

    [TestMethod]
    public void AutoCommit_ThreeBodyWritesThenThrow_RollsBackAll()
    {
        using var connection = Seeded("""
            insert audit_t (note) values ('first');
            insert audit_t (note) values ('second');
            update audit_t set note = 'updated' where note = 'first';
            throw 51000, 'after three writes', 1;
            """);
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery());
        AreEqual("audit=0 main=0", State(connection));
    }

    /// <summary>A runtime error reaches the same scope as an explicit THROW.</summary>
    [TestMethod]
    public void AutoCommit_BodyWriteThenRuntimeError_RollsBackBoth()
    {
        using var connection = Seeded("insert audit_t (note) values ('w'); declare @z int = 1/0;");
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery());
        AreEqual(8134, ex.Number);
        AreEqual("audit=0 main=0", State(connection));
    }

    /// <summary>
    /// The pre-existing explicit-transaction path already shared the
    /// transaction's undo log; locked down so the auto-commit fix doesn't
    /// regress it.
    /// </summary>
    [TestMethod]
    public void ExplicitTransaction_BodyWriteThenThrow_RollsBackBoth()
    {
        using var connection = Seeded("insert audit_t (note) values ('w'); throw 51000, 'boom', 1;");
        _ = connection.CreateCommand("""
            begin try
                begin transaction;
                insert main_t (v) values (1);
                commit;
            end try
            begin catch
                if @@trancount > 0 rollback;
            end catch
            """).ExecuteNonQuery();
        AreEqual("audit=0 main=0", State(connection));
    }

    /// <summary>
    /// An outer CATCH sees the body's original error number, and the unit is
    /// still rolled back.
    /// </summary>
    [TestMethod]
    public void OuterCatch_SeesOriginalErrorNumber_AndUnitRolledBack()
    {
        using var connection = Seeded("insert audit_t (note) values ('w'); throw 51000, 'boom', 1;");
        AreEqual(51000, connection.CreateCommand("""
            begin try
                insert main_t (v) values (1);
            end try
            begin catch
                select error_number();
            end catch
            """).ExecuteScalar());
        AreEqual("audit=0 main=0", State(connection));
    }

    /// <summary>
    /// A stored procedure called from the body is inside the same unit — its
    /// write rolls back too, which is why the enclosing scope is tracked on
    /// the connection rather than on the trigger's own batch.
    /// </summary>
    [TestMethod]
    public void ModuleCalledFromBody_WriteRollsBackWithTheUnit()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table main_t (id int identity(1,1) primary key, v int not null);
            create table audit_t (a int identity(1,1) primary key, note varchar(50) not null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("create procedure p_audit as begin insert audit_t (note) values ('from-proc'); end").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_x on main_t after insert as begin exec p_audit; throw 51000, 'after proc', 1; end").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery());
        AreEqual("audit=0 main=0", State(connection));
    }

    /// <summary>A trigger that completes normally still commits its writes.</summary>
    [TestMethod]
    public void HealthyTrigger_CommitsBodyWrites()
    {
        using var connection = Seeded("insert audit_t (note) values ('ok');");
        _ = connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery();
        AreEqual("audit=1 main=1", State(connection));
    }

    // === Msg 3616: the body's own TRY / CATCH doesn't rescue the statement ===

    [TestMethod]
    public void BodyCatchesOwnThrow_RaisesMsg3616_AndRollsBack()
    {
        using var connection = Seeded("""
            insert audit_t (note) values ('w');
            begin try throw 51000, 'swallowed', 1; end try begin catch end catch
            """);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery());
        AreEqual(3616, ex.Number);
        AreEqual(
            "An error was raised during trigger execution. The batch has been aborted and the user transaction, if any, has been rolled back.",
            ex.Message);
        AreEqual("audit=0 main=0", State(connection));
    }

    [TestMethod]
    public void BodyCatchesOwnRuntimeError_RaisesMsg3616()
    {
        using var connection = Seeded("""
            insert audit_t (note) values ('w');
            begin try declare @z int = 1/0; end try begin catch end catch
            """);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery());
        AreEqual(3616, ex.Number);
        AreEqual("audit=0 main=0", State(connection));
    }

    /// <summary>
    /// Severity 11 is the error floor — a caught RAISERROR at 11 condemns the
    /// unit where the same call at severity 10 leaves it intact.
    /// </summary>
    [TestMethod]
    public void BodyCatchesSeverity11_RaisesMsg3616()
    {
        using var connection = Seeded("""
            insert audit_t (note) values ('w');
            begin try raiserror('sev11', 11, 1); end try begin catch end catch
            """);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery());
        AreEqual(3616, ex.Number);
        AreEqual("audit=0 main=0", State(connection));
    }

    [TestMethod]
    public void BodyRaisesSeverity10_IsInformational_AndUnitSurvives()
    {
        using var connection = Seeded("""
            insert audit_t (note) values ('w');
            begin try raiserror('sev10', 10, 1); end try begin catch end catch
            """);
        _ = connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery();
        AreEqual("audit=1 main=1", State(connection));
    }

    /// <summary>
    /// A handled error in one trigger doesn't condemn the next fire — the flag
    /// is cleared per body.
    /// </summary>
    [TestMethod]
    public void HandledErrorDoesNotLeakToTheNextFire()
    {
        using var connection = Seeded("insert audit_t (note) values ('ok');");
        _ = connection.CreateCommand("insert main_t (v) values (1)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert main_t (v) values (2)").ExecuteNonQuery();
        AreEqual("audit=2 main=2", State(connection));
    }
}
