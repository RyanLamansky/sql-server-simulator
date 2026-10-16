using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags the BCL numeric operations whose results differ between .NET 10 and
/// .NET 11: a <see cref="double"/> or <see cref="float"/> converted to
/// <see cref="decimal"/> (a cast, <c>Convert.ToDecimal</c>, the
/// <c>decimal</c> constructor, <c>decimal.Create*</c>), which .NET 10 rounds to
/// 15 or 7 significant digits and .NET 11 converts exactly; rounding to a
/// number of fractional digits (<c>Math.Round(double, int)</c> and its
/// siblings), which .NET 11 computes without .NET 10's scale-round-unscale
/// steps; and <c>RadiansToDegrees</c> / <c>DegreesToRadians</c>, whose last
/// place moved. The engine's answers must not depend on the runtime it runs
/// on, so each goes through the engine's own spelling of the .NET 10 result.
/// </summary>
/// <remarks>
/// <c>BinaryFloatToDecimal</c>, <c>DoubleDigitRounding</c> and
/// <c>SpatialEllipsoid.MultiplyDivideDegrees</c> / <c>MultiplyDivideRadians</c>
/// are the replacements. A site where the runtime's own answer is the point
/// takes <c>#pragma warning disable SSS019</c> with a one-line rationale.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RuntimeDependentNumericsAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS019",
        title: "Numeric operation whose result depends on the .NET runtime",
        messageFormat: "'{0}' computes differently on .NET 10 and .NET 11; use {1}",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A double or float converted to decimal rounds to 15 or 7 significant digits on .NET 10 and converts exactly on .NET 11; Math.Round(double, int) and its siblings, and double.RadiansToDegrees / DegreesToRadians, round differently in the last place. The engine's results must be the same on every runtime, so these go through BinaryFloatToDecimal, DoubleDigitRounding and SpatialEllipsoid.MultiplyDivideDegrees / MultiplyDivideRadians, which spell out the .NET 10 computation. A site that means the runtime's own answer suppresses with #pragma warning disable SSS019 and a rationale.");

    private const string ToDecimal = "BinaryFloatToDecimal.FromDouble / FromSingle";
    private const string ToDigits = "DoubleDigitRounding.Round";
    private const string Angles = "SpatialEllipsoid.MultiplyDivideDegrees / MultiplyDivideRadians";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
    }

    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.Type?.SpecialType == SpecialType.System_Decimal && IsBinaryFloat(conversion.Operand.Type))
            Report(context, $"({conversion.Operand.Type!.ToDisplayString()} → decimal)", ToDecimal);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        var type = method.ContainingType;
        var parameters = method.Parameters;
        switch (method.Name)
        {
            case "CreateChecked" or "CreateSaturating" or "CreateTruncating"
                when type.SpecialType == SpecialType.System_Decimal && method.TypeArguments.Length == 1 && IsBinaryFloat(method.TypeArguments[0]):
            case "ToDecimal"
                when IsSystemType(type, "Convert") && parameters.Length >= 1 && IsBinaryFloat(parameters[0].Type):
                Report(context, $"{type.Name}.{method.Name}", ToDecimal);
                break;
            case "DegreesToRadians" or "RadiansToDegrees" when IsBinaryFloat(type):
                Report(context, $"{type.Name}.{method.Name}", Angles);
                break;
            case "Round"
                when (IsSystemType(type, "Math") || IsSystemType(type, "MathF") || IsBinaryFloat(type))
                    && parameters.Length >= 2 && parameters[1].Type.SpecialType == SpecialType.System_Int32 && IsBinaryFloat(parameters[0].Type):
                Report(context, $"{type.Name}.Round({parameters[0].Type.ToDisplayString()}, int)", ToDigits);
                break;
        }
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context)
    {
        var constructor = ((IObjectCreationOperation)context.Operation).Constructor;
        if (constructor is { ContainingType.SpecialType: SpecialType.System_Decimal, Parameters.Length: 1 } && IsBinaryFloat(constructor.Parameters[0].Type))
            Report(context, $"new decimal({constructor.Parameters[0].Type.ToDisplayString()})", ToDecimal);
    }

    private static void Report(OperationAnalysisContext context, string operation, string replacement) =>
        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), operation, replacement));

    private static bool IsBinaryFloat(ITypeSymbol? type) =>
        type?.SpecialType is SpecialType.System_Double or SpecialType.System_Single;

    private static bool IsSystemType(INamedTypeSymbol type, string name) =>
        type.Name == name && type.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true };
}
