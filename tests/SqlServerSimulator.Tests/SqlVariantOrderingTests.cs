using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sql_variant</c> cross-type comparison and ordering: two-level compare —
/// datatype-family rank first (uniqueidentifier &lt; binary &lt; character &lt;
/// exact numeric &lt; approximate &lt; date/time, value-blind across
/// families), then value within the family with true equality across base
/// types. Probe-confirmed against SQL Server 2025 (2026-07-19).
/// </summary>
[TestClass]
public sealed class SqlVariantOrderingTests
{
    /// <summary>
    /// One value per base type, values chosen so no two compare equal —
    /// within-family ties have undefined relative order on real SQL Server
    /// (probed: plan-dependent), so the full-order assertion avoids them.
    /// </summary>
    private static Simulation CreateMixedTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v sql_variant);
            insert t values
                (cast('6F9619FF-8B86-D011-B42D-00C04FC964FF' as uniqueidentifier)),
                (cast(0x01 as varbinary(10))),
                (cast(0x0202 as binary(2))),
                (cast(N'apple' as nvarchar(20))),
                (cast('banana' as varchar(20))),
                (cast('cherry' as char(6))),
                (cast(0.5 as smallmoney)),
                (cast(1 as bit)),
                (cast(2 as tinyint)),
                (cast(3 as smallint)),
                (cast(4 as int)),
                (cast(5 as bigint)),
                (cast(6.5 as decimal(5,2))),
                (cast(7.5 as money)),
                (cast(2.5 as real)),
                (cast(3.5 as float)),
                (cast('01:00' as time)),
                (cast('2020-06-15' as date)),
                (cast('2020-06-16 12:00' as smalldatetime)),
                (cast('2020-06-17' as datetime)),
                (cast('2020-06-18' as datetime2)),
                (cast('2020-06-19 12:00:00 +02:00' as datetimeoffset))
            """);
        return sim;
    }

    /// <summary>
    /// The decimal row reports BaseType <c>numeric</c> — the simulator's known
    /// decimal-family naming divergence (real reports <c>decimal</c> for a
    /// decimal-declared inner; see the backlog quirk). Ordering is unaffected.
    /// </summary>
    private static readonly string[] ExpectedAscendingBaseTypes =
    [
        "uniqueidentifier", "varbinary", "binary",
        "nvarchar", "varchar", "char",
        "smallmoney", "bit", "tinyint", "smallint", "int", "bigint", "numeric", "money",
        "real", "float",
        "time", "date", "smalldatetime", "datetime", "datetime2", "datetimeoffset",
    ];

    [TestMethod]
    public void OrderBy_FullFamilyHierarchy_Ascending()
    {
        using var reader = CreateMixedTable().ExecuteReader(
            "select cast(sql_variant_property(v, 'BaseType') as nvarchar(20)) from t order by v");
        foreach (var expected in ExpectedAscendingBaseTypes)
        {
            IsTrue(reader.Read());
            AreEqual(expected, reader.GetString(0));
        }

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void OrderBy_Descending_IsExactReverse()
    {
        using var reader = CreateMixedTable().ExecuteReader(
            "select cast(sql_variant_property(v, 'BaseType') as nvarchar(20)) from t order by v desc");
        foreach (var expected in ExpectedAscendingBaseTypes.Reverse())
        {
            IsTrue(reader.Read());
            AreEqual(expected, reader.GetString(0));
        }
    }

    [TestMethod]
    public void OrderBy_Null_SortsLowestAscending()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v sql_variant); insert t values (cast(1 as int)), (null), (cast(N'z' as nvarchar(5)))");
        using var reader = sim.ExecuteReader("select v from t order by v");
        IsTrue(reader.Read());
        AreEqual(DBNull.Value, reader.GetValue(0));
        IsTrue(reader.Read());
        AreEqual("z", reader.GetValue(0));
        IsTrue(reader.Read());
        AreEqual(1, reader.GetValue(0));
    }

    // Cross-family comparison ignores the values entirely.
    [TestMethod]
    public void CrossFamily_ValueBlind()
    {
        var sim = new Simulation();
        AreEqual("lt", sim.ExecuteScalar("""
            declare @a sql_variant = cast(1000000 as bigint), @b sql_variant = cast(0.5 as float);
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));
        AreEqual("gt", sim.ExecuteScalar("""
            declare @a sql_variant = cast('00:00' as time), @b sql_variant = cast(1e300 as float);
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));
        AreEqual("gt", sim.ExecuteScalar("""
            declare @a sql_variant = cast(0 as bit), @b sql_variant = cast(N'zzz' as nvarchar(5));
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));
    }

    // Unicode and non-Unicode strings are ONE family, compared by value.
    [TestMethod]
    public void CharacterFamily_UnicodeAndAnsi_ValueOrdered()
        => AreEqual("lt", new Simulation().ExecuteScalar("""
            declare @a sql_variant = cast(N'aaa' as nvarchar(5)), @b sql_variant = cast('zzz' as varchar(5));
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));

    [TestMethod]
    public void ExactFamily_CrossType_ValueOrderedAndEqual()
    {
        var sim = new Simulation();
        AreEqual("lt", sim.ExecuteScalar("""
            declare @a sql_variant = cast(3 as int), @b sql_variant = cast(5 as bigint);
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));
        AreEqual(1, sim.ExecuteScalar("""
            declare @a sql_variant = cast(5 as int), @b sql_variant = cast(5 as bigint);
            select case when @a = @b then 1 else 0 end
            """));
        AreEqual(1, sim.ExecuteScalar("""
            declare @a sql_variant = cast(5 as int), @b sql_variant = cast(5.00 as decimal(5,2));
            select case when @a = @b then 1 else 0 end
            """));
        AreEqual(1, sim.ExecuteScalar("""
            declare @a sql_variant = cast(N'x' as nvarchar(5)), @b sql_variant = cast('x' as varchar(5));
            select case when @a = @b then 1 else 0 end
            """));
    }

    // The approximate family sits above every exact value regardless of magnitude.
    [TestMethod]
    public void ApproximateFamily_AboveAllExactValues()
        => AreEqual("gt", new Simulation().ExecuteScalar("""
            declare @a sql_variant = cast(4.0 as float), @b sql_variant = cast(1000000 as decimal(10,0));
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));

    [TestMethod]
    public void GroupBy_CollapsesByFamilyScopedEquality()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v sql_variant);
            insert t values
                (cast(5 as bigint)), (cast(5 as int)), (cast(5.00 as decimal(5,2))),
                (cast(5.0 as float)),
                (cast(N'x' as nvarchar(5))), (cast('x' as varchar(5)))
            """);
        using var reader = sim.ExecuteReader("""
            select cast(sql_variant_property(v, 'BaseType') as nvarchar(20)), count(*)
            from t group by v order by v
            """);
        // character family (2) < exact family (3) < approximate family (1);
        // the exact group's representative is the FIRST value encountered
        // (bigint inserted first), matching real's plan-order representative.
        IsTrue(reader.Read());
        AreEqual("nvarchar", reader.GetString(0));
        AreEqual(2, reader.GetInt32(1));
        IsTrue(reader.Read());
        AreEqual("bigint", reader.GetString(0));
        AreEqual(3, reader.GetInt32(1));
        IsTrue(reader.Read());
        AreEqual("float", reader.GetString(0));
        AreEqual(1, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Distinct_SameFamilyScopedEquality()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (v sql_variant);
            insert t values
                (cast(5 as bigint)), (cast(5 as int)), (cast(5.00 as decimal(5,2))),
                (cast(5.0 as float)),
                (cast(N'x' as nvarchar(5))), (cast('x' as varchar(5)));
            select count(*) from (select distinct v from t) d
            """));

    [TestMethod]
    public void MinMax_PickFamilyExtremes()
    {
        using var reader = CreateMixedTable().ExecuteReader("""
            select cast(sql_variant_property(min(v), 'BaseType') as nvarchar(20)),
                   cast(sql_variant_property(max(v), 'BaseType') as nvarchar(20))
            from t
            """);
        IsTrue(reader.Read());
        AreEqual("uniqueidentifier", reader.GetString(0));
        AreEqual("datetimeoffset", reader.GetString(1));
    }

    // time anchors to 1900-01-01; datetimeoffset compares by UTC instant.
    [TestMethod]
    public void DateTimeFamily_ComparedAsInstant()
    {
        var sim = new Simulation();
        AreEqual("lt", sim.ExecuteScalar("""
            declare @a sql_variant = cast('23:59' as time), @b sql_variant = cast('2050-01-01' as date);
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));
        AreEqual("lt", sim.ExecuteScalar("""
            declare @a sql_variant = cast('1990-01-01 00:00:00 +00:00' as datetimeoffset), @b sql_variant = cast('2050-06-15' as datetime);
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));
    }

    // Variant-wrapped strings in different collations compare by code point
    // without a Msg 468 conflict (unlike bare cross-collation varchar).
    [TestMethod]
    public void CharacterFamily_CrossCollation_CodePointCompare()
        => AreEqual("gt", new Simulation().ExecuteScalar("""
            declare @a sql_variant = cast('abc' collate Latin1_General_BIN as varchar(5)),
                    @b sql_variant = cast('ABC' collate SQL_Latin1_General_CP1_CI_AS as varchar(5));
            select case when @a < @b then 'lt' when @a > @b then 'gt' else 'eq' end
            """));
}
