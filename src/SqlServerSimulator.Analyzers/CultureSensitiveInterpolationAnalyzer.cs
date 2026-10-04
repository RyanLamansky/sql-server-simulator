using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags an interpolated-string hole the current culture formats: a
/// floating-point, <c>decimal</c>, date or time value, or an integer given a
/// format that draws a separator or symbol from the culture. A <c>$"…"</c> that becomes a
/// <c>string</c> formats every hole with <c>CultureInfo.CurrentCulture</c>, and
/// CA1305, which catches the same mistake in a <c>ToString()</c> or
/// <c>string.Format</c> call, doesn't look inside interpolations — so
/// <c>$"{date:dd MMM yyyy}"</c> printed <c>04 Okt. 2026</c> on a German host
/// and <c>$"{date:MM/dd/yy}"</c> took the culture's date separator, where the
/// simulated server answers the same on every machine.
/// </summary>
/// <remarks>
/// <para>
/// The fix is to build the string invariantly —
/// <c>string.Create(CultureInfo.InvariantCulture, $"…")</c> — or to format the
/// one hole with <c>.ToString(format, CultureInfo.InvariantCulture)</c>. An
/// interpolation that becomes a <c>FormattableString</c> or <c>IFormattable</c>,
/// or a handler given a format provider, is the caller's to format and isn't
/// flagged.
/// </para>
/// <para>
/// Integers are exempt unless their format draws a group or decimal
/// separator, a currency or a percent symbol from the culture (the
/// <c>C</c> / <c>E</c> / <c>F</c> / <c>N</c> / <c>P</c> specifiers, or a custom
/// format holding <c>,</c> <c>.</c> <c>%</c> or <c>‰</c>): plain, padded and
/// hexadecimal digits are the same in every culture, and only a negative
/// value's sign could differ. A <c>TimeSpan</c> is exempt unformatted or in
/// its constant <c>c</c> format. Strings, characters, booleans, enums and
/// <c>Guid</c>s never consult the culture.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureSensitiveInterpolationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS014",
        title: "Interpolation hole formatted with the current culture",
        messageFormat: "'{0}' formats a {1} with the current culture; build the string with string.Create(CultureInfo.InvariantCulture, $\"…\") or format the hole with ToString(…, CultureInfo.InvariantCulture)",
        category: "Globalization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An interpolated string that becomes a string formats each hole with CultureInfo.CurrentCulture, which CA1305 doesn't check: a floating-point, decimal, date or time value, or an integer formatted with separators, currency or percent, comes out with the host's decimal separator, month names, date and time separators or calendar. Build the string with string.Create(CultureInfo.InvariantCulture, $\"…\") or format the hole with ToString(format, CultureInfo.InvariantCulture). Exempt: integers whose format draws nothing from the culture, a TimeSpan unformatted or in its constant format, strings, characters, booleans, enums and Guids, and interpolations that become a FormattableString or IFormattable or go to a handler given a format provider.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInterpolatedString, SyntaxKind.InterpolatedStringExpression);
    }

    private static void AnalyzeInterpolatedString(SyntaxNodeAnalysisContext context)
    {
        var node = (InterpolatedStringExpressionSyntax)context.Node;
        var model = context.SemanticModel;
        if (!FormatsWithCurrentCulture(node, model, context.CancellationToken))
            return;

        foreach (var content in node.Contents)
        {
            if (content is not InterpolationSyntax hole)
                continue;
            var type = model.GetTypeInfo(hole.Expression, context.CancellationToken).Type;
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];
            if (type is null || CultureSensitiveKind(type, hole.FormatClause?.FormatStringToken.ValueText) is not { } kind)
                continue;
            context.ReportDiagnostic(Diagnostic.Create(Rule, hole.GetLocation(), hole.ToString(), kind));
        }
    }

    /// <summary>
    /// Whether the interpolation's holes are formatted with the current
    /// culture: it becomes a <c>string</c>, or a handler no format provider
    /// was handed.
    /// </summary>
    private static bool FormatsWithCurrentCulture(InterpolatedStringExpressionSyntax node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
    {
        // An explicit cast reports its conversion on the cast, not the operand.
        var converted = node.Parent is CastExpressionSyntax cast
            ? model.GetTypeInfo(cast, cancellationToken).Type
            : model.GetTypeInfo(node, cancellationToken).ConvertedType;
        if (converted is null)
            return false;
        if (converted.SpecialType == SpecialType.System_String)
            return true;
        if (!converted.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "InterpolatedStringHandlerAttribute"))
            return false;

        // A handler argument: formatted with whatever provider the call hands
        // it, so it's the current culture's only when no argument is one.
        if (node.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax arguments })
            return true;
        foreach (var argument in arguments.Arguments)
        {
            if (model.GetTypeInfo(argument.Expression, cancellationToken).Type is { } argumentType
                && (argumentType.Name == "IFormatProvider" || argumentType.AllInterfaces.Any(static i => i.Name == "IFormatProvider")))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// What a hole of <paramref name="type"/> formatted with
    /// <paramref name="format"/> takes from the culture, described for the
    /// message, or null when it takes nothing.
    /// </summary>
    private static string? CultureSensitiveKind(ITypeSymbol type, string? format)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_String:
            case SpecialType.System_Object:
                return null;
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                return DrawsFromCulture(format) ? "formatted integer" : null;
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return "number with a decimal separator";
            case SpecialType.System_DateTime:
                return "date";
        }

        if (type.TypeKind == TypeKind.Enum || type.ContainingNamespace is not { Name: "System", ContainingNamespace.IsGlobalNamespace: true } and not { Name: "Numerics" })
            return null;
        return type.Name switch
        {
            "DateOnly" or "DateTimeOffset" => "date",
            "Half" => "number with a decimal separator",
            "BigInteger" or "Int128" or "UInt128" => DrawsFromCulture(format) ? "formatted integer" : null,
            "TimeOnly" => "time",
            "TimeSpan" => format is null or "c" ? null : "time",
            _ => null,
        };
    }

    /// <summary>
    /// Whether an integer's <paramref name="format"/> takes a separator or
    /// symbol from the culture: a currency, exponent, fixed-point, number or
    /// percent specifier, or a custom format holding one of the characters
    /// that stand for them.
    /// </summary>
    private static bool DrawsFromCulture(string? format)
    {
        if (format is not { Length: > 0 })
            return false;
        // A standard format is one letter and an optional precision.
        if (char.IsLetter(format[0]) && format.Skip(1).All(char.IsDigit))
            return char.ToUpperInvariant(format[0]) is 'C' or 'E' or 'F' or 'N' or 'P';
        return format.IndexOfAny([',', '.', '%', '\u2030']) >= 0;
    }
}
