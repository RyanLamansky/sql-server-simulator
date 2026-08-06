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

    /// <summary>
    /// <c>REFERENCES p</c> with no column list takes the parent's PRIMARY KEY
    /// as the referenced list, so the constraint enforces against it and the
    /// catalog stores the resolved column.
    /// </summary>
    [TestMethod]
    public void FkWithoutReferencedColumnList_UsesParentPrimaryKey()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table p (id int not null primary key, code int not null);
            create table c (id int not null primary key, p_id int null references p, note varchar(10));
            insert p values (1, 100);
            insert c values (10, 1, 'x')
            """).ExecuteNonQuery();

        AreEqual("id", connection.CreateCommand("""
            select pc.name
            from sys.foreign_keys fk
            join sys.foreign_key_columns fkc on fkc.constraint_object_id = fk.object_id
            join sys.columns pc on pc.object_id = fkc.referenced_object_id and pc.column_id = fkc.referenced_column_id
            where fk.parent_object_id = object_id('dbo.c')
            """).ExecuteScalar());

        var ex = ThrowsExactly<SimulatedSqlException>(
            () => connection.CreateCommand("insert c values (11, 99, 'y')").ExecuteNonQuery());
        AreEqual(547, ex.Number);
    }

    /// <summary>
    /// The implied list is the primary key alone, so a parent carrying only a
    /// UNIQUE constraint has nothing to imply and real reports Msg 1773 — the
    /// implicit-reference message, naming the object as <c>schema.table</c> —
    /// rather than the explicit-list Msg 1776. Probed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void FkWithoutReferencedColumnList_ParentHasNoPrimaryKey_RaisesMsg1773()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null unique);
            create table c (pid int constraint fk_c_p references p)
            """, 1773);
        AreEqual(
            "Foreign key 'fk_c_p' has implicit reference to object 'dbo.p' which does not have a primary key defined on it.",
            ex.Message);
    }

    /// <summary>
    /// <c>NO ACTION</c> spelled out is the same as omitting the clause: the
    /// catalog reports NO_ACTION and a delete that would orphan a child is
    /// refused.
    /// </summary>
    [TestMethod]
    public void ExplicitNoAction_ParsesAndBehavesAsTheDefault()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null,
                constraint fk_c_p foreign key (p_id) references p (id) on delete no action on update no action);
            insert p values (1);
            insert c values (10, 1)
            """).ExecuteNonQuery();

        AreEqual("NO_ACTION|NO_ACTION", connection.CreateCommand(
            "select delete_referential_action_desc + '|' + update_referential_action_desc from sys.foreign_keys where name = 'fk_c_p'")
            .ExecuteScalar());

        var ex = ThrowsExactly<SimulatedSqlException>(
            () => connection.CreateCommand("delete p where id = 1").ExecuteNonQuery());
        AreEqual(547, ex.Number);
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

    /// <summary>
    /// The referenced column list must match a key's columns <em>in declared
    /// order</em>. Probe-confirmed against SQL Server 2025:
    /// <c>REFERENCES p(y, x)</c> against <c>UNIQUE (x, y)</c> raises Msg 1776,
    /// so the earlier set-equality match accepted an FK real rejects.
    /// </summary>
    [TestMethod]
    public void CompositeForeignKey_ReversedReferencedOrder_Raises1776()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table p (x int not null, y int not null, constraint uq_p unique (x, y))");

        var ex = sim.AssertSqlError(
            "create table c (a int not null, b int not null, constraint fk_c foreign key (a, b) references p(y, x))",
            1776);
        Assert.AreEqual(
            "There are no primary or candidate keys in the referenced table 'p' that match the referencing column list in the foreign key 'fk_c'.",
            ex.Message);
    }

    /// <summary>Matching order stays accepted — the gate narrows nothing else.</summary>
    [TestMethod]
    public void CompositeForeignKey_MatchingReferencedOrder_Accepted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table p (x int not null, y int not null, constraint uq_p unique (x, y))");
        _ = sim.ExecuteNonQuery(
            "create table c (a int not null, b int not null, constraint fk_c foreign key (a, b) references p(x, y))");
    }

    /// <summary>
    /// <c>SET DEFAULT</c> needs something to set: a NOT NULL referencing column
    /// with no DEFAULT leaves the action no value, which real rejects at
    /// declaration with <b>Msg 1762</b> rather than at the first cascading
    /// delete (probe-confirmed — note the constraint name is double-quoted
    /// here where Msg 1776 single-quotes it).
    /// </summary>
    [TestMethod]
    public void ForeignKeySetDefault_NotNullColumnWithoutDefault_Raises1762()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table p (x int not null constraint pk_p primary key)");

        var ex = sim.AssertSqlError(
            "create table c (a int not null, constraint fk_c foreign key (a) references p(x) on delete set default)",
            1762);
        Assert.AreEqual(
            "Cannot create the foreign key \"fk_c\" with the SET DEFAULT referential action, because one or more referencing not-nullable columns lack a default constraint.",
            ex.Message);
    }

    /// <summary>
    /// A <em>nullable</em> referencing column without a default is accepted —
    /// NULL is the value SET DEFAULT then sets (probe-confirmed), so the gate
    /// keys on nullability rather than on the mere absence of a default.
    /// </summary>
    [TestMethod]
    [DataRow("create table c (a int null, constraint fk_c foreign key (a) references p(x) on delete set default)")]
    [DataRow("create table c (a int not null default (0), constraint fk_c foreign key (a) references p(x) on delete set default)")]
    public void ForeignKeySetDefault_SettableColumn_Accepted(string sql)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table p (x int not null constraint pk_p primary key)");
        _ = sim.ExecuteNonQuery(sql);
    }

    /// <summary>
    /// A PERSISTED computed column is a legal referencing column in all three
    /// declaration forms (probe-confirmed): the inline column tail with the
    /// optional <c>FOREIGN KEY</c> noise phrase, the table-level list, and
    /// <c>ALTER TABLE … ADD CONSTRAINT</c>.
    /// </summary>
    [TestMethod]
    [DataRow("create table c (id int not null primary key, base int not null, cc as base + 1 persisted foreign key references p(id))")]
    [DataRow("create table c (id int not null primary key, base int not null, cc as base + 1 persisted references p(id))")]
    [DataRow("create table c (id int not null primary key, base int not null, cc as base + 1 persisted, constraint fk_c foreign key (cc) references p(id))")]
    public void ComputedPersisted_ReferencingColumn_EnforcesOnInsert(string createChild)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table p (id int not null primary key); {createChild}; insert p values (2)");
        AreEqual(2, sim.ExecuteScalar("insert c (id, base) values (1, 1); select cc from c"));
        var ex = sim.AssertSqlError("insert c (id, base) values (2, 99)", 547);
        Assert.Contains("FOREIGN KEY constraint", ex.Message);
        Assert.Contains("column 'id'", ex.Message);
    }

    [TestMethod]
    public void ComputedPersisted_ReferencingColumn_AlterTableAddConstraint_Enforces()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, base int not null, cc as base + 1 persisted);
            insert p values (2);
            alter table c add constraint fk_c foreign key (cc) references p(id)
            """);
        AreEqual(1, sim.ExecuteScalar("insert c (id, base) values (1, 1); select count(*) from c"));
        _ = sim.AssertSqlError("insert c (id, base) values (2, 99)", 547);
    }

    /// <summary>
    /// The computed value is re-checked when the columns it reads change: an
    /// UPDATE that recomputes the FK column to an orphan value raises Msg 547
    /// even though the UPDATE never names the FK column.
    /// </summary>
    [TestMethod]
    public void ComputedPersisted_ReferencingColumn_UpdateOfUnderlyingColumn_Revalidates()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, base int not null, cc as base + 1 persisted references p(id));
            insert p values (2), (7);
            insert c (id, base) values (1, 1)
            """);
        var ex = sim.AssertSqlError("update c set base = 90", 547);
        Assert.Contains("UPDATE statement conflicted", ex.Message);
        AreEqual(7, sim.ExecuteScalar("update c set base = 6; select cc from c"));
    }

    /// <summary>
    /// Parent-side enforcement reaches the computed child column too: a
    /// NO ACTION parent DELETE raises Msg 547 naming the computed column, and
    /// ON DELETE CASCADE removes the whole child row (the one delete action a
    /// computed referencing column allows, since it never writes the column).
    /// </summary>
    [TestMethod]
    public void ComputedPersisted_ReferencingColumn_ParentDelete_NoActionRaisesAndCascadeRemoves()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, base int not null, cc as base + 1 persisted references p(id));
            insert p values (5);
            insert c (id, base) values (1, 4)
            """);
        var ex = sim.AssertSqlError("delete p where id = 5", 547);
        Assert.Contains("REFERENCE constraint", ex.Message);
        Assert.Contains("column 'cc'", ex.Message);

        var cascade = new Simulation();
        _ = cascade.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, base int not null, cc as base + 1 persisted,
                            constraint fk_c foreign key (cc) references p(id) on delete cascade);
            insert p values (5);
            insert c (id, base) values (1, 4);
            delete p where id = 5
            """);
        AreEqual(0, cascade.ExecuteScalar("select count(*) from c"));
    }

    /// <summary>
    /// A non-persisted computed referencing column is rejected — <b>Msg 1764</b>
    /// from the table-level and ALTER forms, which reach constraint resolution,
    /// and <b>Msg 8183</b> from the inline column form, which real rejects at
    /// parse before resolution (probe-confirmed split).
    /// </summary>
    [TestMethod]
    public void ComputedNonPersisted_ReferencingColumn_TableLevel_Raises1764()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, base int not null, cc as base + 1,
                            constraint fk_c foreign key (cc) references p(id))
            """, 1764);
        AreEqual("Computed Column 'cc' in table 'c' is invalid for use in 'FOREIGN KEY CONSTRAINT' because it is not persisted.", ex.Message);
    }

    [TestMethod]
    public void ComputedNonPersisted_ReferencingColumn_AlterTableAddConstraint_Raises1764()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, base int not null, cc as base + 1)
            """);
        var ex = sim.AssertSqlError("alter table c add constraint fk_c foreign key (cc) references p(id)", 1764);
        AreEqual("Computed Column 'cc' in table 'c' is invalid for use in 'FOREIGN KEY CONSTRAINT' because it is not persisted.", ex.Message);
    }

    [TestMethod]
    [DataRow("create table c (id int not null primary key, base int not null, cc as base + 1 foreign key references p(id))")]
    [DataRow("create table c (id int not null primary key, base int not null, cc as base + 1 constraint fk_c references p(id))")]
    public void ComputedNonPersisted_ReferencingColumn_Inline_Raises8183(string createChild)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table p (id int not null primary key)");
        var ex = sim.AssertSqlError(createChild, 8183);
        AreEqual(
            "Only UNIQUE or PRIMARY KEY constraints can be created on computed columns, while CHECK, FOREIGN KEY, and NOT NULL constraints require that computed columns be persisted.",
            ex.Message);
    }

    /// <summary>
    /// Referential actions that would have to <em>write</em> the computed
    /// referencing column are rejected at declaration: <b>Msg 1765</b> for
    /// ON DELETE SET NULL / SET DEFAULT, <b>Msg 1715</b> for every ON UPDATE
    /// action but NO ACTION. Probed precedence puts 1765 ahead of 1715.
    /// </summary>
    [TestMethod]
    [DataRow("on delete set null", 1765, "Only NO ACTION and CASCADE referential delete actions are allowed for referencing computed column 'cc'.")]
    [DataRow("on delete set default", 1765, "Only NO ACTION and CASCADE referential delete actions are allowed for referencing computed column 'cc'.")]
    [DataRow("on delete set null on update cascade", 1765, "Only NO ACTION and CASCADE referential delete actions are allowed for referencing computed column 'cc'.")]
    [DataRow("on update cascade", 1715, "Only NO ACTION referential update action is allowed for referencing computed column 'cc'.")]
    [DataRow("on update set null", 1715, "Only NO ACTION referential update action is allowed for referencing computed column 'cc'.")]
    public void ComputedPersisted_ReferencingColumn_WritingReferentialAction_Rejected(string actions, int expectedNumber, string expectedTail)
    {
        var ex = new Simulation().AssertSqlError($"""
            create table p (id int not null primary key);
            create table c (id int not null primary key, base int null, cc as base + 1 persisted,
                            constraint fk_c foreign key (cc) references p(id) {actions})
            """, expectedNumber);
        AreEqual($"Foreign key 'fk_c' creation failed. {expectedTail}", ex.Message);
    }

    /// <summary>
    /// The referenced (parent) side accepts a PERSISTED computed column when it
    /// carries the PK / UNIQUE the FK needs, and enforces against its stored
    /// value — probe-confirmed, including the Msg 547 naming the computed
    /// column. Real rejects a <em>non-persisted</em> computed referenced column
    /// with Msg 1784, which the simulator never reaches: UNIQUE on a
    /// non-persisted computed column is itself unbuilt, so the parent key can't
    /// exist in the first place.
    /// </summary>
    [TestMethod]
    public void ComputedPersisted_ReferencedColumn_EnforcesAgainstStoredValue()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key, b int not null, pc as b * 2 persisted unique);
            create table c (id int not null primary key, r int null, constraint fk_c foreign key (r) references p(pc));
            insert p (id, b) values (1, 21)
            """);
        AreEqual(1, sim.ExecuteScalar("insert c values (1, 42); select count(*) from c"));
        var ex = sim.AssertSqlError("insert c values (2, 43)", 547);
        Assert.Contains("column 'pc'", ex.Message);
    }

    /// <summary>
    /// The <c>FOREIGN KEY</c> noise phrase an inline column-level FK may carry
    /// ahead of <c>REFERENCES</c> is accepted on an ordinary column too, named
    /// or not.
    /// </summary>
    [TestMethod]
    [DataRow("create table c (id int not null primary key, r int foreign key references p(id))")]
    [DataRow("create table c (id int not null primary key, r int constraint fk_c foreign key references p(id))")]
    public void InlineForeignKeyNoisePhrase_OnOrdinaryColumn_Accepted(string createChild)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table p (id int not null primary key); {createChild}");
        _ = sim.AssertSqlError("insert c values (1, 99)", 547);
    }

    /// <summary>
    /// A referenced <c>UNIQUE</c> column may be NULL, and the NULL-keyed parent
    /// row is one no child can reference — so deleting it neither rejects nor
    /// cascades, whatever the referential action. Matches SQL Server 2025
    /// (2026-08-02).
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("on delete cascade")]
    public void DeleteParentRowWithNullUniqueKey_LeavesChildrenAlone(string action)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table p (id int null unique, tag nvarchar(5));
            create table c (cid int not null primary key, pid int null references p(id) {action});
            insert p values (null, 'n'), (1, 'a');
            insert c values (10, 1);
            delete p where tag = 'n'
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from p"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from c where pid = 1"));
    }
}
