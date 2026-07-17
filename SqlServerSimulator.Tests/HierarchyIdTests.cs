using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>hierarchyid</c>: tree-path storage, the static
/// factories <c>hierarchyid::Parse</c> / <c>hierarchyid::GetRoot</c>, the
/// instance methods <c>.GetLevel</c> / <c>.GetAncestor</c> /
/// <c>.GetDescendant</c> / <c>.IsDescendantOf</c> / <c>.ToString</c>,
/// path comparison via <c>ORDER BY</c>, and Msg 6522 verbatim wording on
/// invalid input.
/// </summary>
/// <remarks>
/// hierarchyid is stored in its canonical OrdPath byte form, so
/// <c>CAST(node AS varbinary)</c> is byte-identical to a real server and
/// <c>ORDER BY</c> is an unsigned <c>memcmp</c>. All surfaces probe-match real
/// SQL Server 2025 (encoding/order re-anchored 2026-07-17).
/// </remarks>
[TestClass]
public sealed class HierarchyIdTests
{
    [TestMethod]
    public void Parse_Root_ProducesEmptyPath()
        => AreEqual("/", ExecuteScalar("select hierarchyid::Parse('/').ToString()"));

    [TestMethod]
    public void GetRoot_ProducesSlashPath()
        => AreEqual("/", ExecuteScalar("select hierarchyid::GetRoot().ToString()"));

    [TestMethod]
    [DataRow("/1/", "/1/")]
    [DataRow("/1/2/", "/1/2/")]
    [DataRow("/1/2/3/", "/1/2/3/")]
    [DataRow("/-1/", "/-1/")]
    [DataRow("/0/", "/0/")]
    [DataRow("/1.1/", "/1.1/")]
    [DataRow("/1/2.5/3/", "/1/2.5/3/")]
    [DataRow("/100/", "/100/")]
    [DataRow("/-200/", "/-200/")]
    public void Parse_ToString_RoundTrip(string input, string expected)
        => AreEqual(expected, ExecuteScalar($"select hierarchyid::Parse('{input}').ToString()"));

    [TestMethod]
    [DataRow("")]
    [DataRow("/1")]
    [DataRow("1/")]
    [DataRow("//")]
    [DataRow("/1//2/")]
    [DataRow("/a/")]
    public void Parse_BadInput_RaisesMsg6522(string input)
        => new Simulation().AssertSqlError($"select hierarchyid::Parse('{input}').ToString()", 6522);

    [TestMethod]
    [DataRow("/", 0)]
    [DataRow("/1/", 1)]
    [DataRow("/1/2/", 2)]
    [DataRow("/1/2/3/", 3)]
    [DataRow("/1.1/", 1)]
    [DataRow("/1/2.5/3/", 3)]
    public void GetLevel_ReturnsSegmentCount(string path, int expected)
        => AreEqual((short)expected, ExecuteScalar($"select hierarchyid::Parse('{path}').GetLevel()"));

    [TestMethod]
    [DataRow("/1/2/3/", 0, "/1/2/3/")]
    [DataRow("/1/2/3/", 1, "/1/2/")]
    [DataRow("/1/2/3/", 2, "/1/")]
    [DataRow("/1/2/3/", 3, "/")]
    public void GetAncestor_WalksUpThePath(string path, int depth, string expected)
        => AreEqual(expected, ExecuteScalar($"select hierarchyid::Parse('{path}').GetAncestor({depth}).ToString()"));

    [TestMethod]
    public void GetAncestor_BeyondRoot_ReturnsNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select hierarchyid::Parse('/1/2/3/').GetAncestor(4).ToString()"));

    [TestMethod]
    public void GetAncestor_NegativeDepth_RaisesMsg6522()
        => new Simulation().AssertSqlError("select hierarchyid::Parse('/1/').GetAncestor(-1).ToString()", 6522);

    [TestMethod]
    public void GetDescendant_BothNull_ProducesFirstChild()
        => AreEqual("/1/", ExecuteScalar("select hierarchyid::Parse('/').GetDescendant(null, null).ToString()"));

    [TestMethod]
    public void GetDescendant_AboveC1_IncrementsLastLabel()
        => AreEqual("/2/", ExecuteScalar("select hierarchyid::Parse('/').GetDescendant(hierarchyid::Parse('/1/'), null).ToString()"));

    [TestMethod]
    public void GetDescendant_BelowC2_DecrementsLastLabel()
        => AreEqual("/0/", ExecuteScalar("select hierarchyid::Parse('/').GetDescendant(null, hierarchyid::Parse('/1/')).ToString()"));

    [TestMethod]
    public void GetDescendant_GapBetweenC1AndC2_PicksMidpointInteger()
        => AreEqual("/2/", ExecuteScalar("select hierarchyid::Parse('/').GetDescendant(hierarchyid::Parse('/1/'), hierarchyid::Parse('/3/')).ToString()"));

    [TestMethod]
    public void GetDescendant_AdjacentSiblings_ExtendsWithSubOrdinal()
        => AreEqual("/1.1/", ExecuteScalar("select hierarchyid::Parse('/').GetDescendant(hierarchyid::Parse('/1/'), hierarchyid::Parse('/2/')).ToString()"));

    [TestMethod]
    public void GetDescendant_DeeperParent_PreservesSelfPath()
        => AreEqual("/1/3/", ExecuteScalar("select hierarchyid::Parse('/1/').GetDescendant(hierarchyid::Parse('/1/2/'), null).ToString()"));

    [TestMethod]
    public void GetDescendant_C1NotChildOfSelf_RaisesMsg6522()
        => new Simulation().AssertSqlError("select hierarchyid::Parse('/1/').GetDescendant(hierarchyid::Parse('/2/'), null).ToString()", 6522);

    [TestMethod]
    public void GetDescendant_C1GreaterThanC2_RaisesMsg6522()
        => new Simulation().AssertSqlError("select hierarchyid::Parse('/1/').GetDescendant(hierarchyid::Parse('/1/3/'), hierarchyid::Parse('/1/2/')).ToString()", 6522);

    [TestMethod]
    [DataRow("/", "/", true)]
    [DataRow("/1/", "/", true)]
    [DataRow("/1/2/", "/1/", true)]
    [DataRow("/1/2/", "/2/", false)]
    [DataRow("/", "/1/", false)]
    [DataRow("/1/2/3/", "/1/", true)]
    public void IsDescendantOf_FollowsPrefixContainment(string self, string ancestor, bool expected)
        => AreEqual(expected, ExecuteScalar($"select hierarchyid::Parse('{self}').IsDescendantOf(hierarchyid::Parse('{ancestor}'))"));

    [TestMethod]
    public void OrderBy_FollowsLexicographicPathOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (h hierarchyid not null);
            insert t values
                (hierarchyid::Parse('/2/')),
                (hierarchyid::Parse('/1/2/')),
                (hierarchyid::Parse('/1/')),
                (hierarchyid::Parse('/')),
                (hierarchyid::Parse('/1/1/')),
                (hierarchyid::Parse('/-1/')),
                (hierarchyid::Parse('/1/1/1/'))
            """);
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select h.ToString() from t order by h";
        using var reader = cmd.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read())
            actual.Add(reader.GetString(0));
        var expected = new[] { "/", "/-1/", "/1/", "/1/1/", "/1/1/1/", "/1/2/", "/2/" };
        AreEqual(string.Join(",", expected), string.Join(",", actual));
    }

    // CAST(node AS varbinary) is a zero-copy read of the canonical OrdPath
    // bytes — byte-identical to SQL Server 2025 (probe 2026-07-17).
    [TestMethod]
    [DataRow("/", "")]
    [DataRow("/1/", "58")]
    [DataRow("/79/", "DBF0")]
    [DataRow("/100/", "E02640")]
    [DataRow("/-200/", "1BE044")]
    [DataRow("/1/2.5/3/", "5BA378")]
    [DataRow("/6/1/10/", "957540")]
    public void Cast_HierarchyId_ToVarbinary_IsByteIdentical(string path, string expectedHex)
        => AreEqual(expectedHex, ToHex(ExecuteScalar($"select cast(hierarchyid::Parse('{path}') as varbinary(892))")));

    // The reverse CAST accepts a canonical OrdPath byte string and round-trips.
    [TestMethod]
    [DataRow("0x58", "/1/")]
    [DataRow("0x5BA378", "/1/2.5/3/")]
    [DataRow("0xE02640", "/100/")]
    [DataRow("0x1BE044", "/-200/")]
    public void Cast_Varbinary_ToHierarchyId_RoundTrips(string literal, string expectedPath)
        => AreEqual(expectedPath, ExecuteScalar($"select cast({literal} as hierarchyid).ToString()"));

    // A non-canonical byte string is rejected exactly as SQL Server rejects it
    // (probe 2026-07-17: the .NET-UDR error surfaces as Msg 6522).
    [TestMethod]
    [DataRow("0x59")]
    [DataRow("0x00")]
    [DataRow("0xFFFF")]
    public void Cast_Varbinary_NonCanonical_RaisesMsg6522(string literal)
        => new Simulation().AssertSqlError($"select cast({literal} as hierarchyid).ToString()", 6522);

    // Depth-first order across every modeled dimension: root, negatives,
    // siblings, deep paths, a dotted continuation, and multi-tier ordinals.
    // Expected order probed from SQL Server 2025 (2026-07-17).
    [TestMethod]
    public void OrderBy_SpansTiersAndDottedOrdinals()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (h hierarchyid not null);
            insert t values
                (hierarchyid::Parse('/100/')),
                (hierarchyid::Parse('/1/2.5/')),
                (hierarchyid::Parse('/-1/')),
                (hierarchyid::Parse('/1/3/')),
                (hierarchyid::Parse('/')),
                (hierarchyid::Parse('/1104/')),
                (hierarchyid::Parse('/1/1/1/')),
                (hierarchyid::Parse('/2/')),
                (hierarchyid::Parse('/1/2/')),
                (hierarchyid::Parse('/80/')),
                (hierarchyid::Parse('/-200/')),
                (hierarchyid::Parse('/1/1/')),
                (hierarchyid::Parse('/1/'))
            """);
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select h.ToString() from t order by h";
        using var reader = cmd.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read())
            actual.Add(reader.GetString(0));
        var expected = new[] { "/", "/-200/", "/-1/", "/1/", "/1/1/", "/1/1/1/", "/1/2/", "/1/2.5/", "/1/3/", "/2/", "/80/", "/100/", "/1104/" };
        AreEqual(string.Join(",", expected), string.Join(",", actual));
    }

    private static string ToHex(object? scalar)
        => scalar is byte[] b ? Convert.ToHexString(b) : throw new AssertFailedException($"expected byte[], got {scalar?.GetType().Name ?? "null"}");

    [TestMethod]
    public void Storage_RoundTripsThroughHeap()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, h hierarchyid not null);
            insert t values (1, hierarchyid::Parse('/1/2/3/')), (2, hierarchyid::Parse('/-1/'))
            """);
        AreEqual("/1/2/3/", sim.ExecuteScalar("select h.ToString() from t where id = 1"));
        AreEqual("/-1/", sim.ExecuteScalar("select h.ToString() from t where id = 2"));
    }

    [TestMethod]
    public void Null_Hierarchyid_RoundTripsAsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, h hierarchyid null);
            insert t values (1, null)
            """);
        AreEqual(DBNull.Value, sim.ExecuteScalar("select h from t where id = 1"));
    }

    [TestMethod]
    public void DeclareVariable_AssignParse_ReadGetLevel()
        => AreEqual((short)3, ExecuteScalar("""
            declare @h hierarchyid = hierarchyid::Parse('/1/2/3/');
            select @h.GetLevel()
            """));

    [TestMethod]
    public void Parse_NullArgument_ReturnsNullHierarchyid()
        => AreEqual(DBNull.Value, ExecuteScalar("select hierarchyid::Parse(cast(null as nvarchar(100))).ToString()"));

    [TestMethod]
    public void IsDescendantOf_NullArgument_ReturnsNullBit()
        => AreEqual(DBNull.Value, ExecuteScalar("select hierarchyid::Parse('/1/').IsDescendantOf(cast(null as hierarchyid))"));

    [TestMethod]
    public void SysTypes_ListsHierarchyId()
    {
        var sim = new Simulation();
        AreEqual(240, sim.ExecuteScalar("select cast(system_type_id as int) from sys.types where name = 'hierarchyid'"));
        AreEqual(128, sim.ExecuteScalar("select user_type_id from sys.types where name = 'hierarchyid'"));
    }

    // DATALENGTH measures the OrdPath wire serialization (what a real server
    // stores and DacFx's BCP length prefix records), not the simulator-native
    // segment-array byte form. Probe-confirmed against SQL Server 2025
    // (2026-07-16): DATALENGTH(CAST('/N/' AS hierarchyid)).
    [TestMethod]
    [DataRow("/", 0)]
    [DataRow("/0/", 1)]
    [DataRow("/79/", 2)]
    [DataRow("/1/2/", 2)]
    [DataRow("/1/2.3/", 2)]
    [DataRow("/3/4/7/8/15/16/79/", 7)]
    public void DataLength_HierarchyId_MeasuresOrdPathSerialization(string path, int expected)
        => AreEqual(expected, ExecuteScalar($"select datalength(hierarchyid::Parse('{path}'))"));
}
