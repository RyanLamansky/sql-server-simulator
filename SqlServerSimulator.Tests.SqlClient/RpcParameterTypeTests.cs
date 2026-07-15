using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// One RPC-bound parameter per <see cref="SqlDbType"/> round-trips through a
/// parameterized <c>select @p</c>: SqlClient sends it as an sp_executesql RPC,
/// the simulator returns it, and the value comes back byte-for-byte. Every case
/// is checked against the same simulation's in-process ADO surface (the dual-read
/// oracle), so coercion (money scale, datetime tick rounding, CP1252 folding) is
/// never hardcoded.
/// </summary>
[TestClass]
public sealed class RpcParameterTypeTests
{
    public TestContext TestContext { get; set; } = null!;

    private async Task RoundTrip(Action<SqlParameter> configure)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        _ = await Wire.AssertScalarParamRoundTrips(simulation, connection, TestContext.CancellationToken, configure);
    }

    [TestMethod]
    public Task TinyInt() => RoundTrip(p => { p.SqlDbType = SqlDbType.TinyInt; p.Value = (byte)255; });

    [TestMethod]
    public Task SmallInt() => RoundTrip(p => { p.SqlDbType = SqlDbType.SmallInt; p.Value = (short)-32768; });

    [TestMethod]
    public Task Int() => RoundTrip(p => { p.SqlDbType = SqlDbType.Int; p.Value = 2147483647; });

    [TestMethod]
    public Task BigInt() => RoundTrip(p => { p.SqlDbType = SqlDbType.BigInt; p.Value = 9223372036854775807L; });

    [TestMethod]
    public Task Bit() => RoundTrip(p => { p.SqlDbType = SqlDbType.Bit; p.Value = true; });

    [TestMethod]
    public Task Real() => RoundTrip(p => { p.SqlDbType = SqlDbType.Real; p.Value = 1.5f; });

    [TestMethod]
    public Task Float() => RoundTrip(p => { p.SqlDbType = SqlDbType.Float; p.Value = 3.141592653589793d; });

    [TestMethod]
    public Task SmallMoney() => RoundTrip(p => { p.SqlDbType = SqlDbType.SmallMoney; p.Value = 214748.3647m; });

    [TestMethod]
    public Task Money() => RoundTrip(p => { p.SqlDbType = SqlDbType.Money; p.Value = 922337203685477.5807m; });

    [TestMethod]
    public Task Decimal_WithPrecisionAndScale() => RoundTrip(p =>
    {
        p.SqlDbType = SqlDbType.Decimal;
        p.Precision = 12;
        p.Scale = 3;
        p.Value = 12.345m;
    });

    [TestMethod]
    public Task UniqueIdentifier() => RoundTrip(p => { p.SqlDbType = SqlDbType.UniqueIdentifier; p.Value = new Guid("6F9619FF-8B86-D011-B42D-00C04FC964FF"); });

    [TestMethod]
    public Task Date_DateOnlyValue() => RoundTrip(p => { p.SqlDbType = SqlDbType.Date; p.Value = new DateOnly(2024, 2, 29); });

    [TestMethod]
    public Task Date_DateTimeValue() => RoundTrip(p => { p.SqlDbType = SqlDbType.Date; p.Value = new DateTime(2024, 2, 29); });

    [TestMethod]
    public Task Time_TimeSpanValue() => RoundTrip(p => { p.SqlDbType = SqlDbType.Time; p.Value = new TimeSpan(0, 13, 45, 30, 123); });

    [TestMethod]
    public Task DateTime2() => RoundTrip(p => { p.SqlDbType = SqlDbType.DateTime2; p.Value = new DateTime(2024, 2, 29, 13, 45, 30).AddTicks(1234567); });

    [TestMethod]
    public Task DateTimeOffset() => RoundTrip(p => { p.SqlDbType = SqlDbType.DateTimeOffset; p.Value = new DateTimeOffset(2024, 2, 29, 13, 45, 30, new TimeSpan(5, 30, 0)).AddTicks(1234567); });

    [TestMethod]
    public Task DateTime() => RoundTrip(p => { p.SqlDbType = SqlDbType.DateTime; p.Value = new DateTime(2024, 2, 29, 13, 45, 30, 123); });

    [TestMethod]
    public Task SmallDateTime() => RoundTrip(p => { p.SqlDbType = SqlDbType.SmallDateTime; p.Value = new DateTime(2024, 2, 29, 13, 45, 0); });

    [TestMethod]
    public Task Char_Cp1252() => RoundTrip(p => { p.SqlDbType = SqlDbType.Char; p.Size = 8; p.Value = "café"; });

    [TestMethod]
    public Task VarChar_Cp1252() => RoundTrip(p => { p.SqlDbType = SqlDbType.VarChar; p.Value = "café"; });

    [TestMethod]
    public Task NChar_Unicode() => RoundTrip(p => { p.SqlDbType = SqlDbType.NChar; p.Size = 4; p.Value = "アイウ€"; });

    [TestMethod]
    public Task NVarChar_Unicode() => RoundTrip(p => { p.SqlDbType = SqlDbType.NVarChar; p.Value = "アイウ€"; });

    // FAILING (product bug) — a string/binary RPC parameter's declared Size is
    // dropped in BatchContext.SeedVariables (declaredMaxLength: null), so a
    // varchar(max)/nvarchar(max)/varbinary(max) parameter is treated as a bounded
    // type. Values up to 65535 bytes round-trip by accident; past 0xFFFF the
    // non-PLP 16-bit length field overflows and the TDS stream desyncs, tearing
    // down the connection ("transport-level error ... Connection was terminated").
    [TestMethod]
    public Task VarCharMax_80k_ClientSendsPlp() => RoundTrip(p =>
    {
        p.SqlDbType = SqlDbType.VarChar;
        p.Size = -1;
        p.Value = string.Concat(Enumerable.Range(0, 10000).Select(i => $"abcdef{i % 10}x")); // 80,000 chars
    });

    [TestMethod]
    public Task NVarCharMax_Large_ClientSendsPlp() => RoundTrip(p =>
    {
        p.SqlDbType = SqlDbType.NVarChar;
        p.Size = -1;
        p.Value = string.Concat(Enumerable.Range(0, 20000).Select(i => $"アイウ{i:D4}")); // 140,000 chars
    });

    [TestMethod]
    public Task Binary() => RoundTrip(p => { p.SqlDbType = SqlDbType.Binary; p.Size = 8; p.Value = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }; });

    [TestMethod]
    public Task VarBinary() => RoundTrip(p => { p.SqlDbType = SqlDbType.VarBinary; p.Value = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }; });

    [TestMethod]
    public Task VarBinaryMax_150k_ClientSendsPlp() => RoundTrip(p =>
    {
        var value = new byte[150000];
        for (var i = 0; i < value.Length; i++)
            value[i] = (byte)((i * 31) + 7);
        p.SqlDbType = SqlDbType.VarBinary;
        p.Size = -1;
        p.Value = value;
    });

    // FAILING — documents a product gap. An xml RPC parameter decodes to
    // DbType.Xml, which BatchContext.SeedVariables / SqlType.GetByDbType has no
    // mapping for, so the wire surfaces Msg 50000 "No SqlType mapping for DbType
    // Xml" instead of round-tripping. Real SQL Server accepts SqlDbType.Xml
    // parameters. Kept as a round-trip assertion so the gap stays visible; the
    // in-process oracle can't be used here because it hits the same NotSupported.
    [TestMethod]
    public async Task Xml_Parameter_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select @p", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Xml) { Value = "<root><a>1</a></root>" });
        AreEqual("<root><a>1</a></root>", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Null_TypedParameters_RoundTripAsDbNull()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var typedNulls = new Action<SqlParameter>[]
        {
            p => p.SqlDbType = SqlDbType.Int,
            p => p.SqlDbType = SqlDbType.NVarChar,
            p => { p.SqlDbType = SqlDbType.Decimal; p.Precision = 10; p.Scale = 2; },
            p => p.SqlDbType = SqlDbType.DateTime2,
            p => p.SqlDbType = SqlDbType.UniqueIdentifier,
            p => p.SqlDbType = SqlDbType.VarBinary,
            p => p.SqlDbType = SqlDbType.Money,
        };

        foreach (var typing in typedNulls)
        {
            var wireValue = await Wire.AssertScalarParamRoundTrips(simulation, connection, TestContext.CancellationToken, p =>
            {
                typing(p);
                p.Value = DBNull.Value;
            });
            _ = IsInstanceOfType<DBNull>(wireValue);
        }
    }

    // A statement > nvarchar(4000) forces SqlClient to send the sp_executesql
    // @statement parameter as ntext (0x63) with the legacy 4-byte-length value
    // form (LONGLEN max + collation + LONGLEN data + UTF-16 bytes) — NOT PLP.
    // SMO's Object-Explorer user-database enumeration is exactly such a large
    // parameterized query; rejecting ntext RPC params meant it never executed,
    // so the Databases node stayed empty. Spans multiple TDS packets at the
    // 8000-byte default. Probe-confirmed wire shape (2026-07-15).
    [TestMethod]
    public async Task LargeStatement_SentAsNtextRpcParam_Executes()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        // ~12 KB of statement text → over nvarchar(4000) and multi-packet.
        var pad = "/* " + new string('x', 6000) + " */";
        await using var command = new SqlCommand($"{pad} SELECT @p AS v", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Int) { Value = 42 });
        AreEqual(42, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // The @statement itself carrying > 4000 chars of real SQL (not padding) —
    // ntext value must decode to the exact query the server then runs. The
    // arithmetic chain stays shallow (200 terms) because expression parsing
    // recurses per operator and default 1 MB thread stacks overflow near 700
    // levels; a positionally-distinctive 3000-char literal carries the bulk
    // of the statement past the nvarchar(4000) ntext threshold instead, and
    // its char-exact round-trip is the decode-exactness evidence.
    [TestMethod]
    public async Task LargeStatement_NtextValue_DecodesExactly()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var marker = string.Concat(Enumerable.Range(0, 500).Select(i => $"m{i:D4}"));
        var terms = string.Join(" + ", Enumerable.Repeat("1", 200));
        await using var command = new SqlCommand($"SELECT ({terms}) AS total, N'{marker}' AS marker, @p AS p", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.NVarChar, 20) { Value = "ok" });
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(200, reader.GetInt32(0));
        AreEqual(marker, reader.GetString(1));
        AreEqual("ok", reader.GetString(2));
    }
}
