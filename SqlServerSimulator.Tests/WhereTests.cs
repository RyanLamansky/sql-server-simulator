using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

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
    public void PureExpressionFilter(string whereExpression, int expectedCount) =>
        AreEqual(expectedCount, new Simulation().ExecuteReader($"select 1 where {whereExpression}").EnumerateRecords().Count());

    [TestMethod]
    public void TablelessWhere_NullOperand_ReturnsZeroRows()
        => AreEqual(0, new Simulation().ExecuteReader("select 1 where null = 1").EnumerateRecords().Count());

    [TestMethod]
    public void FromTableWhere_FiltersByEqualityToLiteral()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t ( id int, v int );
            insert t values ( 1, 100 ), ( 2, 200 ), ( 3, 300 )
            """).ExecuteNonQuery();

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
        _ = connection.CreateCommand("""
            create table t ( v int );
            insert t values ( 1 ), ( 2 ), ( 3 ), ( 4 )
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v from t where v > 2").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(3, reader[0]);
        IsTrue(reader.Read()); AreEqual(4, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_NullColumnNeverMatches()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t ( id int, v int );
            insert t ( id ) values ( 1 );
            insert t values ( 2, 99 )
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select id from t where v = 99").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void FromTableWhere_FiltersByParameter()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t ( id int );
            insert t values ( 1 ), ( 2 ), ( 3 )
            """).ExecuteNonQuery();

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
        using var ins = connection.CreateCommand();
        ins.CommandText = "create table t ( id int, d date );insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
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
        using var ins = connection.CreateCommand();
        ins.CommandText = "create table t ( id int, d date );insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
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
        _ = connection.CreateCommand(
            "create table t ( d date );insert t values ( @p )",
            ("p", new DateOnly(2026, 5, 4))).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select d from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(new DateTime(2026, 5, 4), reader.GetDateTime(0));
    }

    [TestMethod]
    public void FromTableWhere_FiltersByDateTime2Equality()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var ins = connection.CreateCommand();
        ins.CommandText = "create table t ( id int, d datetime2(7) );insert t values ( 1, @a ), ( 2, @b )";
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

    // Stored at precision 3, compared against precision-7 parameter — promotion widens both to p=7.
    [TestMethod]
    public void FromTableWhere_CrossPrecisionDateTime2Comparison()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t ( id int, d datetime2(3) );insert t values ( 1, @a )",
            ("a", new DateTime(2026, 5, 4, 13, 45, 30, 100))).ExecuteNonQuery();

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
        using var ins = connection.CreateCommand();
        ins.CommandText = "create table t ( id int, t time(7) );insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
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
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t ( id int, t time(3) );insert t values ( 1, @a )",
            ("a", new TimeSpan(0, 13, 45, 30, 100))).ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where t = @target";
        AddTypedParameter(select, "target", DbType.Time, new TimeSpan(0, 13, 45, 30, 100));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    // Row and parameter share a UTC instant but carry different offsets; SQL Server compares by UTC.
    [TestMethod]
    public void FromTableWhere_FiltersByDateTimeOffsetEquality_AcrossOffsets()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var ins = connection.CreateCommand();
        ins.CommandText = "create table t ( id int, d datetimeoffset(7) );insert t values ( 1, @a ), ( 2, @b )";
        AddTypedParameter(ins, "a", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)));
        AddTypedParameter(ins, "b", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 14, 45, 30, TimeSpan.FromHours(-7)));
        _ = ins.ExecuteNonQuery();

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
        using var ins = connection.CreateCommand();
        ins.CommandText = "create table t ( id int, d datetimeoffset(7) );insert t values ( 1, @a ), ( 2, @b ), ( 3, @c )";
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
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t ( id int, d datetimeoffset(0) );insert t values ( 1, @a )",
            ("a", new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)))).ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "select id from t where d = @target";
        AddTypedParameter(select, "target", DbType.DateTimeOffset, new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)));

        using var reader = select.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader[0]);
        IsFalse(reader.Read());
    }

    // Regression: pre-fix, `where a=X and b=Y` parsed only `a=X` and dropped `and b=Y`.
    [TestMethod]
    public void Where_AndChain_TwoPredicates_BothMustHold()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int not null, b int not null, c int);
            insert t values (1, 2, 100), (1, 3, 200), (2, 2, 300)
            """).ExecuteNonQuery();

        AreEqual(200, connection.CreateCommand("select c from t where a = 1 and b = 3").ExecuteScalar());
        AreEqual(100, connection.CreateCommand("select c from t where a = 1 and b = 2").ExecuteScalar());
        AreEqual(300, connection.CreateCommand("select c from t where a = 2 and b = 2").ExecuteScalar());
    }

    [TestMethod]
    public void Where_AndChain_ThreePredicates_AllMustHold()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int, b int, c int);
            insert t values (1, 2, 100), (1, 2, 200)
            """).ExecuteNonQuery();

        AreEqual(100, connection.CreateCommand("select c from t where a = 1 and b = 2 and c = 100").ExecuteScalar());
        IsNull(connection.CreateCommand("select c from t where a = 1 and b = 2 and c = 999").ExecuteScalar());
    }

    [TestMethod]
    public void Where_AndChain_NullOperand_ExcludesRow()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int, b int);
            insert t values (1, null), (1, 2)
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select b from t where a = 1 and b = 2").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Where_OrChain_TwoPredicates_EitherMustHold()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int, b int);
            insert t values (1, 2), (3, 4), (5, 6)
            """).ExecuteNonQuery();

        using var reader = connection.CreateCommand("select a from t where a = 1 or b = 4").ExecuteReader();
        var matched = new List<int>();
        while (reader.Read())
            matched.Add((int)reader[0]);
        CollectionAssert.AreEquivalent(new[] { 1, 3 }, matched);
    }

    [TestMethod]
    [DataRow("a = 1 or b = 2 and c = 2", 3)]    // AND binds tighter: (1,2,3),(1,3,2),(2,2,2)
    [DataRow("(a = 1 or b = 2) and c = 2", 2)]  // parens flip precedence: (1,3,2),(2,2,2)
    public void Where_AndOrPrecedence(string predicate, int expected)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int, b int, c int);
            insert t values (1, 2, 3), (1, 3, 2), (2, 2, 2), (0, 0, 5)
            """).ExecuteNonQuery();

        AreEqual(expected, CountWhere(connection, predicate));
    }

    [TestMethod]
    [DataRow("not (a = 1)", 1)]              // NULL is excluded by tri-state
    [DataRow("a <> 1", 1)]                   // mirrors NOT (a=1) tri-state
    [DataRow("not not (a = 1)", 1)]
    [DataRow("((a = 1))", 1)]
    [DataRow("a is null", 1)]
    [DataRow("a is not null", 2)]
    [DataRow("a = 1 or a is null", 2)]
    public void Where_TriStateAndIsNull_OnNullableIntColumn(string predicate, int expected)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (null), (1), (2)
            """).ExecuteNonQuery();

        AreEqual(expected, CountWhere(connection, predicate));
    }

    [TestMethod]
    [DataRow("a in (1, 3)", 2)]
    [DataRow("a not in (1, 3)", 2)]
    public void Where_InNotIn_BasicMembership(string predicate, int expected)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (1), (2), (3), (4)
            """).ExecuteNonQuery();

        AreEqual(expected, CountWhere(connection, predicate));
    }

    // `NULL IN (1, 2)` is UNKNOWN; UNKNOWN excludes from WHERE.
    [TestMethod]
    public void Where_InList_NullLeftSideIsExcluded()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (null), (1), (2)
            """).ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a in (1, 2)"));
    }

    // `1 NOT IN (1, NULL)` = false (1 matches 1). `2 NOT IN (1, NULL)` = UNKNOWN → excluded.
    [TestMethod]
    public void Where_NotInList_WithNullElementExcludesEverythingViaUnknown()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (1), (2), (3)
            """).ExecuteNonQuery();

        AreEqual(0, CountWhere(connection, "a not in (1, null)"));
    }

    [TestMethod]
    public void Where_InList_WithStringValues()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int, status nvarchar(20));
            insert t values (1, 'active'), (2, 'pending'), (3, 'archived')
            """).ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "status in ('active', 'archived')"));
    }

    // Member-list values are int literals; column is bigint. SQL Server promotes to a common numeric type.
    [TestMethod]
    public void Where_InList_PromotesAcrossNumericTypes()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a bigint);
            insert t values (1), (2), (3)
            """).ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a in (1, 3)"));
    }

    [TestMethod]
    public void Where_FivePartColumnReference_RaisesMsg4104()
        => AssertSqlError("select 1 where a.b.c.d.e = 1", 4104, "The multi-part identifier \"a.b.c.d.e\" could not be bound.");

    /// <summary>
    /// Paren-wrapped value LHS: <c>(value_expr) cmp rhs</c> is accepted in
    /// any boolean position. The boolean parser disambiguates via a token-
    /// lookahead at the matching <c>)</c> — when the next token is an
    /// arithmetic / comparison operator (or IS / IN / LIKE / BETWEEN / NOT),
    /// the leading <c>(</c> introduces a value expression rather than a
    /// boolean group.
    /// </summary>
    [TestMethod]
    public void Where_ParenWrappedArithLhs_FiltersAsExpected()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (3), (5), (7)
            """).ExecuteNonQuery();
        AreEqual(2, CountWhere(connection, "(a + 1) > 5"));
    }

    [TestMethod]
    public void Where_DoublyParenWrappedLhs_FiltersAsExpected()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (1), (2), (3)
            """).ExecuteNonQuery();
        AreEqual(1, CountWhere(connection, "((a + 1)) = 3"));
    }

    [TestMethod]
    public void Where_ParenLhs_BetweenOperator_FiltersAsExpected()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (1), (5), (10), (20)
            """).ExecuteNonQuery();
        AreEqual(2, CountWhere(connection, "(a * 2) between 10 and 25"));
    }

    [TestMethod]
    public void Where_ParenLhs_InList_FiltersAsExpected()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (a int);
            insert t values (1), (2), (3), (4)
            """).ExecuteNonQuery();
        AreEqual(2, CountWhere(connection, "(a + 1) in (3, 5)"));
    }

    [TestMethod]
    public void Where_ParenLhs_AndOuterBooleanGroup_BothShapesCoexist()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (a int, b int);
            insert t values (1, 10), (2, 20), (3, 30);
            select count(*) from t where (a + 1) = 3 AND (b > 15)
            """));

    [TestMethod]
    public void Having_ParenWrappedAggregateLhs_FiltersAsExpected()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (g int, v int);
            insert t values (1, 10), (1, 20), (2, 5);
            select count(*) from (select g from t group by g having (sum(v)) > 25) x
            """));

    [TestMethod]
    public void Case_ParenWrappedValueLhs_EvaluatesAsExpected()
        => AreEqual("yes", new Simulation().ExecuteScalar("select case when (1 + 2) = 3 then 'yes' else 'no' end"));

    [TestMethod]
    public void Case_DoublyParenWrappedComparison_StillBooleanGroup()
        => AreEqual("yes", new Simulation().ExecuteScalar("select case when ((1) = (1)) then 'yes' else 'no' end"));

    private static int CountWhere(DbConnection connection, string predicate)
    {
        using var reader = connection.CreateCommand($"select a from t where {predicate}").ExecuteReader();
        var n = 0;
        while (reader.Read())
            n++;
        return n;
    }

    private static void AddTypedParameter(DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
