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

    [TestMethod]
    public void Where_AndChain_TwoPredicates_BothMustHold()
    {
        // Regression for the silent-wrong-rows bug: pre-fix, `where a=X and
        // b=Y` parsed only `a=X` and dropped `and b=Y`, returning the first
        // row matching just the left predicate. With composite-PK row
        // (1,3,200) present, the right answer is 200, not the (1,2,100) row
        // that matches only `a=1`.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int not null, b int not null, c int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, 2, 100), (1, 3, 200), (2, 2, 300)").ExecuteNonQuery();

        AreEqual(200, connection.CreateCommand("select c from t where a = 1 and b = 3").ExecuteScalar());
        AreEqual(100, connection.CreateCommand("select c from t where a = 1 and b = 2").ExecuteScalar());
        AreEqual(300, connection.CreateCommand("select c from t where a = 2 and b = 2").ExecuteScalar());
    }

    [TestMethod]
    public void Where_AndChain_ThreePredicates_AllMustHold()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int, c int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, 2, 100), (1, 2, 200)").ExecuteNonQuery();

        AreEqual(100, connection.CreateCommand("select c from t where a = 1 and b = 2 and c = 100").ExecuteScalar());
        IsNull(connection.CreateCommand("select c from t where a = 1 and b = 2 and c = 999").ExecuteScalar());
    }

    [TestMethod]
    public void Where_AndChain_NullOperand_ExcludesRow()
    {
        // SQL Server WHERE: NULL on either side of AND treats the row as
        // excluded (the whole predicate evaluates to false/UNKNOWN).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, null), (1, 2)").ExecuteNonQuery();

        // Only (1, 2) passes both predicates; (1, NULL) fails the b=2 side.
        using var reader = connection.CreateCommand("select b from t where a = 1 and b = 2").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Where_OrChain_TwoPredicates_EitherMustHold()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, 2), (3, 4), (5, 6)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select a from t where a = 1 or b = 4").ExecuteReader();
        var matched = new List<int>();
        while (reader.Read())
            matched.Add((int)reader[0]);
        CollectionAssert.AreEquivalent(new[] { 1, 3 }, matched);
    }

    [TestMethod]
    public void Where_AndOrPrecedence_AndBindsTighter()
    {
        // `a=1 OR b=2 AND c=2` parses as `a=1 OR (b=2 AND c=2)` — standard
        // SQL precedence (AND binds tighter than OR). Probe of real SQL
        // Server returned 3 rows for this dataset; simulator must match.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int, c int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, 2, 3), (1, 3, 2), (2, 2, 2), (0, 0, 5)").ExecuteNonQuery();

        var matched = CountWhere(connection, "a = 1 or b = 2 and c = 2");
        AreEqual(3, matched); // (1,2,3), (1,3,2), (2,2,2)
    }

    [TestMethod]
    public void Where_ParensOverridePrecedence()
    {
        // Same data as the precedence test but with explicit parens forcing
        // `(a=1 OR b=2) AND c=2` — only rows where c=2 AND (a=1 OR b=2).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int, c int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, 2, 3), (1, 3, 2), (2, 2, 2), (0, 0, 5)").ExecuteNonQuery();

        var matched = CountWhere(connection, "(a = 1 or b = 2) and c = 2");
        AreEqual(2, matched); // (1,3,2), (2,2,2)
    }

    [TestMethod]
    public void Where_NotPredicate_ExcludesNullViaTriState()
    {
        // SQL Server: NOT (NULL = 1) → NOT NULL → NULL → row excluded by
        // WHERE. Simulator's tri-state Run propagates UNKNOWN; only a true
        // Run result lets a row through.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (null), (1), (2)").ExecuteNonQuery();

        AreEqual(1, CountWhere(connection, "not (a = 1)")); // only the row with a=2 passes
    }

    [TestMethod]
    public void Where_NotEqual_ExcludesEqualAndNull()
    {
        // <> mirrors NOT (a = X)'s tri-state semantics: NULL operand → null
        // → exclude.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (null), (1), (2)").ExecuteNonQuery();

        AreEqual(1, CountWhere(connection, "a <> 1"));
    }

    [TestMethod]
    public void Where_DoubleNot_CancelsToOriginal()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (null), (1), (2)").ExecuteNonQuery();

        AreEqual(1, CountWhere(connection, "not not (a = 1)"));
    }

    [TestMethod]
    public void Where_NestedParensPredicate()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, 2), (1, 3), (2, 2)").ExecuteNonQuery();

        AreEqual(1, CountWhere(connection, "((a = 1 and b = 2))"));
    }

    [TestMethod]
    public void Where_IsNull_MatchesNullsOnly()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (null), (1), (2)").ExecuteNonQuery();

        AreEqual(1, CountWhere(connection, "a is null"));
    }

    [TestMethod]
    public void Where_IsNotNull_ExcludesNulls()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (null), (1), (2)").ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a is not null"));
    }

    [TestMethod]
    public void Where_IsNullCombinesWithOr()
    {
        // The standard "include NULLs in addition to a value match" shape.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (null), (1), (2)").ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a = 1 or a is null"));
    }

    [TestMethod]
    public void Where_InList_MatchesAnyMember()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1), (2), (3), (4)").ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a in (1, 3)"));
    }

    [TestMethod]
    public void Where_NotInList_ExcludesMembers()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1), (2), (3), (4)").ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a not in (1, 3)"));
    }

    [TestMethod]
    public void Where_InList_NullLeftSideIsExcluded()
    {
        // `NULL IN (1, 2)` is UNKNOWN per three-valued logic; UNKNOWN
        // excludes from WHERE.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (null), (1), (2)").ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a in (1, 2)"));
    }

    [TestMethod]
    public void Where_NotInList_WithNullElementExcludesEverythingViaUnknown()
    {
        // `1 NOT IN (1, NULL)` is false (1 matches 1).
        // `2 NOT IN (1, NULL)` is UNKNOWN (no match seen but NULL might be 2)
        // → excluded from WHERE. Only the matched-and-thus-false row is
        // excluded explicitly; the other rows fall through UNKNOWN-as-exclude.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1), (2), (3)").ExecuteNonQuery();

        AreEqual(0, CountWhere(connection, "a not in (1, null)"));
    }

    [TestMethod]
    public void Where_InList_WithStringValues()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, status nvarchar(20))").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1, 'active'), (2, 'pending'), (3, 'archived')").ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "status in ('active', 'archived')"));
    }

    [TestMethod]
    public void Where_InList_PromotesAcrossNumericTypes()
    {
        // Member-list values are numeric literals (int by default); column
        // is bigint. SQL Server promotes to a common numeric type for the
        // comparison.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a bigint)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert into t values (1), (2), (3)").ExecuteNonQuery();

        AreEqual(2, CountWhere(connection, "a in (1, 3)"));
    }

    private static int CountWhere(System.Data.Common.DbConnection connection, string predicate)
    {
        using var reader = connection.CreateCommand($"select a from t where {predicate}").ExecuteReader();
        var n = 0;
        while (reader.Read())
            n++;
        return n;
    }

    private static void AddTypedParameter(System.Data.Common.DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    [TestMethod]
    public void Where_FivePartColumnReference_RaisesMsg4104()
    {
        // SQL Server's grammar caps qualified column references at 4 parts
        // (linked.db.schema.object). Real SQL Server parses arbitrary-many
        // parts and fails at resolution with Msg 4104; the simulator's
        // MultiPartName cap raises the same Msg 4104 at parse time, with
        // the full attempted dotted name in the message — matching the
        // user-visible wire effect.
        var ex = Throws<System.Data.Common.DbException>(() =>
            _ = new Simulation().ExecuteScalar("select 1 where a.b.c.d.e = 1"));
        AreEqual("4104", ex.Data["HelpLink.EvtID"]);
        AreEqual("The multi-part identifier \"a.b.c.d.e\" could not be bound.", ex.Message);
    }
}
