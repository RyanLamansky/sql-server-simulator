using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>ALTER TABLE … (CHECK | NOCHECK) CONSTRAINT (ALL |
/// name [,…])</c> trust-toggle shapes. <c>NOCHECK</c> disables enforcement
/// (sets IsDisabled + IsNotTrusted); bare <c>CHECK</c> re-enables without
/// revalidating (leaves IsNotTrusted = true); <c>WITH CHECK CHECK
/// CONSTRAINT</c> revalidates and clears IsNotTrusted on success.
/// Probe-confirmed against SQL Server 2025 on 2026-05-13.
/// </summary>
[TestClass]
public sealed class AlterTableTrustToggleTests
{
    // --- NOCHECK CONSTRAINT (disable) ---

    [TestMethod]
    public void NoCheckConstraint_FK_DisablesEnforcement()
    {
        var sim = new Simulation();
        // Bulk-import scenario: disable FK, insert orphan, re-enable.
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null constraint fk_cp references p(id));
            alter table c nocheck constraint fk_cp;
            insert c values (1, 999)
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from c"));
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.foreign_keys where name = 'fk_cp'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_not_trusted from sys.foreign_keys where name = 'fk_cp'")!);
    }

    [TestMethod]
    public void NoCheckConstraint_FK_SkipsCascadeOnDelete()
    {
        var sim = new Simulation();
        // Probe-confirmed: disabled CASCADE FK leaves children orphaned when
        // parent is deleted (cascade action is suppressed alongside the
        // NO-ACTION reject).
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null constraint fk_cp references p(id) on delete cascade);
            insert p values (1);
            insert c values (10, 1), (20, 1);
            alter table c nocheck constraint fk_cp;
            delete p
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from c"));
    }

    [TestMethod]
    public void NoCheckConstraint_Check_DisablesPredicate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (qty int constraint ck_q check (qty > 0));
            alter table t nocheck constraint ck_q;
            insert t values (-5)
            """);
        AreEqual(-5, sim.ExecuteScalar("select qty from t"));
    }

    // --- CHECK CONSTRAINT (re-enable without revalidate) ---

    [TestMethod]
    public void CheckConstraint_BareReEnablesWithoutRevalidating()
    {
        var sim = new Simulation();
        // Probe-confirmed gotcha: bare CHECK CONSTRAINT (no WITH CHECK
        // prefix) re-enables enforcement but leaves IsNotTrusted = true.
        _ = sim.ExecuteNonQuery("""
            create table t (qty int constraint ck_q check (qty > 0));
            alter table t nocheck constraint ck_q;
            insert t values (-5);
            alter table t check constraint ck_q
            """);
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.check_constraints where name = 'ck_q'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_not_trusted from sys.check_constraints where name = 'ck_q'")!);
        // New rows are now enforced.
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (-10)"));
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    // --- WITH CHECK CHECK CONSTRAINT (re-validate + re-trust) ---

    [TestMethod]
    public void WithCheckCheckConstraint_FK_RevalidatesAndClearsNotTrusted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null constraint fk_cp references p(id));
            insert p values (1);
            insert c values (10, 1);
            alter table c nocheck constraint fk_cp;
            alter table c with check check constraint fk_cp
            """);
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.foreign_keys where name = 'fk_cp'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_not_trusted from sys.foreign_keys where name = 'fk_cp'")!);
    }

    [TestMethod]
    public void WithCheckCheckConstraint_FK_OrphanRaisesMsg547WithAlterPrefix()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null constraint fk_cp references p(id));
            alter table c nocheck constraint fk_cp;
            insert c values (10, 999);
            alter table c with check check constraint fk_cp
            """, 547);
        Contains("ALTER TABLE statement conflicted with the FOREIGN KEY constraint", ex.Message);
        Contains("\"fk_cp\"", ex.Message);
    }

    [TestMethod]
    public void WithCheckCheckConstraint_Check_BadRowRaisesMsg547()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (qty int constraint ck_q check (qty > 0));
            alter table t nocheck constraint ck_q;
            insert t values (-5);
            alter table t with check check constraint ck_q
            """, 547);
        Contains("ALTER TABLE statement conflicted with the CHECK constraint", ex.Message);
        Contains("\"ck_q\"", ex.Message);
    }

    // --- ALL keyword ---

    [TestMethod]
    public void NoCheckConstraintAll_DisablesEveryFkAndCheckOnTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (
                id int not null primary key,
                p_id int null constraint fk_cp references p(id),
                qty int constraint ck_q check (qty > 0)
            );
            alter table c nocheck constraint all
            """);
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.foreign_keys where name = 'fk_cp'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.check_constraints where name = 'ck_q'")!);
        // Inserts with both violations now succeed.
        AreEqual(1, sim.ExecuteScalar("""
            insert c values (1, 999, -10);
            select count(*) from c
            """));
    }

    [TestMethod]
    public void CheckConstraintAll_BareReEnablesAllWithoutRevalidating()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (
                id int not null primary key,
                p_id int null constraint fk_cp references p(id),
                qty int constraint ck_q check (qty > 0)
            );
            alter table c nocheck constraint all;
            alter table c check constraint all
            """);
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.foreign_keys where name = 'fk_cp'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_not_trusted from sys.foreign_keys where name = 'fk_cp'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.check_constraints where name = 'ck_q'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_not_trusted from sys.check_constraints where name = 'ck_q'")!);
    }

    [TestMethod]
    public void WithCheckCheckConstraintAll_ClearsNotTrusted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (
                id int not null primary key,
                p_id int null constraint fk_cp references p(id),
                qty int constraint ck_q check (qty > 0)
            );
            insert p values (1);
            alter table c nocheck constraint all;
            alter table c with check check constraint all
            """);
        IsFalse((bool)sim.ExecuteScalar("select is_not_trusted from sys.foreign_keys where name = 'fk_cp'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_not_trusted from sys.check_constraints where name = 'ck_q'")!);
    }

    // --- Error paths ---

    [TestMethod]
    public void NoCheckConstraint_NameNotFound_RaisesMsg4917()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter table t nocheck constraint missing
            """, 4917);

    [TestMethod]
    public void WithCheckCheckConstraint_NameNotFound_RaisesMsg4917()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter table t with check check constraint missing
            """, 4917);

    [TestMethod]
    public void MultiName_OneMissing_AtomicNoMutation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, qty int);
            alter table t add constraint ck_q check (qty > 0);
            alter table t nocheck constraint ck_q
            """);
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.check_constraints where name = 'ck_q'")!);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t check constraint ck_q, missing"));
        AreEqual("4917", ex.Data["HelpLink.EvtID"]);
        // ck_q was disabled before; the failed multi-toggle leaves it disabled.
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.check_constraints where name = 'ck_q'")!);
    }

    // --- Bulk-import recipe ---

    [TestMethod]
    public void BulkImport_DisableImportReEnable_FlowEndToEnd()
    {
        var sim = new Simulation();
        // The intended bulk-import use case: disable all constraints, push
        // data (possibly with rows that would have violated), re-enable.
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (
                id int not null primary key,
                p_id int null constraint fk_cp references p(id),
                qty int constraint ck_q check (qty > 0)
            );
            insert p values (1), (2);
            alter table c nocheck constraint all;
            insert c values (10, 1, 5), (20, 2, -3), (30, 99, 10);
            alter table c check constraint all
            """);
        AreEqual(3, sim.ExecuteScalar("select count(*) from c"));
        // After re-enable without WITH CHECK, IsNotTrusted = true; new
        // inserts enforce again so attempting a fresh orphan now fails.
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert c values (40, 999, 1)"));
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }
}
