using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public static class AssemblyHooks
{
    /// <summary>
    /// First-use of <see cref="CSharpAnalyzerTest{TAnalyzer, TVerifier}"/> loads
    /// Roslyn assemblies and reference metadata into per-process caches. With
    /// method-level parallelism, multiple tests racing through that cold path
    /// contend on shared locks (~3x slowdown observed). Running one trivial
    /// analysis up front warms the caches so each test only does its unique work.
    /// </summary>
    [AssemblyInitialize]
    public static Task WarmUp(TestContext context) =>
        new CSharpAnalyzerTest<WrapperPropertyAnalyzer, DefaultVerifier>
        { TestCode = "internal sealed class C { }" }.RunAsync(context.CancellationToken);
}
