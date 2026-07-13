using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Round-trip tests for the <c>DbType</c> → <c>SqlType.ConvertParameter</c>
/// dispatch. Each DbType lands on a specific concrete <c>SqlType</c> via
/// <c>SqlType.GetByDbType</c>; this class exercises the per-type
/// <c>ConvertParameter</c> overrides for every reachable mapping. Many of
/// these were 0%-covered prior — the live overrides just weren't being
/// hit by name-typed parameters.
/// </summary>
[TestClass]
public sealed class ParameterBindingByDbTypeTests
{
    private static (DbConnection conn, DbCommand cmd) Open(string columnType)
    {
        var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand($"create table t (v {columnType})").ExecuteNonQuery();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "insert t values (@v)";
        return (conn, cmd);
    }

    private static DbParameter Param(DbCommand cmd, DbType dbType, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = "@v";
        p.DbType = dbType;
        p.Value = value;
        _ = cmd.Parameters.Add(p);
        return p;
    }

    private static object? ReadBack(DbConnection conn) =>
        conn.CreateCommand("select v from t").ExecuteScalar();

    [TestMethod]
    public void Xml_BindsViaDbTypeXml()
    {
        var (conn, cmd) = Open("xml");
        _ = Param(cmd, DbType.Xml, "<r><a>1</a></r>");
        AreEqual(1, cmd.ExecuteNonQuery());
        AreEqual("<r><a>1</a></r>", ReadBack(conn));
    }

    [TestMethod]
    public void SizeMinusOne_DeclaresMaxTypedParameter()
    {
        // SqlClient's Size = -1 convention means varchar(max) /
        // nvarchar(max) / varbinary(max); SELECT INTO materializes the
        // parameter's declared type, observable via sys.columns.
        var conn = new Simulation().CreateOpenConnection();
        var cases = new (DbType DbType, object Value, string Table)[]
        {
            (DbType.String, new string('x', 80_000), "t_nvarchar"),
            (DbType.AnsiString, new string('y', 80_000), "t_varchar"),
            (DbType.Binary, new byte[70_000], "t_varbinary"),
        };
        foreach (var (dbType, value, table) in cases)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select @v as c into {table}";
            var p = cmd.CreateParameter();
            p.ParameterName = "@v";
            p.DbType = dbType;
            p.Size = -1;
            p.Value = value;
            _ = cmd.Parameters.Add(p);
            _ = cmd.ExecuteNonQuery();
            AreEqual((short)-1, conn.CreateCommand($"select max_length from sys.columns where object_id = object_id('{table}')").ExecuteScalar(), table);
        }
    }

    [TestMethod]
    public void Int64_BindsViaBigIntConvertParameter()
    {
        var (conn, cmd) = Open("bigint");
        using (conn)
        using (cmd)
        {
            _ = Param(cmd, DbType.Int64, 1234567890123L);
            AreEqual(1, cmd.ExecuteNonQuery());
            AreEqual(1234567890123L, ReadBack(conn));
        }
    }

    [TestMethod]
    public void Currency_BindsViaMoneyConvertParameter()
    {
        var (conn, cmd) = Open("money");
        using (conn)
        using (cmd)
        {
            _ = Param(cmd, DbType.Currency, 99.50m);
            AreEqual(1, cmd.ExecuteNonQuery());
            AreEqual(99.50m, ReadBack(conn));
        }
    }

    [TestMethod]
    public void Single_BindsViaRealConvertParameter()
    {
        var (conn, cmd) = Open("real");
        using (conn)
        using (cmd)
        {
            _ = Param(cmd, DbType.Single, 3.5f);
            AreEqual(1, cmd.ExecuteNonQuery());
            AreEqual(3.5f, ReadBack(conn));
        }
    }

    [TestMethod]
    public void Time_TypedDbType_BindsViaTimeConvertParameter()
    {
        var (conn, cmd) = Open("time(7)");
        using (conn)
        using (cmd)
        {
            _ = Param(cmd, DbType.Time, new TimeSpan(13, 45, 30));
            AreEqual(1, cmd.ExecuteNonQuery());
            AreEqual(new TimeSpan(13, 45, 30), ReadBack(conn));
        }
    }

    [TestMethod]
    public void DateTimeOffset_TypedDbType_BindsViaDateTimeOffsetConvertParameter()
    {
        var (conn, cmd) = Open("datetimeoffset(7)");
        using (conn)
        using (cmd)
        {
            var v = new DateTimeOffset(2024, 6, 15, 13, 0, 0, TimeSpan.FromHours(-7));
            _ = Param(cmd, DbType.DateTimeOffset, v);
            AreEqual(1, cmd.ExecuteNonQuery());
            AreEqual(v, ReadBack(conn));
        }
    }

    [TestMethod]
    public void Boolean_BindsViaBitConvertParameter()
    {
        var (conn, cmd) = Open("bit");
        using (conn)
        using (cmd)
        {
            _ = Param(cmd, DbType.Boolean, true);
            AreEqual(1, cmd.ExecuteNonQuery());
            IsTrue((bool)ReadBack(conn)!);
        }
    }
}
