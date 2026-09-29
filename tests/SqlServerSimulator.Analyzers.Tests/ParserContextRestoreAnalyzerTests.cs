using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace SqlServerSimulator.Analyzers;

[TestClass]
public sealed class ParserContextRestoreAnalyzerTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string ParserContextDeclaration = """
        namespace SqlServerSimulator.Parser
        {
            internal sealed class ParserContext
            {
                public bool InOverBody;
                public bool InTopCount;
                public int CaseDepth;
                public object? OuterTypeResolver;
                public object? ScopeSources;
            }
        }
        """;

    private Task RunAsync(string source) =>
        new CSharpAnalyzerTest<ParserContextRestoreAnalyzer, DefaultVerifier>
        { TestState = { Sources = { source, ParserContextDeclaration } } }.RunAsync(this.TestContext.CancellationToken);

    [TestMethod]
    public Task SavedFieldRestoredInFinally_Reports() =>
        RunAsync("""
            using SqlServerSimulator.Parser;
            internal static class Holder
            {
                public static void Parse(ParserContext context)
                {
                    var saved = context.InOverBody;
                    context.InOverBody = true;
                    try
                    {
                    }
                    finally
                    {
                        {|SSS013:context.InOverBody = saved|};
                    }
                }
            }
            """);

    // Each field of a multi-field save list reports on its own restore.
    [TestMethod]
    public Task EachRestoredFieldReports() =>
        RunAsync("""
            using SqlServerSimulator.Parser;
            internal static class Holder
            {
                public static void Parse(ParserContext context, object resolver)
                {
                    var saved = context.OuterTypeResolver;
                    var savedSources = context.ScopeSources;
                    context.OuterTypeResolver = resolver;
                    try
                    {
                    }
                    finally
                    {
                        {|SSS013:context.OuterTypeResolver = saved|};
                        {|SSS013:context.ScopeSources = savedSources|};
                    }
                }
            }
            """);

    // A reset to a constant restores something other than the entry value.
    [TestMethod]
    public Task ConstantResetInFinally_DoesNotReport() =>
        RunAsync("""
            using SqlServerSimulator.Parser;
            internal static class Holder
            {
                public static void Parse(ParserContext context)
                {
                    context.InTopCount = true;
                    try
                    {
                    }
                    finally
                    {
                        context.InTopCount = false;
                        context.CaseDepth--;
                    }
                }
            }
            """);

    [TestMethod]
    public Task RestoreOutsideFinally_DoesNotReport() =>
        RunAsync("""
            using SqlServerSimulator.Parser;
            internal static class Holder
            {
                public static void Parse(ParserContext context, object resolver)
                {
                    var saved = context.OuterTypeResolver;
                    context.OuterTypeResolver = resolver;
                    context.OuterTypeResolver = saved;
                }
            }
            """);

    // A local read from another field is not that field's saved value.
    [TestMethod]
    public Task LocalFromDifferentField_DoesNotReport() =>
        RunAsync("""
            using SqlServerSimulator.Parser;
            internal static class Holder
            {
                public static void Parse(ParserContext context)
                {
                    var sources = context.ScopeSources;
                    try
                    {
                    }
                    finally
                    {
                        context.OuterTypeResolver = sources;
                    }
                }
            }
            """);

    [TestMethod]
    public Task OtherTypesField_DoesNotReport() =>
        RunAsync("""
            internal sealed class Connection
            {
                public bool QuotedIdentifiers;
            }
            internal static class Holder
            {
                public static void Run(Connection connection)
                {
                    var saved = connection.QuotedIdentifiers;
                    try
                    {
                    }
                    finally
                    {
                        connection.QuotedIdentifiers = saved;
                    }
                }
            }
            """);
}
