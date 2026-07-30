using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <see cref="DbParameterCollection"/> contract
/// surfaced through <c>DbCommand.Parameters</c>. SqlClient-style consumers
/// reach these methods directly (Add, Contains, IndexOf, RemoveAt by name,
/// etc.) when manipulating parameter collections; the simulator's
/// implementation is exercised by frameworks like Dapper / EF Core's
/// command interception layer.
/// </summary>
[TestClass]
public sealed class DbParameterCollectionTests
{
    private static (DbConnection conn, DbCommand cmd) Open()
    {
        var conn = new Simulation().CreateOpenConnection();
        return (conn, conn.CreateCommand());
    }

    private static DbParameter MakeParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }

    [TestMethod]
    public void Add_ReturnsIndex_ParametersAccessibleByPosition()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            var p1 = MakeParam(cmd, "@a", 1);
            var p2 = MakeParam(cmd, "@b", 2);
            AreEqual(0, cmd.Parameters.Add(p1));
            AreEqual(1, cmd.Parameters.Add(p2));
            HasCount(2, cmd.Parameters);
            AreSame(p1, cmd.Parameters[0]);
            AreSame(p2, cmd.Parameters[1]);
        }
    }

    [TestMethod]
    public void IndexOf_ByName_IsCaseInsensitive()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            _ = cmd.Parameters.Add(MakeParam(cmd, "@Foo", 1));
            AreEqual(0, cmd.Parameters.IndexOf("@foo"));
            AreEqual(0, cmd.Parameters.IndexOf("@FOO"));
            AreEqual(-1, cmd.Parameters.IndexOf("@bar"));
        }
    }

    [TestMethod]
    public void Contains_ByName_AndByObject()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            var p = MakeParam(cmd, "@x", 1);
            _ = cmd.Parameters.Add(p);
            var hasX = cmd.Parameters.Contains("@x");
            var hasY = cmd.Parameters.Contains("@y");
            var hasP = cmd.Parameters.Contains(p);
            IsTrue(hasX);
            IsFalse(hasY);
            IsTrue(hasP);
        }
    }

    [TestMethod]
    public void IndexerByName_GetAndSet()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            var p = MakeParam(cmd, "@x", 1);
            _ = cmd.Parameters.Add(p);
            AreSame(p, cmd.Parameters["@x"]);
            var replacement = MakeParam(cmd, "@x", 99);
            cmd.Parameters["@x"] = replacement;
            AreSame(replacement, cmd.Parameters["@x"]);
        }
    }

    [TestMethod]
    public void IndexerByName_MissingNameThrows()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
            _ = Throws<ArgumentException>(() => _ = cmd.Parameters["@missing"]);
    }

    [TestMethod]
    public void RemoveAt_ByIndex_AndByName()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            _ = cmd.Parameters.Add(MakeParam(cmd, "@a", 1));
            _ = cmd.Parameters.Add(MakeParam(cmd, "@b", 2));
            _ = cmd.Parameters.Add(MakeParam(cmd, "@c", 3));
            cmd.Parameters.RemoveAt(0);
            HasCount(2, cmd.Parameters);
            cmd.Parameters.RemoveAt("@c");
            HasCount(1, cmd.Parameters);
            AreEqual("@b", cmd.Parameters[0].ParameterName);
        }
    }

    [TestMethod]
    public void Remove_ByObject()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            var p = MakeParam(cmd, "@x", 1);
            _ = cmd.Parameters.Add(p);
            cmd.Parameters.Remove(p);
            IsEmpty(cmd.Parameters);
        }
    }

    [TestMethod]
    public void Insert_PutsParameterAtSpecificIndex()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            _ = cmd.Parameters.Add(MakeParam(cmd, "@a", 1));
            _ = cmd.Parameters.Add(MakeParam(cmd, "@c", 3));
            cmd.Parameters.Insert(1, MakeParam(cmd, "@b", 2));
            AreEqual("@a", cmd.Parameters[0].ParameterName);
            AreEqual("@b", cmd.Parameters[1].ParameterName);
            AreEqual("@c", cmd.Parameters[2].ParameterName);
        }
    }

    [TestMethod]
    public void AddRange_AddsAllElements()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            var batch = new[] { MakeParam(cmd, "@a", 1), MakeParam(cmd, "@b", 2) };
            cmd.Parameters.AddRange(batch);
            HasCount(2, cmd.Parameters);
        }
    }

    [TestMethod]
    public void Clear_EmptiesCollection()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            _ = cmd.Parameters.Add(MakeParam(cmd, "@a", 1));
            _ = cmd.Parameters.Add(MakeParam(cmd, "@b", 2));
            cmd.Parameters.Clear();
            IsEmpty(cmd.Parameters);
        }
    }

    [TestMethod]
    public void CopyTo_CopiesIntoArray()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            _ = cmd.Parameters.Add(MakeParam(cmd, "@a", 1));
            _ = cmd.Parameters.Add(MakeParam(cmd, "@b", 2));
            var dest = new DbParameter[2];
            cmd.Parameters.CopyTo(dest, 0);
            AreEqual("@a", dest[0].ParameterName);
            AreEqual("@b", dest[1].ParameterName);
        }
    }

    [TestMethod]
    public void Enumerator_YieldsParametersInOrder()
    {
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            _ = cmd.Parameters.Add(MakeParam(cmd, "@a", 1));
            _ = cmd.Parameters.Add(MakeParam(cmd, "@b", 2));
            var names = new List<string>();
            foreach (DbParameter p in cmd.Parameters)
                names.Add(p.ParameterName);
            CollectionAssert.AreEqual(new[] { "@a", "@b" }, names);
        }
    }

    [TestMethod]
    public void SyncRoot_NotNull() =>
        IsNotNull(new Simulation().CreateOpenConnection().CreateCommand().Parameters.SyncRoot);

    /// <summary>
    /// The strongly-typed indexers shadow the <see cref="DbParameterCollection"/>
    /// ones so a consumer that downcasts reaches <see cref="SimulatedDbParameter"/>
    /// without a cast — the same shape SqlClient exposes.
    /// </summary>
    [TestMethod]
    public void TypedIndexers_GetAndSetByPositionAndName()
    {
        using var connection = new Simulation().CreateDbConnection();
        using var command = connection.CreateCommand();
        var first = command.CreateParameter();
        first.ParameterName = "@a";
        first.Value = 1;
        _ = command.Parameters.Add(first);

        var byIndex = command.Parameters[0];
        AreEqual("@a", byIndex.ParameterName);
        AreEqual("@a", command.Parameters["@a"].ParameterName);

        var replacement = command.CreateParameter();
        replacement.ParameterName = "@a";
        replacement.Value = 2;
        command.Parameters[0] = replacement;
        AreEqual(2, command.Parameters["@a"].Value);

        var third = command.CreateParameter();
        third.ParameterName = "@a";
        third.Value = 3;
        command.Parameters["@a"] = third;
        AreEqual(3, command.Parameters[0].Value);
    }

    /// <summary>An unknown name is an argument failure, not a null return.</summary>
    [TestMethod]
    public void TypedIndexer_UnknownName_Throws()
    {
        using var connection = new Simulation().CreateDbConnection();
        using var command = connection.CreateCommand();
        _ = Throws<ArgumentException>(() => _ = command.Parameters["@nope"]);
    }
}
