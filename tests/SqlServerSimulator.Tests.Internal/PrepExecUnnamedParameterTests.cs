using System.Text;
using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Regression guard for the native-ODBC <c>sp_prepexec</c> (ProcId 13) parameter
/// binding. ODBC Driver 18 sends the prepared statement's value parameters
/// positionally with an empty name (name=''); the handler must name them from
/// the prepared declaration, exactly as <c>sp_execute</c> does, or the statement
/// fails with Msg 137 "Must declare the scalar variable "@P1".". SqlClient names
/// its own params, so its oracle never exercised this path.
/// </summary>
[TestClass]
public sealed class PrepExecUnnamedParameterTests
{
    [TestMethod]
    public void PrepExecMessage_ValueParameterArrivesUnnamed()
    {
        var request = ParseSinglePrepExec();

        AreEqual(Tds.ProcIdPrepExec, request.ProcId);
        HasCount(4, request.Parameters);
        AreEqual("@P1 int", request.Parameters[1].Value);          // declaration
        AreEqual("SELECT @P1", request.Parameters[2].Value);       // statement
        AreEqual("", request.Parameters[3].Name);                  // the ODBC unnamed value param
        AreEqual(System.Data.DbType.Int32, request.Parameters[3].DbType);
        AreEqual(5, Convert.ToInt32(request.Parameters[3].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void NameUnnamedParameters_BindsPositionalValueToDeclaredName()
    {
        var request = ParseSinglePrepExec();

        // The sp_prepexec handler names value params (index 3+) from the decl.
        var bound = TdsSession.NameUnnamedParameters(request.Parameters, 3, ["@P1"]);

        HasCount(1, bound);
        AreEqual("@P1", bound[0].Name);
        AreEqual(5, Convert.ToInt32(bound[0].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void NameUnnamedParameters_MapsPositionally_KeepingNamedAndSurplusParameters()
    {
        var first = new TdsRpcParameter("", isOutput: false, System.Data.DbType.Int32, 9);
        var explicitName = new TdsRpcParameter("@given", isOutput: false, System.Data.DbType.Int32, 7);
        var surplus = new TdsRpcParameter("", isOutput: false, System.Data.DbType.Int32, 11);

        var bound = TdsSession.NameUnnamedParameters([first, explicitName, surplus], 0, ["@a", "@b"]);

        AreEqual("@a", bound[0].Name);      // positional: slot 0 → names[0]
        AreEqual("@given", bound[1].Name);  // already named → preserved (names[1] not applied)
        AreEqual("", bound[2].Name);        // slot 2 beyond the declared names → unchanged
    }

    /// <summary>
    /// Builds an ODBC-shaped sp_prepexec RPC message:
    /// params = [ handle (out int NULL) | '@P1 int' (decl) | 'SELECT @P1' (stmt) | 5 (unnamed value) ].
    /// </summary>
    private static TdsRpcRequest ParseSinglePrepExec()
    {
        var body = new List<byte>();
        AddUInt16(body, 0xFFFF);            // numeric ProcId form
        AddUInt16(body, Tds.ProcIdPrepExec);
        AddUInt16(body, 0);                 // option flags

        AddIntNParameter(body, name: "", isOutput: true, value: null);  // handle (out)
        AddNVarcharParameter(body, "@P1 int");                          // declaration
        AddNVarcharParameter(body, "SELECT @P1");                       // statement
        AddIntNParameter(body, name: "", isOutput: false, value: 5);    // unnamed value

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
