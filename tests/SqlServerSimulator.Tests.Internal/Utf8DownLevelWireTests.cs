using System.Buffers.Binary;
using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a client that negotiated no UTF-8 support receives — the fixture's
/// session logs in no client, so it acknowledged no feature: a UTF-8
/// <c>varchar</c> or <c>char</c> as the <c>nvarchar</c> of its width in the
/// same collation (<c>nvarchar(max)</c> past 4000), and a <c>json</c> or
/// <c>vector</c> as <c>nvarchar(max)</c> in the database's collation, each value
/// as UTF-16 (captured 2026-10-18 against SQL Server 2025 with a raw TDS client
/// sending no feature extension).
/// </summary>
[TestClass]
public sealed class Utf8DownLevelWireTests
{
    /// <summary>
    /// The first column of the response's COLMETADATA as its type byte, its
    /// two-byte maximum length, its collation bytes and its user type, then the
    /// first ROW's value bytes for that one-column result.
    /// </summary>
    private static (string Described, byte[] Value) FirstColumn(byte[] response)
    {
        var i = Array.IndexOf(response, (byte)0x81);
        IsGreaterThanOrEqualTo(0, i);
        AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i + 1)));
        var userType = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(i + 3));
        var type = response[i + 9];
        var maxLength = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i + 10));
        var collation = Convert.ToHexString(response, i + 12, 5);
        i += 17;
        i += 1 + (response[i] * 2);
        AreEqual(0xD1, response[i]);
        i++;
        byte[] value;
        if (maxLength == 0xFFFF)
        {
            // PLP: the total length, one chunk, the terminator.
            var chunk = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(i + 8));
            value = response.AsSpan(i + 12, chunk).ToArray();
        }
        else
        {
            value = response.AsSpan(i + 2, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(i))).ToArray();
        }
        return (string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{type:X2} {maxLength} {collation} ut={userType}"), value);
    }

    [TestMethod]
    [DataRow("select cast(N'aé€' as varchar(10)) collate Latin1_General_100_CI_AS_SC_UTF8 as v", "E7 20 0904D02400 ut=0", "aé€")]
    [DataRow("select cast(N'xé' as char(4)) collate Latin1_General_100_CI_AS_SC_UTF8 as v", "E7 8 0904D02400 ut=0", "xé ")]
    [DataRow("select cast(N'aé' as varchar(5000)) collate Latin1_General_100_CI_AS_SC_UTF8 as v", "E7 65535 0904D02400 ut=0", "aé")]
    [DataRow("select cast(N'aé' as varchar(10)) collate Latin1_General_100_BIN2_UTF8 as v", "E7 20 0904002600 ut=0", "aé")]
    [DataRow("select cast('{\"a\":1}' as json) as v", "E7 65535 0904D00034 ut=0", "{\"a\":1}")]
    [DataRow("select cast('[1]' as vector(1)) as v", "E7 65535 0904D00034 ut=255", "[1.0000000e+000]")]
    [DataRow("select cast('lé' as varchar(10)) as v", "A7 10 0904D00034 ut=0", null)]
    public void Utf8Strings_TravelAsNvarchar(string sql, string described, string? text)
    {
        using var fixture = new TdsSessionFixture();
        var (actual, value) = FirstColumn(fixture.RunBatch(sql));
        AreEqual(described, actual);
        if (text is not null)
            AreEqual(text, Encoding.Unicode.GetString(value));
    }

    [TestMethod]
    public void DownLevelJson_TakesTheDatabasesCollation()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("alter database simulated collate Latin1_General_CS_AS");
        AreEqual("E7 65535 0904C00000 ut=0", FirstColumn(fixture.RunBatch("select cast('{}' as json) as v")).Described);
    }
}
