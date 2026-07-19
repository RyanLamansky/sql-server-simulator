using SqlServerSimulator.Network;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Pins the <see cref="TdsTokenWriter.AtTokenBoundary"/> invariant the terminal
/// crash boundary relies on: the flag is true whenever the buffer ends at a
/// complete-token boundary and false only while a composite token
/// (COLMETADATA / ROW / RETURNVALUE — the writers that interleave a
/// throw-capable per-column sub-write) is mid-write. A crash that struck
/// mid-composite therefore leaves the flag false, and the backstop declines to
/// append an ERROR token that would desync the stream.
/// </summary>
[TestClass]
public sealed class TokenBoundaryTests
{
    private static TdsTokenWriter NewWriter() => new(new TdsPacketTransport(new MemoryStream()));

    [TestMethod]
    public void FreshWriter_IsAtTokenBoundary()
    {
        IsTrue(NewWriter().AtTokenBoundary);
    }

    [TestMethod]
    public void SelfContainedToken_LeavesBoundaryTrue()
    {
        var writer = NewWriter();
        writer.WriteDone(Tds.DoneFinal, 0);
        IsTrue(writer.AtTokenBoundary);
    }

    [TestMethod]
    public void Composite_IsNotAtBoundary_WhileOpen_TrueWhenClosed()
    {
        var writer = NewWriter();
        writer.EnterComposite();
        IsFalse(writer.AtTokenBoundary);
        writer.LeaveComposite();
        IsTrue(writer.AtTokenBoundary);
    }

    [TestMethod]
    public void WriteColMetadata_LeavesBoundaryTrue()
    {
        var writer = NewWriter();
        TdsTypeCodec.WriteColMetadata(writer, [SqlType.Int32], ["a"], null);
        IsTrue(writer.AtTokenBoundary);
    }

    [TestMethod]
    public void CompositeAbandonedMidWrite_LeavesBoundaryFalse()
    {
        // Simulates a throw between the token's first byte and its completion —
        // WriteColMetadata's EnterComposite ran but a per-column WriteTypeInfo
        // would have thrown before LeaveComposite. The flag stays false, so the
        // backstop must NOT append another token here.
        var writer = NewWriter();
        writer.EnterComposite();
        writer.WriteByte(Tds.TokenColMetadata);
        IsFalse(writer.AtTokenBoundary);
    }
}
