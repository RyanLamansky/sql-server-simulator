using System.Runtime.CompilerServices;

namespace SqlServerSimulator;

/// <summary>
/// The JIT tiering choice for the bookkeeping every statement or row passes
/// through — lock grants and releases, the seek cache, I/O statistics, the
/// name memo — and for the parser frame every nested query holds, written as
/// <c>[MethodImpl(Tiering.OptimizeFirstCall)]</c>.
/// </summary>
internal static class Tiering
{
    /// <summary>
    /// Compiles the method optimized on its first call, skipping the tiers the
    /// runtime otherwise walks it through: unoptimized code first, then an
    /// instrumented copy, then the optimized one, both recompiles waiting on a
    /// background queue that is held back while anything new is being
    /// compiled. In a test process the consumer keeps that queue busy — EF
    /// Core compiles methods for every query it shapes — so the simulator's
    /// hottest methods spent most of a test class unoptimized or instrumented.
    /// Reserved for methods that dispatch nothing virtually and call no
    /// delegate in their hot path: what they give up is the profile-guided
    /// recompile, whose main gain is devirtualizing exactly those calls.
    /// </summary>
    /// <remarks>
    /// The parser's query-block method takes it for its stack instead: while
    /// unoptimized it gives every local and temporary a slot of its own, a
    /// frame several times the optimized one's, and each level of nested
    /// queries holds that frame, so a cold process met Msg 8631 at a fraction
    /// of the nesting real accepts. Its optimized compile lands ahead of the
    /// first query rather than in the background, which a process that runs
    /// on would have paid for anyway.
    /// </remarks>
    internal const MethodImplOptions OptimizeFirstCall = MethodImplOptions.AggressiveOptimization;
}
