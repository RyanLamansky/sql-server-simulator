using System.Text;
using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Regression guard for the <c>sp_executesql</c> (ProcId 10) positional
/// parameter binding. mssql-jdbc's <c>PreparedStatement</c> sends the value
/// parameters positionally with an empty name (name=''), same as native ODBC's
/// <c>sp_prepexec</c>; the handler must name them from the declaration
/// (parameter index 1), or the statement fails with Msg 137 "Must declare the
/// scalar variable "@P0".". SqlClient names its own sp_executesql params, so its
/// oracle never exercised this path.
/// </summary>
[TestClass]
public sealed class SpExecuteSqlUnnamedParameterTests
{
    [TestMethod]
    public void ExecuteSqlMessage_ValueParameterArrivesUnnamed()
    {
        var request = ParseSingleExecuteSql();

        AreEqual(Tds.ProcIdExecuteSql, request.ProcId);
        HasCount(3, request.Parameters);
        AreEqual("SELECT @P0", request.Parameters[0].Value);   // statement
        AreEqual("@P0 int", request.Parameters[1].Value);      // declaration
        AreEqual("", request.Parameters[2].Name);              // the JDBC unnamed value param
        AreEqual(System.Data.DbType.Int32, request.Parameters[2].DbType);
        AreEqual(42, Convert.ToInt32(request.Parameters[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void NameUnnamedParameters_Skip2_BindsPositionalValueToDeclaredName()
    {
        var request = ParseSingleExecuteSql();

        // The sp_executesql handler names value params (index 2+) from the decl.
        var bound = TdsSession.NameUnnamedParameters(request.Parameters, 2, ["@P0"]);

        HasCount(1, bound);
        AreEqual("@P0", bound[0].Name);
        AreEqual(42, Convert.ToInt32(bound[0].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Builds a JDBC-shaped sp_executesql RPC message:
    /// params = [ 'SELECT @P0' (stmt) | '@P0 int' (decl) | 42 (unnamed value) ].
    /// </summary>
    private static TdsRpcRequest ParseSingleExecuteSql()
    {
        var body = new List<byte>();
        AddUInt16(body, 0xFFFF);            // numeric ProcId form
        AddUInt16(body, Tds.ProcIdExecuteSql);
        AddUInt16(body, 0);                 // option flags

        AddNVarcharParameter(body, "SELECT @P0");                       // statement
        AddNVarcharParameter(body, "@P0 int");                          // declaration
        AddIntNParameter(body, name: "", isOutput: false, value: 42);   // unnamed value

        // ALL_HEADERS: a length field covering only itself, so parsing starts at offset 4.
        var payload = new List<byte>();
        AddUInt32(payload, 4);
        payload.AddRange(body);

        var requests = TdsRpcRequest.ParseMessage([.. payload], "master");
        HasCount(1, requests);
        return requests[0];
    }

    private static void AddIntNParameter(List<byte> buffer, string name, bool isOutput, int? value)
    {
        AddBVarchar(buffer, name);
        buffer.Add(isOutput ? (byte)0x01 : (byte)0x00);
        buffer.Add(0x26);   // INTN
        buffer.Add(4);      // declared length (int)
        if (value is int v)
        {
            buffer.Add(4);  // value length
            AddInt32(buffer, v);
        }
        else
        {
            buffer.Add(0);  // NULL
        }
    }

    private static void AddNVarcharParameter(List<byte> buffer, string text)
    {
        AddBVarchar(buffer, "");   // unnamed
        buffer.Add(0x00);          // status
        buffer.Add(0xE7);          // NVARCHAR
        AddUInt16(buffer, 8000);   // declared max byte length
        AddUInt32(buffer, 0);      // collation info
        buffer.Add(0);             // collation sort id
        var bytes = Encoding.Unicode.GetBytes(text);
        AddUInt16(buffer, (ushort)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void AddBVarchar(List<byte> buffer, string text)
    {
        buffer.Add((byte)text.Length);
        buffer.AddRange(Encoding.Unicode.GetBytes(text));
    }

    private static void AddUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)value);
        buffer.Add((byte)(value >> 8));
    }

    private static void AddUInt32(List<byte> buffer, uint value)
    {
        for (var i = 0; i < 4; i++)
            buffer.Add((byte)(value >> (i * 8)));
    }

    private static void AddInt32(List<byte> buffer, int value) => AddUInt32(buffer, (uint)value);
}
