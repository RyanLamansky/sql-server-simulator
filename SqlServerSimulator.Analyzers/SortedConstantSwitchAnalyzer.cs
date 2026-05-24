using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a <c>switch</c> expression or statement whose orderable arms are
/// single-value compile-time constants (a "low-complexity" dispatch) but are
/// not listed in sorted order. String arms sort by ordinal value, numeric
/// (integer / decimal / floating) arms numerically. A switch is left alone
/// the moment any arm carries a guard (<c>when</c>), an
/// or/relational/recursive/declaration/<c>var</c> pattern, an <c>enum</c> /
/// <c>char</c> / <c>bool</c> constant, or any other non-constant label —
/// those either aren't "single-value constant" branches or are routinely
/// ordered by meaning (type rank, operator grouping) rather than value, so
/// no value-based canonical order applies.
/// </summary>
/// <remarks>
/// Keyword-dispatch tables (see <c>Parser/Expression.cs:ResolveBuiltIn</c>,
/// whose outer length switch nests per-length string switches) grow by
/// accretion: a contributor appends a new case wherever the cursor happens
/// to be, and the table drifts out of order. A sorted table is the only one
/// where "is X already handled?" is an O(log n) eyeball scan instead of a
/// full read, and where a merge conflict over two independently-added cases
/// resolves positionally. The default (<c>_</c>) and <c>null</c> arms are
/// sentinels — they're excluded from the ordering check, never reordered.
/// The rule reports each adjacent inversion so a single build pass surfaces
/// every out-of-place case at once.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SortedConstantSwitchAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS005",
        title: "Constant switch arms should be sorted",
        messageFormat: "Switch arm '{0}' is out of order — sort the arms {1}, or suppress SSS005 with a rationale if they are deliberately ordered by meaning",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A switch (expression or statement) whose arms are single string or numeric compile-time constants has one canonical order: strings by ordinal value, numeric constants numerically. Listing the arms in that order keeps coverage auditable by scan and makes independently-added cases conflict positionally. Switches with any guard, or/relational/recursive/var pattern, enum/char/bool constant, or non-constant label are exempt — they aren't low-complexity value-ordered dispatch. The rule cannot read intent, so a switch whose string/numeric arms are deliberately ordered by meaning (time-unit magnitude, host level, …) is a legitimate '#pragma warning disable SSS005' site — add a one-line rationale rather than sorting.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeSwitchExpression, SyntaxKind.SwitchExpression);
        context.RegisterSyntaxNodeAction(AnalyzeSwitchStatement, SyntaxKind.SwitchStatement);
    }

    private static void AnalyzeSwitchExpression(SyntaxNodeAnalysisContext context)
    {
        var switchExpression = (SwitchExpressionSyntax)context.Node;

        var arms = new List<Arm>(switchExpression.Arms.Count);
        foreach (var arm in switchExpression.Arms)
        {
            // A guard makes the arm value-dependent, not a single constant.
            if (arm.WhenClause is not null)
                return;

            var key = ClassifyPattern(arm.Pattern, context.SemanticModel, context.CancellationToken);
            if (key.Kind == ArmKind.Complex)
                return;
            if (key.Kind == ArmKind.Sentinel)
                continue;

            arms.Add(new Arm(key, arm.Pattern.GetLocation(), arm.Pattern.ToString()));
        }

        ReportInversions(context, arms);
    }

    private static void AnalyzeSwitchStatement(SyntaxNodeAnalysisContext context)
    {
        var switchStatement = (SwitchStatementSyntax)context.Node;

        var arms = new List<Arm>();
        foreach (var section in switchStatement.Sections)
        {
            foreach (var label in section.Labels)
            {
                ArmKey key;
                switch (label)
                {
                    case DefaultSwitchLabelSyntax:
                        continue;
                    case CaseSwitchLabelSyntax value:
                        key = ClassifyExpression(value.Value, context.SemanticModel, context.CancellationToken);
                        break;
                    case CasePatternSwitchLabelSyntax pattern when pattern.WhenClause is null:
                        key = ClassifyPattern(pattern.Pattern, context.SemanticModel, context.CancellationToken);
                        break;
                    default:
                        return;
                }

                if (key.Kind == ArmKind.Complex)
                    return;
                if (key.Kind == ArmKind.Sentinel)
                    continue;

                arms.Add(new Arm(key, label.GetLocation(), label.ToString()));
            }
        }

        ReportInversions(context, arms);
    }

    /// <summary>
    /// Reports every arm whose sort key is less than its predecessor's. Bails
    /// when fewer than two ordered arms exist (nothing to sort) or when the
    /// arms aren't a single homogeneous kind (no cross-kind order is defined).
    /// </summary>
    private static void ReportInversions(SyntaxNodeAnalysisContext context, List<Arm> arms)
    {
        if (arms.Count < 2)
            return;

        var kind = arms[0].Key.Kind;
        for (var i = 1; i < arms.Count; i++)
        {
            if (arms[i].Key.Kind != kind)
                return;
        }

        var orderDescription = kind == ArmKind.String ? "alphabetically" : "numerically";

        for (var i = 1; i < arms.Count; i++)
        {
            if (Compare(arms[i - 1].Key, arms[i].Key) > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    arms[i].Location,
                    arms[i].Display,
                    orderDescription));
            }
        }
    }

    private static int Compare(ArmKey left, ArmKey right) => left.Kind switch
    {
        ArmKind.Number => decimal.Compare(left.Number, right.Number),
        _ => string.CompareOrdinal(left.Text, right.Text),
    };

    /// <summary>
    /// Classifies a pattern arm. Only a bare constant pattern or a discard
    /// (<c>_</c>) is recognized; any richer pattern shape is reported as
    /// <see cref="ArmKind.Complex"/> so the enclosing switch is skipped.
    /// </summary>
    private static ArmKey ClassifyPattern(PatternSyntax pattern, SemanticModel model, System.Threading.CancellationToken cancellationToken) => pattern switch
    {
        ConstantPatternSyntax constant => ClassifyExpression(constant.Expression, model, cancellationToken),
        DiscardPatternSyntax => ArmKey.Sentinel,
        _ => ArmKey.Complex,
    };

    /// <summary>
    /// Derives a sort key from a constant-bearing expression: a string keyed
    /// by value, and integer / decimal / floating constants keyed numerically.
    /// A <c>null</c> literal is a sentinel. <c>enum</c>, <c>char</c>, and
    /// <c>bool</c> constants — routinely ordered by meaning rather than value —
    /// are complex, so they exempt the whole switch; so is anything without a
    /// compile-time constant value.
    /// </summary>
    private static ArmKey ClassifyExpression(ExpressionSyntax expression, SemanticModel model, System.Threading.CancellationToken cancellationToken)
    {
        if (model.GetTypeInfo(expression, cancellationToken).Type?.TypeKind == TypeKind.Enum)
            return ArmKey.Complex;

        var constant = model.GetConstantValue(expression, cancellationToken);
        if (!constant.HasValue)
            return ArmKey.Complex;

        switch (constant.Value)
        {
            case null:
                return ArmKey.Sentinel;
            case string text:
                return ArmKey.OfString(text);
            case bool:
            case char:
                return ArmKey.Complex;
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal or float or double:
                try
                {
                    return ArmKey.OfNumber(Convert.ToDecimal(constant.Value));
                }
                catch (Exception exception) when (exception is OverflowException or InvalidCastException or FormatException)
                {
                    return ArmKey.Complex;
                }
            default:
                return ArmKey.Complex;
        }
    }

    private enum ArmKind
    {
        /// <summary>An arm shape that disqualifies the whole switch.</summary>
        Complex,

        /// <summary>A discard or <c>null</c> arm: excluded from ordering.</summary>
        Sentinel,

        String,
        Number,
    }

    private readonly struct ArmKey
    {
        public readonly ArmKind Kind;
        public readonly string? Text;
        public readonly decimal Number;

        private ArmKey(ArmKind kind, string? text, decimal number)
        {
            this.Kind = kind;
            this.Text = text;
            this.Number = number;
        }

        public static readonly ArmKey Complex = new(ArmKind.Complex, null, 0m);
        public static readonly ArmKey Sentinel = new(ArmKind.Sentinel, null, 0m);
        public static ArmKey OfString(string value) => new(ArmKind.String, value, 0m);
        public static ArmKey OfNumber(decimal value) => new(ArmKind.Number, null, value);
    }

    private readonly struct Arm(ArmKey key, Location location, string display)
    {
        public readonly ArmKey Key = key;
        public readonly Location Location = location;
        public readonly string Display = display;
    }
}
