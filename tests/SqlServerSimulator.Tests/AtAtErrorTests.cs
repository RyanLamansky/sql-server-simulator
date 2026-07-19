using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>@@ERROR</c>. The simulator always returns 0 because TRY/CATCH
/// isn't modeled — any <c>SimulatedSqlException</c> propagates out of the
/// dispatch loop and terminates the batch, so the only statements that ever
/// complete are successful ones and the most-recently-completed statement's
/// error number is always 0. Apps that read <c>@@ERROR</c> in straight-line
/// scripts (no TRY/CATCH wrapping) get correct behavior; apps that wrap a
/// statement-terminating-only error in TRY/CATCH and expect to observe the
/// number won't work until TRY/CATCH lands.
/// </summary>
[TestClass]
public sealed class AtAtErrorTests
{
    [TestMethod]
    public void AtAtError_BareRead_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@error"));

    [TestMethod]
    public void AtAtError_AfterSuccessfulInsert_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1);
            select @@error
            """));

    [TestMethod]
    public void AtAtError_CaseInsensitive()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@ERROR"));

    [TestMethod]
    public void AtAtError_UsableInPredicate()
        => AreEqual("ok", new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1);
            if @@error = 0 select 'ok'
            """));

    [TestMethod]
    public void AtAtError_ReturnsInt()
    {
        using var reader = new Simulation().ExecuteReader("select @@error as e");
        IsTrue(reader.Read());
        AreEqual(typeof(int), reader.GetFieldType(0));
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void AtAtError_InArithmetic_IsZero()
        => AreEqual(7, new Simulation().ExecuteScalar("select @@error + 7"));
}
