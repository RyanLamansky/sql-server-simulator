using SqlServerSimulator.Parser.Expressions;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The vector kernels' software fused multiply-add, which a CPU without the
/// instruction takes, must round exactly as the instruction does, or the
/// kernels' last-bit match with real breaks there. Checked bit for bit against
/// the hardware instruction on a CPU that has one.
/// </summary>
[TestClass]
public sealed class FusedMultiplyAddTests
{
    [TestMethod]
    public void Software_MatchesTheInstructionBitForBit()
    {
        if (!System.Runtime.Intrinsics.X86.Fma.IsSupported && !System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported)
            Inconclusive("No fused multiply-add instruction to compare against.");

        var random = new Random(20261003);
        for (var i = 0; i < 2_000_000; i++)
        {
            var x = Next(random);
            var y = Next(random);
            // Every fourth case cancels the product almost exactly, where a
            // separately rounded product shows most.
            var z = i % 4 == 0 ? -(x * y) * (1 + ((random.NextSingle() - 0.5f) * 1e-6f)) : Next(random);
            var expected = MathF.FusedMultiplyAdd(x, y, z);
            var actual = VectorArguments.SoftwareFusedMultiplyAdd(x, y, z);
            if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
                Fail($"fma({x:R}, {y:R}, {z:R}): expected {expected:R}, got {actual:R}");
        }
    }

    [TestMethod]
    [DataRow(float.MaxValue, 2f, 0f)]
    [DataRow(float.PositiveInfinity, 0f, 1f)]
    [DataRow(float.NaN, 1f, 1f)]
    [DataRow(1e-30f, 1e-30f, 0f)]
    [DataRow(1e-20f, 1e-20f, -1e-40f)]
    [DataRow(-0f, 1f, 0f)]
    [DataRow(1f, 1f, -1f)]
    public void Software_EdgeValues(float x, float y, float z)
    {
        var expected = MathF.FusedMultiplyAdd(x, y, z);
        var actual = VectorArguments.SoftwareFusedMultiplyAdd(x, y, z);
        // A NaN's sign and payload differ by implementation (x86's default
        // NaN is negative, the Windows C runtime's fmaf positive) and carry
        // no meaning, so only NaN-ness is compared.
        if (float.IsNaN(expected))
            IsTrue(float.IsNaN(actual));
        else
            AreEqual(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
    }

    private static float Next(Random random)
    {
        var magnitude = MathF.Pow(2, random.Next(-60, 60));
        return (random.NextSingle() - 0.5f) * 2 * magnitude;
    }
}
