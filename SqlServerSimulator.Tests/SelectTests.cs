using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public class SelectTests
{
    private static DbDataReader ExecuteReaderAndRead(string commandText)
    {
        var reader = new Simulation().ExecuteReader(commandText);
        IsTrue(reader.Read());
        return reader;
    }

    [TestMethod]
    public void Select1ViaExecuteReaderIndexer() => AreEqual(1, ExecuteReaderAndRead("select 1")[0]);

    [TestMethod]
    public void Select1ViaExecuteReaderGetInt32() => AreEqual(1, ExecuteReaderAndRead("select 1").GetInt32(0));

    [TestMethod]
    public void Null() => IsInstanceOfType<DBNull>(ExecuteScalar("select null"));

    [TestMethod]
    [DataRow("SELECT @@VERSION")]
    [DataRow("select @@version")]
    [DataRow("Select @@Version")]
    public void SelectVersion(string commandText) => AreEqual("SQL Server Simulator", new Simulation().ExecuteScalar(commandText));

    [TestMethod]
    [DataRow("select @p0", "p0", 5)]
    [DataRow("select @p0", "@p0", 6)]
    [DataRow("select (@p0)", "p0", 7)]
    public void SelectParameterValue(string commandText, string name, object value)
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand(commandText, (name, value))
            .ExecuteScalar();

        AreEqual(value, result);
    }

    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("1", 1)]
    [DataRow("42", 42)]
    [DataRow("2147483647", int.MaxValue)]
    [DataRow("(1)", 1)]
    [DataRow("(1) + 1", 2)]
    [DataRow("(1) + (1)", 2)]
    [DataRow("(1 + 2) * 3", 9)]
    [DataRow("1 + 1", 2)]
    [DataRow("1 - 1", 0)]
    [DataRow("-1", -1)]
    [DataRow("- 1", -1)]
    [DataRow("+1", 1)]
    [DataRow("+ 1", 1)]
    [DataRow("2 * 2", 4)]
    [DataRow("2 / 2", 1)]
    [DataRow("2 / 1", 2)]
    [DataRow("5 % 2", 1)]
    [DataRow("5 % 3", 2)]
    [DataRow("5 % 5", 0)]
    [DataRow("1 & 3", 1)]
    [DataRow("1 | 3", 3)]
    [DataRow("1 ^ 3", 2)]
    [DataRow("1 + 2 * 3", 7)]
    [DataRow("3 * 2 + 1", 7)]
    public void Expression(string commandText, object value)
    {
        using var reader = new Simulation().ExecuteReader($"select {commandText}");
        IsTrue(reader.Read());
        AreEqual(value, reader[0]);
    }

    [TestMethod]
    public void BareAs() => AssertSqlMessage("select as z", "Incorrect syntax near the keyword 'as'.");

    [TestMethod]
    public void UnsupportedCharacter_RaisesMsg102()
        => AssertSqlError("select 1 ~ 2", 102, "Incorrect syntax near '~'.");

    [TestMethod]
    [DataRow("select 1", "", 1)]
    [DataRow("select 1 c", "c", 1)]
    [DataRow("select 1 as c", "c", 1)]
    [DataRow("select 1 as [c]", "c", 1)]
    [DataRow("select 1 as [c]]d]", "c]d", 1)]
    [DataRow("select 1 as [e f]", "e f", 1)]
    [DataRow("select 1 + 1 as c", "c", 2)]
    public void NamedExpression(string commandText, string name, object value)
    {
        using var reader = new Simulation().ExecuteReader(commandText);
        IsTrue(reader.Read());
        AreEqual(name, reader.GetName(0));
        AreEqual(value, reader[0]);
    }

    [TestMethod]
    [DataRow("select 1 from systypes", 34, 1, 1)]
    [DataRow("select 1 from systypes as s", 34, 1, 1)]
    [DataRow("select name from systypes", 34, 34, "image")]
    [DataRow("select 1 + 1 from systypes", 34, 1, 2)]
    public void ExpressionFromTable(string commandText, int minimumRows, int uniqueRows, object value)
    {
        using var reader = new Simulation().ExecuteReader(commandText);
        var results = reader.EnumerateRecords().Take(minimumRows).Select(r => r[0]).ToArray();

        Assert.HasCount(minimumRows, results);
        Assert.HasCount(uniqueRows, results.ToHashSet());
        AreEqual(value, results[0]);
    }

    [TestMethod]
    [DataRow("select name c from systypes", 34, 34, "c", "image")]
    [DataRow("select name as c from systypes", 34, 34, "c", "image")]
    [DataRow("select name as c from systypes as s", 34, 34, "c", "image")]
    [DataRow("select systypes.name from systypes", 34, 34, "name", "image")]
    [DataRow("select s.name from systypes as s", 34, 34, "name", "image")]
    [DataRow("select 1 + 1 as c from systypes", 34, 1, "c", 2)]
    public void NamedExpressionFromTable(string commandText, int minimumRows, int uniqueRows, string name, object value)
    {
        using var reader = new Simulation().ExecuteReader(commandText);
        var results = reader.EnumerateRecords().Take(minimumRows).Select(r =>
        {
            AreEqual(name, r.GetName(0));
            return r[0];
        }).ToArray();

        Assert.HasCount(minimumRows, results);
        Assert.HasCount(uniqueRows, results.ToHashSet());
        AreEqual(value, results[0]);
    }

    [TestMethod]
    [DataRow("select 1 + 1 as x, name as c from systypes", 34, "x", 2, "c", "image")]
    [DataRow("select 1 + 1, name as c from systypes", 34, "", 2, "c", "image")]
    public void NamedExpressionAndColumnFromTable(string commandText, int minimumRows, string name0, object value0, string name1, object value1)
    {
        using var reader = new Simulation().ExecuteReader(commandText);
        var results = reader.EnumerateRecords().Take(minimumRows).Select(r =>
        {
            AreEqual(name0, r.GetName(0));
            AreEqual(name1, r.GetName(1));
            return (C0: r[0], C1: r[1]);
        }).ToArray();

        Assert.HasCount(minimumRows, results);
        AreEqual(value0, results[0].C0);
        AreEqual(value1, results[0].C1);
    }

    [TestMethod]
    public void Select1Comma2()
    {
        using var reader = new Simulation().ExecuteReader("select 1, 2");
        var results = reader.EnumerateRecords().Select(r => (C1: r.GetInt32(0), C2: r.GetInt32(1))).ToArray();

        Assert.HasCount(1, results);
        AreEqual(1, results[0].C1);
        AreEqual(2, results[0].C2);
    }

    [TestMethod]
    public void SelectTwoColumns()
    {
        using var reader = new Simulation().ExecuteReader("select name, length from systypes");
        var results = reader.EnumerateRecords().Take(34).Select(r => (C1: r.GetString(0), C2: r.GetInt16(1))).ToArray();

        Assert.HasCount(34, results);
        AreEqual("image", results[0].C1);
        AreEqual((short)16, results[0].C2);
    }

    [TestMethod]
    [DataRow("select", "select")]
    [DataRow("select ", "select")]
    [DataRow("select ,", ",")]
    public void SelectSyntaxErrorsAreCorrect(string commandText, string nearSyntax) =>
        new Simulation().ValidateSyntaxError(commandText, nearSyntax);

    [TestMethod]
    public void IdentifierVeryLong()
    {
        var identifier = new string('z', 128);
        using var reader = new Simulation().ExecuteReader($"select 1 as {identifier}");
        IsTrue(reader.Read());
        AreEqual(identifier, reader.GetName(0));
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public void IdentifierTooLong()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteScalar($"select 1 as {new string('z', 129)}"));
        Contains("zzz", ex.Message);
    }

    [TestMethod]
    [DataRow("select x from ( select 1 as x ) as x", "x", 1)]
    [DataRow("select x from ( select 1 + 1 as x ) as x", "x", 2)]
    public void DerivedTable(string commandText, string name, object value)
    {
        using var reader = new Simulation().ExecuteReader(commandText);
        var result = reader.EnumerateRecords().Select(r =>
        {
            AreEqual(name, r.GetName(0));
            return r[0];
        }).SingleOrDefault();
        AreEqual(value, result);
    }

    [TestMethod]
    public void MultiColumnNamedAndUnnamed()
    {
        using var reader = new Simulation().ExecuteReader("select 1 + 1 as x, 7 as y");
        IsTrue(reader.Read());
        AreEqual(2, reader.FieldCount);
        AreEqual("x", reader.GetName(0));
        AreEqual("y", reader.GetName(1));
        AreEqual(2, reader[0]);
        AreEqual(7, reader[1]);
    }

    [TestMethod]
    public void NullPropagatesThroughArithmetic() => AreEqual(DBNull.Value, ExecuteScalar("select null + 1"));

    [TestMethod]
    public void SelectFromEmptyTable_ReturnsNoRows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SelectFromTable_ProjectionReorderingAndSubsetting()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( a int, b int, c int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1, 2, 3 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select c, a from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader.FieldCount);
        AreEqual("c", reader.GetName(0));
        AreEqual("a", reader.GetName(1));
        AreEqual(3, reader[0]);
        AreEqual(1, reader[1]);
    }

    [TestMethod]
    public void MultipleSelects_SeparatedBySemicolon_ProduceMultipleResultSets()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select 1; select 2; select 3").ExecuteReader();

        IsTrue(reader.Read()); AreEqual(1, reader[0]);
        IsFalse(reader.Read());
        IsTrue(reader.NextResult());
        IsTrue(reader.Read()); AreEqual(2, reader[0]);
        IsFalse(reader.Read());
        IsTrue(reader.NextResult());
        IsTrue(reader.Read()); AreEqual(3, reader[0]);
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void TrailingSemicolonOnTablelessSelect_IsAccepted()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(1, connection.CreateCommand("select 1;").ExecuteScalar());
    }

    [TestMethod]
    public void RepeatedSemicolons_BetweenStatements_AreNoOps()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select 1;; ;select 2").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
        IsTrue(reader.NextResult());
        IsTrue(reader.Read()); AreEqual(2, reader[0]);
    }

    [TestMethod]
    public void UnaryMinusBeforeFrom_DoesNotSwallowFromClause()
    {
        // FROM keyword must survive a unary-minus projection: parser cannot greedily consume past the projection's end.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 ), ( 3 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select -v from t").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add((int)reader[0]);
        CollectionAssert.AreEquivalent(new[] { -1, -2, -3 }, values);
    }

    [TestMethod]
    public void UnaryMinusInProjection_PreservesAlias()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select -1 n").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(-1, reader[0]);
        AreEqual("n", reader.GetName(0));
    }

    [TestMethod]
    public void SelectStar_SingleSource_ProjectsAllColumnsInDeclaredOrder()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int, c int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, 2, 3)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select * from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(3, reader.FieldCount);
        AreEqual("a", reader.GetName(0));
        AreEqual("b", reader.GetName(1));
        AreEqual("c", reader.GetName(2));
        AreEqual(1, reader[0]);
        AreEqual(2, reader[1]);
        AreEqual(3, reader[2]);
    }

    [TestMethod]
    public void SelectStar_WithWhereClause_FiltersRows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, 10), (2, 20), (3, 30)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select * from t where a >= 2").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(1));
        CollectionAssert.AreEqual(new[] { 20, 30 }, values);
    }

    [TestMethod]
    public void SelectStar_TwoSourceJoin_IncludesDuplicateColumnNames()
    {
        // Multi-source `*` keeps duplicate column names; per-source qualifying lets the resolver bind without Msg 209.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("create table u (a int, c int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, 2)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert u values (1, 9)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select * from t inner join u on t.a = u.a").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(4, reader.FieldCount);
        AreEqual("a", reader.GetName(0));
        AreEqual("b", reader.GetName(1));
        AreEqual("a", reader.GetName(2));
        AreEqual("c", reader.GetName(3));
    }

    [TestMethod]
    public void SelectQualifiedStar_RestrictsToNamedSource()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("create table u (a int, c int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, 2)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert u values (1, 9)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select t.* from t inner join u on t.a = u.a").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader.FieldCount);
        AreEqual("a", reader.GetName(0));
        AreEqual("b", reader.GetName(1));
    }

    [TestMethod]
    public void SelectStar_InsideDerivedTable_OuterReferencesProjectedColumns()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, 2), (3, 4)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select x.a from (select * from t) as x where x.a = 3").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(3, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SelectQualifiedStar_UnboundQualifier_RaisesMsg4104()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        var ex = Throws<DbException>(() => connection.CreateCommand("select notbound.* from t").ExecuteReader().Read());
        AreEqual("The multi-part identifier \"notbound.*\" could not be bound.", ex.Message);
    }
}
