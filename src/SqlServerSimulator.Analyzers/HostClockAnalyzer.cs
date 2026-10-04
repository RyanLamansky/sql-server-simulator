using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a read of the host machine's local clock or time zone:
/// <c>DateTime.Now</c>, <c>DateTime.Today</c>, <c>DateTimeOffset.Now</c>,
/// <c>TimeZoneInfo.Local</c> and the <c>ToLocalTime()</c> conversions. The
/// simulated server's clock is UTC — <c>GETDATE()</c> and its siblings read
/// the statement's frozen UTC instant — so a value taken from the host's local
/// time disagrees with them on any machine not set to UTC, and the test suites,
/// run on UTC machines, can't see it.
/// </summary>
/// <remarks>
/// A timestamp belongs to the statement that takes it
/// (<c>batch.CurrentStatement.UtcNow</c>), the transaction
/// (<c>batch.SystemTimeUtc</c>), or failing either <c>DateTime.UtcNow</c>.
/// A site that genuinely means the host's own clock takes
/// <c>#pragma warning disable SSS015</c> with a one-line rationale.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HostClockAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS015",
        title: "Host local clock or time zone read",
        messageFormat: "'{0}' reads the host's local time, where the simulated server's clock is UTC; use the statement's UtcNow, the transaction's SystemTimeUtc or DateTime.UtcNow",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The simulated server keeps UTC as its local time: GETDATE(), SYSDATETIME() and every catalog timestamp read the statement's frozen UTC instant. DateTime.Now, DateTime.Today, DateTimeOffset.Now, TimeZoneInfo.Local and ToLocalTime() read the host machine's time zone instead, so a value built from them disagrees with GETDATE() on any host not set to UTC, which the UTC test machines never show. Take the statement's batch.CurrentStatement.UtcNow, the transaction's batch.SystemTimeUtc, or DateTime.UtcNow; a site that means the host's own clock suppresses with #pragma warning disable SSS015 and a rationale.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var property = ((IPropertyReferenceOperation)context.Operation).Property;
        var flagged = property.Name switch
        {
            "Local" => IsSystemType(property.ContainingType, "TimeZoneInfo"),
            "Now" => IsSystemType(property.ContainingType, "DateTime") || IsSystemType(property.ContainingType, "DateTimeOffset"),
            "Today" => IsSystemType(property.ContainingType, "DateTime"),
            _ => false,
        };
        if (flagged)
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), $"{property.ContainingType.Name}.{property.Name}"));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (method.Name == "ToLocalTime" && (IsSystemType(method.ContainingType, "DateTime") || IsSystemType(method.ContainingType, "DateTimeOffset")))
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), $"{method.ContainingType.Name}.ToLocalTime()"));
    }

    private static bool IsSystemType(INamedTypeSymbol type, string name) =>
        type.Name == name && type.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true };
}
