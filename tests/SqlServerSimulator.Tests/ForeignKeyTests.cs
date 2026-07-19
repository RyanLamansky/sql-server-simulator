using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for FOREIGN KEY constraints: parse + enforce + cascade.
/// Probe-confirmed wording and behavior against SQL Server 2025 on
/// 2026-05-13. Covers inline + table-level grammar; named + auto-named; the
/// four referential actions (NO ACTION / CASCADE / SET NULL / SET DEFAULT)
/// on ON DELETE and ON UPDATE; NULL-in-FK-column skip; FK references must
/// match PK or UNIQUE (Msg 1776); cascade-cycle rejection at CREATE (Msg 1785);
/// DROP TABLE protection (Msg 3726); the sys.foreign_keys /
/// sys.foreign_key_columns catalog surface; and rollback of cascade effects
/// across statement-atomic and explicit-transaction boundaries.
/// </summary>
[TestClass]
public sealed class ForeignKeyTests
{
    [TestMethod]
    public void Inline_InsertChild_WithoutParent_RaisesMsg547()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            insert c values (1, 99)
            """, 547);
        Assert.Contains("FOREIGN KEY constraint", ex.Message);
        Assert.Contains("table \"dbo.p\"", ex.Message);
        Assert.Contains("column 'id'", ex.Message);
    }

    [TestMethod]
    public void Inline_InsertChild_WithMatchingParent_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            insert p values (1);
            insert c values (1, 1);
            select count(*) from c
            """));

    [TestMethod]
    public void TableLevel_Composite_Match_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table p (a int not null, b int not null, primary key (a, b));
            create table c (
                id int not null primary key,
                ra int not null,
                rb int not null,
                foreign key (ra, rb) references p(a, b));
            insert p values (1, 2);
            insert c values (10, 1, 2);
            select count(*) from c
            """));

    [TestMethod]
    public void TableLevel_Composite_Mismatch_RaisesMsg547_WithoutColumnSuffix()
    {
        // Composite FK omits the column phrase (probe-confirmed).
        var ex = new Simulation().AssertSqlError("""
            create table p (a int not null, b int not null, primary key (a, b));
            create table c (id int not null primary key, ra int not null, rb int not null, foreign key (ra, rb) references p(a, b));
            insert p values (1, 2);
            insert c values (10, 9, 9)
            """, 547);
        Assert.DoesNotContain("column '", ex.Message);
    }

    [TestMethod]
    public void UpdateChild_ToMissingParent_RaisesMsg547()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            insert p values (1);
            insert c values (1, 1);
            update c set p_id = 99
            """, 547);
        Assert.Contains("UPDATE statement conflicted", ex.Message);
    }

    [TestMethod]
    public void DeleteParent_WithChildren_RaisesMsg547_OnReferenceConstraint()
    {
        // Parent-side wording: "REFERENCE constraint" (singular) and the
        // child table / column appear, not the parent.
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            insert p values (1);
            insert c values (1, 1);
            delete p where id = 1
            """, 547);
        Assert.Contains("REFERENCE constraint", ex.Message);
        Assert.Contains("table \"dbo.c\"", ex.Message);
        Assert.Contains("column 'p_id'", ex.Message);
    }

    [TestMethod]
    public void UpdateParent_KeyColumn_WithChildren_RaisesMsg547()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            insert p values (1);
            insert c values (1, 1);
            update p set id = 99 where id = 1
            """, 547);
        Assert.Contains("REFERENCE constraint", ex.Message);
    }

    [TestMethod]
    public void DropParent_WhenReferenced_RaisesMsg3726()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            drop table p
            """, 3726);
        Assert.Contains("Could not drop object 'p'", ex.Message);
    }

    [TestMethod]
    public void DropChild_DetachesIncomingRefs_AndAllowsDroppingParent()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            drop table c;
            drop table p;
            select count(*) from sys.tables where name = 'p'
            """));

    [TestMethod]
    public void Null_InFkColumn_SkipsCheck()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null references p(id));
            insert c values (1, null);
            select count(*) from c
            """));

    /// <summary>
    /// Probe-confirmed: a NULL in any FK column makes the entire FK check
    /// pass for that row (even with a non-NULL on the other side).
    /// </summary>
    [TestMethod]
    public void PartialNull_InCompositeFk_SkipsCheck()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table p (a int not null, b int not null, primary key (a, b));
            create table c (id int not null primary key, ra int null, rb int null, foreign key (ra, rb) references p(a, b));
            insert c values (1, null, 99), (2, 1, null);
            select count(*) from c
            """));

    [TestMethod]
    public void FkReferencingNonKey_RaisesMsg1776()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null, b int not null);
            create table c (id int not null primary key, ra int not null references p(id))
            """, 1776);
        Assert.Contains("no primary or candidate keys", ex.Message);
    }

    [TestMethod]
    public void FkReferencingUniqueColumn_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key, code int not null unique);
            create table c (id int not null primary key, p_code int not null references p(code));
            insert p values (1, 100);
            insert c values (1, 100);
            select count(*) from c
            """));

    [TestMethod]
    public void OnDeleteCascade_DeletesChildren()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id) on delete cascade);
            insert p values (10), (20);
            insert c values (1, 10), (2, 10), (3, 20);
            delete p where id = 10;
            select count(*) from c
            """));

    [TestMethod]
    public void OnDeleteSetNull_NullsChildFkColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null references p(id) on delete set null);
            insert p values (10), (20);
            insert c values (1, 10), (2, 10), (3, 20);
            delete p where id = 10
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from c where p_id is null"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from c where p_id = 20"));
    }

    [TestMethod]
    public void OnDeleteSetDefault_UsesColumnDefault()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null default 20 references p(id) on delete set default);
            insert p values (10), (20);
            insert c values (1, 10), (2, 10);
            delete p where id = 10;
            select count(*) from c where p_id = 20
            """));

    [TestMethod]
    public void OnUpdateCascade_RewritesChildFkColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id) on update cascade);
            insert p values (1), (2);
            insert c values (10, 1), (20, 1), (30, 2);
            update p set id = 99 where id = 1
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from c where p_id = 99"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from c where p_id = 2"));
    }

    [TestMethod]
    public void CascadeChainDelete_PropagatesAcrossMultipleHops()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (id int not null primary key);
            create table b (id int not null primary key, a_id int not null references a(id) on delete cascade);
            create table cc (id int not null primary key, b_id int not null references b(id) on delete cascade);
            insert a values (1);
            insert b values (10, 1);
            insert cc values (100, 10);
            delete a where id = 1
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from a"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from b"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from cc"));
    }

    [TestMethod]
    public void NamedFk_ProducesUserSuppliedName_InSysObjects()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null constraint fk_c_to_p references p(id))
            """);
        AreEqual("fk_c_to_p", sim.ExecuteScalar("select name from sys.foreign_keys where parent_object_id = object_id('c')"));
        IsFalse((bool)sim.ExecuteScalar("select is_system_named from sys.foreign_keys where parent_object_id = object_id('c')")!);
    }

    [TestMethod]
    public void AutoNamedFk_HasPrefixFK()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id))
            """);
        var name = (string)sim.ExecuteScalar("select name from sys.foreign_keys where parent_object_id = object_id('c')")!;
        IsTrue(name.StartsWith("FK__", StringComparison.Ordinal));
        IsTrue((bool)sim.ExecuteScalar("select is_system_named from sys.foreign_keys where parent_object_id = object_id('c')")!);
    }

    [TestMethod]
    public void SysForeignKeys_HasExpectedShape()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id) on delete cascade)
            """);
        using var reader = sim.ExecuteReader("""
            select type, type_desc, delete_referential_action, delete_referential_action_desc,
                   update_referential_action, update_referential_action_desc
            from sys.foreign_keys where parent_object_id = object_id('c')
            """);
        IsTrue(reader.Read());
        AreEqual("F ", reader.GetString(0));
        AreEqual("FOREIGN_KEY_CONSTRAINT", reader.GetString(1));
        AreEqual((byte)1, reader.GetByte(2));
        AreEqual("CASCADE", reader.GetString(3));
        AreEqual((byte)0, reader.GetByte(4));
        AreEqual("NO_ACTION", reader.GetString(5));
    }

    [TestMethod]
    public void SysForeignKeyColumns_Composite_EmitsOneRowPerPair()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table p (a int not null, b int not null, primary key (a, b));
            create table c (id int not null primary key, ra int not null, rb int not null, foreign key (ra, rb) references p(a, b));
            select count(*) from sys.foreign_key_columns where parent_object_id = object_id('c')
            """));

    [TestMethod]
    public void SysObjects_FkRow_HasTypeFAndF_AsTwoCharCode()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id))
            """);
        AreEqual("F ", sim.ExecuteScalar("select type from sys.objects where type = 'F'"));
        AreEqual("FOREIGN_KEY_CONSTRAINT", sim.ExecuteScalar("select type_desc from sys.objects where type = 'F'"));
    }

    [TestMethod]
    public void CascadeCycle_SelfReference_RaisesMsg1785_AtCreate()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (id int not null primary key, parent_id int null references t(id) on delete cascade)",
            1785);
        Assert.Contains("may cause cycles or multiple cascade paths", ex.Message);
    }

    [TestMethod]
    public void SelfReference_NoAction_Allowed()
    {
        // Self-referencing FK uses "FOREIGN KEY SAME TABLE" wording (probe-confirmed).
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, parent_id int null references t(id));
            insert t values (1, null);
            insert t values (2, 1);
            insert t values (3, 99)
            """, 547);
        Assert.Contains("FOREIGN KEY SAME TABLE", ex.Message);
    }

    [TestMethod]
    public void MultiRowInsert_OneFkViolation_RollsBackPriorRows()
    {
        // Multi-row VALUES with the second row violating FK — statement-atomic
        // rollback drops the first row too.
        var sim = new Simulation();
        _ = sim.AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            insert p values (1);
            insert c values (1, 1), (2, 99)
            """, 547);
        AreEqual(0, sim.ExecuteScalar("select count(*) from c"));
    }

    [TestMethod]
    public void TableLevelNamedFk_NameRoundTrips()
        => AreEqual("fk_named", new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (
                id int not null primary key,
                p_id int not null,
                constraint fk_named foreign key (p_id) references p(id));
            select name from sys.foreign_keys where parent_object_id = object_id('c')
            """));

    [TestMethod]
    public void MultipleFksOnOneTable_AllRegistered()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table a (id int not null primary key);
            create table b (id int not null primary key);
            create table c (id int not null primary key, a_id int not null references a(id), b_id int not null references b(id));
            select count(*) from sys.foreign_keys where parent_object_id = object_id('c')
            """));

    [TestMethod]
    public void FkInsideTableVariable_Msg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int not null primary key, p_id int not null references something(id))",
            102);

    [TestMethod]
    public void MergeFkViolation_RaisesMerge547()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id));
            insert p values (1);
            insert c values (1, 1);
            merge c as t
            using (values (1, 99)) as s(id, p_id)
            on t.id = s.id
            when matched then update set p_id = s.p_id;
            """, 547);
        Assert.Contains("MERGE statement conflicted", ex.Message);
    }

    // --- Transaction rollback over FK cascades --------------------------
    //
    // Probe-confirmed against SQL Server 2025 (2026-05-13): the entire
    // cascade chain is captured in the active transaction's undo log;
    // a ROLLBACK restores every row touched (parent + every cascaded
    // child / great-grandchild), and SET NULL / SET DEFAULT / ON UPDATE
    // CASCADE all roll back the rewrites symmetrically. Statement-atomic
    // rollback for a NO ACTION leaf restores every row deleted higher in
    // the chain.

    [TestMethod]
    public void RollbackTran_RestoresCascadedDeletes()
    {
        // Three-table cascade chain (a → b → c). DELETE on the root inside
        // a tx empties all three; ROLLBACK restores all three. Single-batch
        // form: cascade + rollback + final count in one ExecuteScalar.
        AreEqual(3, new Simulation().ExecuteScalar("""
            create table a (id int not null primary key);
            create table b (id int not null primary key, a_id int not null references a(id) on delete cascade);
            create table cc (id int not null primary key, b_id int not null references b(id) on delete cascade);
            insert a values (1);
            insert b values (10, 1);
            insert cc values (100, 10);
            begin tran;
            delete a where id = 1;
            rollback;
            select (select count(*) from a) + (select count(*) from b) + (select count(*) from cc)
            """));
    }

    [TestMethod]
    public void RollbackTran_RestoresSetNullCascadeRewrites()
    {
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null references p(id) on delete set null);
            insert p values (10);
            insert c values (1, 10), (2, 10);
            begin tran;
            delete p where id = 10
            """).ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from c where p_id is null").ExecuteScalar());

        // Both the parent row and the child's original p_id values come back.
        _ = conn.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select count(*) from p").ExecuteScalar());
        AreEqual(0, conn.CreateCommand("select count(*) from c where p_id is null").ExecuteScalar());
        AreEqual(2, conn.CreateCommand("select count(*) from c where p_id = 10").ExecuteScalar());
    }

    [TestMethod]
    public void RollbackTran_RestoresSetDefaultCascadeRewrites()
    {
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null default 20 references p(id) on delete set default);
            insert p values (10), (20);
            insert c values (1, 10), (2, 10);
            begin tran;
            delete p where id = 10
            """).ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from c where p_id = 20").ExecuteScalar());

        _ = conn.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from c where p_id = 10").ExecuteScalar());
        AreEqual(2, conn.CreateCommand("select count(*) from p").ExecuteScalar());
    }

    [TestMethod]
    public void RollbackTran_RestoresOnUpdateCascadeRewrites()
    {
        // ON UPDATE CASCADE rewrites every matching child's FK column to the
        // parent's new value. ROLLBACK reverses both the parent UPDATE and
        // the child rewrites.
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int not null references p(id) on update cascade);
            insert p values (1);
            insert c values (10, 1), (20, 1);
            begin tran;
            update p set id = 99 where id = 1
            """).ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from c where p_id = 99").ExecuteScalar());

        _ = conn.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select count(*) from p where id = 1").ExecuteScalar());
        AreEqual(2, conn.CreateCommand("select count(*) from c where p_id = 1").ExecuteScalar());
        AreEqual(0, conn.CreateCommand("select count(*) from c where p_id = 99").ExecuteScalar());
    }

    [TestMethod]
    public void Savepoint_RollbackToSavepoint_RestoresCascadeChain()
    {
        // SAVE TRAN / ROLLBACK TRAN <name> is the EF SaveChanges idiom — the
        // simulator's per-tx undo log records the savepoint marker and
        // rewinds to it. Cascade effects across the chain land between the
        // savepoint and the rollback target, so they all come back. Outer
        // tx survives the savepoint rollback and commits cleanly.
        AreEqual(2, new Simulation().ExecuteScalar("""
            create table a (id int not null primary key);
            create table b (id int not null primary key, a_id int not null references a(id) on delete cascade);
            insert a values (1);
            insert b values (10, 1);
            begin tran;
            save tran sp1;
            delete a where id = 1;
            rollback tran sp1;
            commit;
            select (select count(*) from a) + (select count(*) from b)
            """));
    }

    [TestMethod]
    public void PartialCascadeBlockedByNoAction_StatementAtomicRollback_LeavesAllRowsIntact()
    {
        // Three-hop cascade chain (a → b → c → block). The leaf FK is
        // NO ACTION; the cascade reaches it after deleting a / b / c and
        // raises Msg 547. Statement-atomic rollback restores every row
        // even though no explicit BEGIN TRAN is active. Probe-confirmed:
        // all four tables back to one row each.
        var sim = new Simulation();
        var ex = sim.AssertSqlError("""
            create table a (id int not null primary key);
            create table b (id int not null primary key, a_id int not null references a(id) on delete cascade);
            create table cc (id int not null primary key, b_id int not null references b(id) on delete cascade);
            create table blk (id int not null primary key, c_id int not null references cc(id));
            insert a values (1);
            insert b values (10, 1);
            insert cc values (100, 10);
            insert blk values (1000, 100);
            delete a where id = 1
            """, 547);
        Assert.Contains("REFERENCE constraint", ex.Message);
        AreEqual(4, sim.ExecuteScalar(
            "select (select count(*) from a) + (select count(*) from b) + (select count(*) from cc) + (select count(*) from blk)"));
    }

    [TestMethod]
    public void PartialCascadeBlockedInsideExplicitTx_StatementRollback_LeavesOuterTxAlive()
    {
        // Inside an explicit BEGIN TRAN, a failed cascade-blocking statement
        // unwinds just that statement's undo entries — the outer tx stays
        // alive and prior writes inside it persist until COMMIT or ROLLBACK.
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table a (id int not null primary key);
            create table b (id int not null primary key, a_id int not null references a(id) on delete cascade);
            create table blk (id int not null primary key, b_id int not null references b(id));
            insert a values (1), (2);
            insert b values (10, 1), (20, 2);
            insert blk values (1000, 10);
            begin tran;
            delete a where id = 2
            """).ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select count(*) from a").ExecuteScalar());

        // This delete cascades a → b but blk's NO ACTION blocks the b row's
        // delete. Statement-atomic rollback restores the cascade-deleted
        // rows; the earlier successful delete inside the same tx persists
        // until ROLLBACK closes the tx.
        var ex = Throws<DbException>(() => conn.CreateCommand("delete a where id = 1").ExecuteNonQuery());
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
        AreEqual(1, conn.CreateCommand("select count(*) from a").ExecuteScalar());
        AreEqual(1, conn.CreateCommand("select count(*) from b").ExecuteScalar());
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());

        _ = conn.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from a").ExecuteScalar());
        AreEqual(2, conn.CreateCommand("select count(*) from b").ExecuteScalar());
    }
}
