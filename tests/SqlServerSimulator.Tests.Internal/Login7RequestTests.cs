using System.Buffers.Binary;
using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A LOGIN7 message no driver sends is malformed traffic, refused as
/// <see cref="InvalidDataException"/> so the session closes the connection
/// as real's server does a login packet it finds structurally invalid.
/// </summary>
[TestClass]
public sealed class Login7RequestTests
{
    /// <summary>
    /// A feature extension block whose offset, or one of whose features'
    /// lengths, is a DWORD past <c>int</c>'s range.
    /// </summary>
    [TestMethod]
    [DataRow(0xFFFFFFFFu, 0x0Au, 1u)]
    [DataRow(98u, 0x0Au, 0xFFFFFFFFu)]
    [DataRow(98u, 0x0Au, 0x7FFFFFF0u)]
    public void AFeatureExtensionPastTheMessage_IsMalformed(uint blockOffset, uint featureId, uint featureLength)
    {
        var payload = new byte[94 + 4 + 5 + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);
        payload[27] = 0x10;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(56), 94);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(94), blockOffset);
        payload[98] = (byte)featureId;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(99), featureLength);
        payload[^1] = 0xFF;
        _ = ThrowsExactly<InvalidDataException>(() => Login7Request.Parse(payload));
    }
}
