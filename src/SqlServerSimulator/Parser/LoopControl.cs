namespace SqlServerSimulator.Parser;

/// <summary>
/// In-flight loop-flow signal for the dispatch loop. Set by the
/// <c>BREAK</c> / <c>CONTINUE</c> dispatch sites, consumed and reset by the
/// innermost <c>WHILE</c>. Lives on <see cref="BatchContext.LoopControl"/>;
/// the <see cref="BatchContext.IsSkipping"/> property OR's it into the
/// statement-level skip predicate so subsequent statements in the body
/// naturally no-op until the enclosing WHILE picks the flag back up.
/// </summary>
/// <remarks>
/// Flag-based rather than exception-based: exceptions for normal control
/// flow compose poorly with iterator-based dispatch (yield-in-try semantics
/// + state-machine wakeup ordering), and the flag plumbing reuses the same
/// skip-mode gates already in place for un-taken IF branches.
/// </remarks>
internal enum LoopControl
{
    /// <summary>No loop signal in flight (default).</summary>
    None,
    /// <summary>BREAK fired in the current loop body. Innermost WHILE exits.</summary>
    Break,
    /// <summary>CONTINUE fired in the current loop body. Innermost WHILE re-iterates.</summary>
    Continue,
}
