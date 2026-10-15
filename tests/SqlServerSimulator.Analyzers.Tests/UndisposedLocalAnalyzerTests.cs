using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class UndisposedLocalAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<UndisposedLocalAnalyzer, DefaultVerifier>
        {
            TestCode = Preamble + source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        }.RunAsync(this.TestContext.CancellationToken);

    private const string Preamble = """
        using System;
        using System.IO;
        using System.Threading.Tasks;
        internal sealed class Resource : IDisposable
        {
            public int Value;
            public void Run() { }
            public Resource Self() => this;
            public void Dispose() { }
        }
        internal static class Factory
        {
            public static Resource Open() => new();
            public static void Consume(Resource resource) => resource.Dispose();
            public static void Release(this Resource resource) => resource.Dispose();
        }

        """;

    [TestMethod]
    public Task LocalUsedOnlyThroughMembers_Reports() =>
        RunAsync("""
            internal static class C
            {
                public static int M()
                {
                    var {|SSS018:resource|} = new Resource();
                    resource.Run();
                    return resource.Value;
                }
            }
            """);

    [TestMethod]
    public Task LocalNeverUsed_Reports() =>
        RunAsync("""
            internal static class C
            {
                public static void M()
                {
                    var {|SSS018:resource|} = new Resource();
                }
            }
            """);

    // A method's result may be borrowed rather than owned, so only 'new' counts.
    [TestMethod]
    public Task LocalFromMethod_DoesNotReport() =>
        RunAsync("""
            internal static class C
            {
                private static Resource shared = new();
                public static void M()
                {
                    var opened = Factory.Open();
                    opened.Run();
                    var borrowed = System.Threading.Volatile.Read(ref shared);
                    borrowed.Run();
                }
            }
            """);

    [TestMethod]
    public Task DiscardedCreation_Reports() =>
        RunAsync("""
            internal static class C
            {
                public static int M()
                {
                    {|SSS018:new Resource()|};
                    {|SSS018:new Resource()|}.Run();
                    return {|SSS018:new Resource()|}.Value;
                }
            }
            """);

    [TestMethod]
    public Task UsingForms_DoNotReport() =>
        RunAsync("""
            internal static class C
            {
                public static void M()
                {
                    using var first = new Resource();
                    first.Run();
                    using (var second = new Resource())
                        second.Run();
                }
            }
            """);

    [TestMethod]
    public Task DisposedOrHandedOn_DoNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                private Resource? kept;
                public void Disposed() { var r = new Resource(); r.Run(); r.Dispose(); }
                public Resource Returned() { var r = new Resource(); r.Run(); return r; }
                public void Stored() { var r = new Resource(); this.kept = r; }
                public void Passed() { var r = new Resource(); Factory.Consume(r); }
                public void Extension() { var r = new Resource(); r.Release(); }
                public void Conditional() { var r = new Resource(); r?.Dispose(); }
                public Action Captured() { var r = new Resource(); return () => r.Run(); }
                public Resource Fluent() => new Resource().Self();
                public object Boxed() => new Resource();
            }
            """);

    [TestMethod]
    public Task ExemptAndValueTypes_DoNotReport() =>
        RunAsync("""
            internal static class C
            {
                public static string M(byte[] bytes)
                {
                    var stream = new MemoryStream(bytes);
                    var writer = new StringWriter();
                    writer.Write(stream.Length);
                    var task = Task.Run(() => { });
                    task.Wait();
                    var list = new System.Collections.Generic.List<int>();
                    var enumerator = list.GetEnumerator();
                    _ = enumerator.MoveNext();
                    return writer.ToString();
                }
            }
            """);

    [TestMethod]
    public Task NonDisposable_DoesNotReport() =>
        RunAsync("""
            internal static class C
            {
                public static string M()
                {
                    var o = new object();
                    new object();
                    return o.ToString()!;
                }
            }
            """);
}
