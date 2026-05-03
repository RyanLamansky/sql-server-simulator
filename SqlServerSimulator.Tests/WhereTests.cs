using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class WhereTests
{
    [TestMethod]
    [DataRow("1 = 0", 0)]
    [DataRow("1 = 1", 1)]
    [DataRow("1 > 0", 1)]
    [DataRow("1 > 1", 0)]
    [DataRow("1 >= 0", 1)]
    [DataRow("1 >= 1", 1)]
    [DataRow("1 >= 2", 0)]
    [DataRow("1 > = 0", 1)]
    [DataRow("1 > = 1", 1)]
    [DataRow("1 > = 2", 0)]
    [DataRow("0 < 1", 1)]
    [DataRow("1 < 1", 0)]
    [DataRow("1 <= 0", 0)]
    [DataRow("1 <= 1", 1)]
    [DataRow("1 <= 2", 1)]
    [DataRow("1 < = 0", 0)]
    [DataRow("1 < = 1", 1)]
    [DataRow("1 < = 2", 1)]
    [DataRow("1 <> 0", 1)]
    [DataRow("1 <> 1", 0)]
    [DataRow("1 < > 0", 1)]
    [DataRow("1 < > 1", 0)]
    [DataRow("1 != 0", 1)]
    [DataRow("1 != 1", 0)]
    [DataRow("1 ! = 0", 1)]
    [DataRow("1 ! = 1", 0)]
    [DataRow("1 !> 0", 0)]
    [DataRow("1 !> 1", 1)]
    [DataRow("1 !> 2", 1)]
    [DataRow("1 ! > 0", 0)]
    [DataRow("1 ! > 1", 1)]
    [DataRow("1 ! > 2", 1)]
    [DataRow("1 !< 0", 1)]
    [DataRow("1 !< 1", 1)]
    [DataRow("1 !< 2", 0)]
    [DataRow("1 ! < 0", 1)]
    [DataRow("1 ! < 1", 1)]
    [DataRow("1 ! < 2", 0)]
    public void PureExpressionFilter(string whereExpression, int expectedCount)
    {
        AreEqual(expectedCount, new Simulation().ExecuteReader($"select 1 where {whereExpression}").EnumerateRecords().Count());
    }

    [TestMethod]
    public void TablelessWhere_NullOperand_ReturnsZeroRows()
        => AreEqual(0, new Simulation().ExecuteReader("select 1 where null = 1").EnumerateRecords().Count());

    [TestMethod]
    public void FromTableWhere_FiltersByEqualityToLiteral()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int, v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1, 100 ), ( 2, 200 ), ( 3, 300 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id, v from t where id = 2").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        AreEqual(200, reader[1]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByGreaterThan()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 ), ( 3 ), ( 4 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t where v > 2").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(3, reader[0]);
        IsTrue(reader.Read()); AreEqual(4, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_NullColumnNeverMatches()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int, v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t ( id ) values ( 1 )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 2, 99 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id from t where v = 99").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByParameter()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 ), ( 3 )").ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where id = @id";
        AddTypedParameter(select, "id", DbType.Int32, 2);

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByDateEquality()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int, d date )").ExecuteNonQuery();

        using var ins = connection.CreateCommand();
        ins.CommandText = "insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
        AddTypedParameter(ins, "a", DbType.Date, new DateOnly(2024, 1, 1));
        AddTypedParameter(ins, "b", DbType.Date, new DateOnly(2026, 5, 4));
        AddTypedParameter(ins, "c", DbType.Date, new DateOnly(2099, 12, 31));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = @target";
        AddTypedParameter(select, "target", DbType.Date, new DateOnly(2026, 5, 4));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByDateOrdering()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( id int, d date )").ExecuteNonQuery();

        using var ins = connection.CreateCommand();
        ins.CommandText = "insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
        AddTypedParameter(ins, "a", DbType.Date, new DateOnly(2024, 1, 1));
        AddTypedParameter(ins, "b", DbType.Date, new DateOnly(2026, 5, 4));
        AddTypedParameter(ins, "c", DbType.Date, new DateOnly(2099, 12, 31));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d > @target";
        AddTypedParameter(select, "target", DbType.Date, new DateOnly(2026, 1, 1));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(2, reader[0]);
        IsTrue(reader.Read()); AreEqual(3, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_DateColumnReadableViaGetDateTime()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( d date )").ExecuteNonQuery();

        using var ins = connection.CreateCommand("insert t values ( @p )", ("p", new DateOnly(2026, 5, 4)));
        _ = ins.ExecuteNonQuery();

        using var reader = connection.CreateCommand("select d from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(new DateTime(2026, 5, 4), reader.GetDateTime(0));
    }

    [TestMethod]
    public void FromTableWhere_FiltersByDateTime2Equality()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetime2(7) )").ExecuteNonQuery();

        using var ins = connection.CreateCommand();
        ins.CommandText = "insert t values ( 1, @a ), ( 2, @b )";
        AddTypedParameter(ins, "a", DbType.DateTime2, new DateTime(2026, 5, 4, 13, 45, 30));
        AddTypedParameter(ins, "b", DbType.DateTime2, new DateTime(2026, 5, 4, 13, 45, 31));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = @target";
        AddTypedParameter(select, "target", DbType.DateTime2, new DateTime(2026, 5, 4, 13, 45, 31));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_CrossPrecisionDateTime2Comparison()
    {
        // Stored at precision 3; compared against a precision-7 parameter.
        // Promotion widens both to precision 7 so a same-tick match succeeds.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetime2(3) )").ExecuteNonQuery();

        using var ins = connection.CreateCommand("insert t values ( 1, @a )", ("a", new DateTime(2026, 5, 4, 13, 45, 30, 100)));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = @target";
        AddTypedParameter(select, "target", DbType.DateTime2, new DateTime(2026, 5, 4, 13, 45, 30, 100));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByTimeOrdering()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, t time(7) )").ExecuteNonQuery();

        using var ins = connection.CreateCommand();
        ins.CommandText = "insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
        AddTypedParameter(ins, "a", DbType.Time, new TimeSpan(8, 0, 0));
        AddTypedParameter(ins, "b", DbType.Time, new TimeSpan(13, 45, 30));
        AddTypedParameter(ins, "c", DbType.Time, new TimeSpan(23, 0, 0));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where t > @target";
        AddTypedParameter(select, "target", DbType.Time, new TimeSpan(12, 0, 0));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(2, reader[0]);
        IsTrue(reader.Read()); AreEqual(3, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_CrossPrecisionTimeComparison()
    {
        // Stored at precision 3; compared against precision-7 parameter.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, t time(3) )").ExecuteNonQuery();

        using var ins = connection.CreateCommand("insert t values ( 1, @a )", ("a", new TimeSpan(0, 13, 45, 30, 100)));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where t = @target";
        AddTypedParameter(select, "target", DbType.Time, new TimeSpan(0, 13, 45, 30, 100));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByDateTimeOffsetEquality_AcrossOffsets()
    {
        // The stored row and the parameter share a UTC instant but carry
        // different offsets; SQL Server compares by UTC, so they should match.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetimeoffset(7) )").ExecuteNonQuery();

        using var ins = connection.CreateCommand();
        ins.CommandText = "insert t values ( 1, @a ), ( 2, @b )";
        AddTypedParameter(ins, "a", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)));
        AddTypedParameter(ins, "b", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 14, 45, 30, TimeSpan.FromHours(-7)));
        _ = ins.ExecuteNonQuery();

        // Equivalent UTC instant to row 1 but expressed in +03:00.
        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = @target";
        AddTypedParameter(select, "target", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 23, 45, 30, TimeSpan.FromHours(3)));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByDateTimeOffsetOrdering()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetimeoffset(7) )").ExecuteNonQuery();

        using var ins = connection.CreateCommand();
        ins.CommandText = "insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
        AddTypedParameter(ins, "a", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero));
        AddTypedParameter(ins, "b", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 13, 0, 0, TimeSpan.Zero));
        AddTypedParameter(ins, "c", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 23, 0, 0, TimeSpan.Zero));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d > @target";
        AddTypedParameter(select, "target", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read()); AreEqual(2, reader[0]);
        IsTrue(reader.Read()); AreEqual(3, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_CrossPrecisionDateTimeOffsetComparison()
    {
        // Stored at precision 0; compared against a precision-7 parameter.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( id int, d datetimeoffset(0) )").ExecuteNonQuery();

        using var ins = connection.CreateCommand("insert t values ( 1, @a )", ("a", new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7))));
        _ = ins.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = @target";
        AddTypedParameter(select, "target", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    private static void AddTypedParameter(System.Data.Common.DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
