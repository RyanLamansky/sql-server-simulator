using System.Collections;
using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The concrete ADO.NET pipeline's own surface — the members a consumer reaches
/// only by holding the simulator's types rather than their <c>System.Data</c>
/// bases. The <c>new</c>-shadowed strongly-typed returns
/// (<see cref="SimulatedDbCommand.Transaction"/>,
/// <see cref="SimulatedDbCommand.ExecuteReader(CommandBehavior)"/>,
/// <see cref="SimulatedDbConnection.BeginTransaction()"/>) are invisible
/// through a <see cref="DbCommand"/> / <see cref="DbConnection"/>-typed
/// variable, which is what every other test holds; the explicit interface
/// members on the collections are likewise reachable only through the
/// interface. Mirrors <c>Microsoft.Data.SqlClient</c>'s shape, so a consumer
/// downcasting to reach a concrete property behaves the same here.
/// </summary>
[TestClass]
public sealed class ConcreteAdoSurfaceTests
{
    private static SimulatedDbConnection OpenConcrete(Simulation? simulation = null)
    {
        var connection = (simulation ?? new Simulation()).CreateDbConnection();
        connection.Open();
        return connection;
    }

    [TestMethod]
    public void Connection_ServerVersion_ReportsAVersionString()
    {
        using var connection = OpenConcrete();
        var version = connection.ServerVersion;
        IsFalse(string.IsNullOrEmpty(version));
        IsTrue(Version.TryParse(version, out _), $"not a version: {version}");
    }

    [TestMethod]
    public void Connection_BeginTransaction_ReturnsTheConcreteTransaction()
    {
        using var connection = OpenConcrete();
        using var transaction = connection.BeginTransaction();
        AreSame(connection, transaction.Connection);
        // SqlClient reports the level as the caller passed it, and the no-argument
        // overload passes Unspecified rather than naming the session's default.
        AreEqual(IsolationLevel.Unspecified, transaction.IsolationLevel);
        transaction.Rollback();
    }

    [TestMethod]
    public void Transaction_IsolationLevel_ReportsWhatBeginAsked()
    {
        using var connection = OpenConcrete();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        AreEqual(IsolationLevel.Serializable, transaction.IsolationLevel);
        transaction.Rollback();
    }

    [TestMethod]
    public void Command_TransactionProperty_RoundTripsTheConcreteTransaction()
    {
        using var connection = OpenConcrete();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        IsNull(command.Transaction);
        command.Transaction = transaction;
        AreSame(transaction, command.Transaction);
        command.CommandText = "select 1";
        AreEqual(1, command.ExecuteScalar());
        transaction.Rollback();
    }

    [TestMethod]
    public void Command_ExecuteReaderWithBehavior_ReturnsTheConcreteReader()
    {
        using var connection = OpenConcrete();
        using var command = connection.CreateCommand();
        command.CommandText = "select 42 as answer";
        using var reader = command.ExecuteReader(CommandBehavior.SingleRow);
        IsTrue(reader.Read());
        AreEqual(42, reader.GetInt32(0));
    }

    [TestMethod]
    public void ParameterCollection_IndexerSetter_IndexOfObject_AndTypedEnumeration()
    {
        using var connection = OpenConcrete();
        using var command = connection.CreateCommand();
        command.CommandText = "select @a + @b";
        var first = command.CreateParameter();
        first.ParameterName = "@a";
        first.Value = 1;
        var second = command.CreateParameter();
        second.ParameterName = "@b";
        second.Value = 2;
        _ = command.Parameters.Add(first);
        _ = command.Parameters.Add(second);

        // IndexOf(object) — the non-generic overload behind Contains / Remove.
        AreEqual(1, command.Parameters.IndexOf(second));
        AreEqual(-1, command.Parameters.IndexOf(command.CreateParameter()));

        // The indexer setter, which routes through SetParameter(int, DbParameter).
        var replacement = command.CreateParameter();
        replacement.ParameterName = "@a";
        replacement.Value = 10;
        ((DbParameterCollection)command.Parameters)[0] = replacement;
        AreEqual(12, command.ExecuteScalar());

        // The strongly-typed enumerator the collection exposes as
        // IEnumerable<SimulatedDbParameter>.
        var names = new List<string>();
        foreach (var parameter in (IEnumerable<SimulatedDbParameter>)command.Parameters)
            names.Add(parameter.ParameterName);
        CollectionAssert.AreEqual(new[] { "@a", "@b" }, names);
    }

    [TestMethod]
    public void ErrorCollection_CollectionSurface()
    {
        var raised = ThrowsExactly<SimulatedSqlException>(
            () => new Simulation().ExecuteNonQuery("select cast('x' as int)"));
        var errors = raised.Errors;
        HasCount(1, errors);

        var asCollection = (ICollection)errors;
        IsFalse(asCollection.IsSynchronized);
        IsNotNull(asCollection.SyncRoot);

        var typed = new SimulatedError[1];
        errors.CopyTo(typed, 0);
        AreEqual(raised.Number, typed[0].Number);

        var untyped = new SimulatedError[1];
        asCollection.CopyTo(untyped, 0);
        AreEqual(raised.Number, untyped[0].Number);

        var walked = 0;
        foreach (var _ in (IEnumerable)errors)
            walked++;
        AreEqual(1, walked);
    }

    /// <summary>
    /// A reader positioned before any <c>Read</c> — and one positioned on a
    /// statement that failed — reports no fields and no rows, and indexing a
    /// row that isn't there is an <see cref="InvalidOperationException"/>
    /// rather than an out-of-range one.
    /// </summary>
    [TestMethod]
    public void Reader_BeforeFirstReadAndOnAFailedStatement_ReportNoRowAndNoFields()
    {
        using var connection = OpenConcrete();
        using var beforeAny = connection.CreateCommand("select 1 where 1 = 0").ExecuteReader();
        IsFalse(beforeAny.Read());
        _ = ThrowsExactly<InvalidOperationException>(() => beforeAny.GetValue(0));

        // The second statement fails, so NextResult lands the reader on the
        // error position: no fields, no rows, and Read throws the carried error.
        using var failing = connection.CreateCommand("select 1; select cast('x' as int);").ExecuteReader();
        IsTrue(failing.Read());
        IsFalse(failing.Read());
        IsTrue(failing.NextResult());
        AreEqual(0, failing.FieldCount);
        IsFalse(failing.HasRows);
        _ = ThrowsExactly<InvalidOperationException>(() => failing.GetValue(0));
        AreEqual(245, ThrowsExactly<SimulatedSqlException>(() => failing.Read()).Number);
    }

    /// <summary>
    /// Under a finite <c>SET TEXTSIZE</c> the client cursor is wrapped, so the
    /// wrapper is what answers <c>FieldCount</c> / <c>HasRows</c> as well as
    /// the clipped values.
    /// </summary>
    [TestMethod]
    public void Reader_UnderFiniteTextSize_TheWrappingCursorAnswersShapeQuestions()
    {
        using var connection = OpenConcrete();
        _ = connection.CreateCommand("create table t (v varchar(max)); insert t values ('abcdefghij')").ExecuteNonQuery();
        using var reader = connection.CreateCommand("set textsize 4; select v from t").ExecuteReader();
        AreEqual(1, reader.FieldCount);
        IsTrue(reader.HasRows);
        IsTrue(reader.Read());
        AreEqual("abcd", reader.GetString(0));
    }
}
