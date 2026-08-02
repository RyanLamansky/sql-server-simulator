using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class NonPublicRecordAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    // Records need a reference set carrying IsExternalInit, which the testing
    // library's default (a pre-.NET 5 framework) doesn't have.
    private Task RunAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<NonPublicRecordAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync(this.TestContext.CancellationToken);
    }

    [TestMethod]
    public Task InternalRecordStruct_Reports() =>
        RunAsync("""
            internal readonly record struct {|SSS009:S|}(int Value);
            """);

    [TestMethod]
    public Task InternalRecordClass_Reports() =>
        RunAsync("""
            internal sealed record {|SSS009:C|}(int Value);
            """);

    [TestMethod]
    public Task PrivateNestedRecord_Reports() =>
        RunAsync("""
            internal sealed class Holder
            {
                private readonly record struct {|SSS009:Entry|}(int Value);
            }
            """);

    // The record shape is the whole subject, so a record carrying no positional
    // parameters is flagged just the same — it still synthesizes equality,
    // copying and printing members.
    [TestMethod]
    public Task ParameterlessInternalRecord_Reports() =>
        RunAsync("""
            internal sealed record {|SSS009:C|}
            {
                public readonly int Value;
                public C(int value) => this.Value = value;
            }
            """);

    [TestMethod]
    public Task PublicRecord_DoesNotReport() =>
        RunAsync("""
            public sealed record C(int Value);
            """);

    [TestMethod]
    public Task PublicRecordStruct_DoesNotReport() =>
        RunAsync("""
            public readonly record struct S(int Value);
            """);

    // A public type nested in an internal one is not reachable from outside the
    // assembly, so it has no API surface to protect either.
    [TestMethod]
    public Task PublicRecordNestedInInternalType_Reports() =>
        RunAsync("""
            internal sealed class Holder
            {
                public readonly record struct {|SSS009:Entry|}(int Value);
            }
            """);

    [TestMethod]
    public Task PlainInternalClass_DoesNotReport() =>
        RunAsync("""
            internal sealed class C
            {
                public readonly int Value;
                public C(int value) => this.Value = value;
            }
            """);

    [TestMethod]
    public Task PlainInternalStruct_DoesNotReport() =>
        RunAsync("""
            internal readonly struct S
            {
                public readonly int Value;
                public S(int value) => this.Value = value;
            }
            """);

    // The three replacement shapes the message names. A readonly record struct
    // keeps its readonly modifier; a record class becomes a plain class.
    [TestMethod]
    public Task ReadonlyRecordStruct_NamesReadonlyStructReplacement() =>
        RunAsync(
            """
            internal readonly record struct {|#0:S|}(int Value);
            """,
            new DiagnosticResult("SSS009", DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("S", "readonly struct"));

    [TestMethod]
    public Task MutableRecordStruct_NamesStructReplacement() =>
        RunAsync(
            """
            internal record struct {|#0:S|}(int Value);
            """,
            new DiagnosticResult("SSS009", DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("S", "struct"));

    [TestMethod]
    public Task RecordClass_NamesClassReplacement() =>
        RunAsync(
            """
            internal sealed record class {|#0:C|}(int Value);
            """,
            new DiagnosticResult("SSS009", DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("C", "class"));

    // A bare `record` with no class/struct keyword is a record class.
    [TestMethod]
    public Task BareRecord_NamesClassReplacement() =>
        RunAsync(
            """
            internal record {|#0:C|}(int Value);
            """,
            new DiagnosticResult("SSS009", DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("C", "class"));
}
