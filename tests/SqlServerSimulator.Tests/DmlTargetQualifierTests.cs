using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The only qualifier a <c>SET</c> assignment target admits is the write target
/// as written — the leading name, its alias when it has one. Every other one is
/// Msg 4104 naming the whole dotted form, ahead of the leaf lookup and whether
/// or not the leaf names a real column. A <c>VALUES</c> cell has no column scope
/// at all, so every qualified reference there is Msg 4104 too. Probed against
/// SQL Server 2025 (2026-08-05).
/// </summary>
[TestClass]
public sealed class DmlTargetQualifierTests
{
    private const string Setup =
        "create table t1 (id int, v int); create table t2 (id int, w int); " +
        "insert t1 values (1, 10); insert t2 values (1, 20); ";

    private static void Unbound(string statement, string dottedName)
        => new Simulation().AssertSqlError(
            Setup + statement,
            4104,
            $"The multi-part identifier \"{dottedName}\" could not be bound.");

    private static object? Run(string statement)
        => new Simulation().ExecuteScalar(Setup + statement + "; select v from t1");

    [TestMethod]
    public void UnknownQualifierOnSetTarget_Msg4104()
        => Unbound("update t1 set zz.id = 5", "zz.id");

    /// <summary>The leaf naming nothing doesn't change the diagnostic.</summary>
    [TestMethod]
    public void UnknownQualifierOnUnknownColumn_StillMsg4104()
        => Unbound("update t1 set zz.nosuch = 5", "zz.nosuch");

    /// <summary>A matching qualifier gets the ordinary Msg 207 on its leaf.</summary>
    [TestMethod]
    public void TargetQualifierOnUnknownColumn_Msg207()
        => new Simulation().AssertSqlError(Setup + "update t1 set t1.nosuch = 5", 207, "Invalid column name 'nosuch'.");

    [TestMethod]
    public void TargetNameQualifier_Binds()
        => AreEqual(5, Run("update t1 set t1.v = 5"));

    [TestMethod]
    public void SchemaQualifiedTarget_Binds()
        => AreEqual(5, Run("update t1 set dbo.t1.v = 5"));

    [TestMethod]
    public void DatabaseQualifiedTarget_Binds()
        => AreEqual(5, Run("update t1 set simulated.dbo.t1.v = 5"));

    /// <summary>A fifth segment is the grammar limit, reported with the whole name.</summary>
    [TestMethod]
    public void FiveSegmentTarget_Msg4104()
        => Unbound("update t1 set a.b.c.d.id = 5", "a.b.c.d.id");

    /// <summary>Once aliased, the table's own name no longer binds.</summary>
    [TestMethod]
    public void TableNameAgainstAnAliasedTarget_Msg4104()
        => Unbound("update a set t1.v = 5 from t1 a", "t1.v");

    [TestMethod]
    public void AliasQualifier_Binds()
        => AreEqual(5, Run("update a set a.v = 5 from t1 a"));

    /// <summary>A joined UPDATE writes its target only — a source's column is unbindable.</summary>
    [TestMethod]
    public void JoinSourceColumnAsSetTarget_Msg4104()
        => Unbound("update a set b.w = 1 from t1 a join t2 b on a.id = b.id", "b.w");

    [TestMethod]
    public void UnaliasedJoinSourceColumnAsSetTarget_Msg4104()
        => Unbound("update t1 set t2.w = 1 from t1 join t2 on t1.id = t2.id", "t2.w");

    [TestMethod]
    public void MergeTargetAliasHidesTheTableName_Msg4104()
        => Unbound("merge t1 as a using t2 on a.id = t2.id when matched then update set t1.v = 1;", "t1.v");

    [TestMethod]
    public void UnknownQualifierInMergeUpdate_Msg4104()
        => Unbound("merge t1 using t2 on t1.id = t2.id when matched then update set zz.v = 1;", "zz.v");

    // --- VALUES cells have no column scope ---

    [TestMethod]
    public void UnknownQualifierInValues_Msg4104()
        => Unbound("insert into t1 (id, v) values (zz.id, 1)", "zz.id");

    /// <summary>Not even the insert target's own name is in scope there.</summary>
    [TestMethod]
    public void TargetQualifierInValues_Msg4104()
        => Unbound("insert into t1 (id, v) values (t1.id, 1)", "t1.id");

    [TestMethod]
    public void UnqualifiedUnknownNameInValues_Msg207()
        => new Simulation().AssertSqlError(Setup + "insert into t1 (id, v) values (nosuch, 1)", 207, "Invalid column name 'nosuch'.");
}
