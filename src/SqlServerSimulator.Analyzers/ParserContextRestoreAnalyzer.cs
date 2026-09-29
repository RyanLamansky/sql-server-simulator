using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a hand-written save / restore of a <c>ParserContext</c> field: a
/// local initialized from the field, assigned back to that same field inside
/// a <c>finally</c>. The parser holds a position flag or collector for a
/// lexical scope through a <c>ParserScope</c> guard instead —
/// <c>using var x = ParserScope.Enter(ref context.Field, value);</c> — which
/// restores on every exit path and can't be left half-restored by an edit
/// that adds a field to the save list and forgets the finally.
/// </summary>
/// <remarks>
/// Only the exact save-then-restore shape reports. A <c>finally</c> that
/// resets a field to a constant (<c>context.InTopCount = false</c>), adjusts a
/// counter (<c>context.CaseDepth--</c>), or restores outside a <c>finally</c>
/// is a different contract from a guard's, so each stays hand-written and
/// unflagged.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ParserContextRestoreAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS013",
        title: "ParserContext field restored by hand in a finally",
        messageFormat: "ParserContext.{0} is saved into '{1}' and restored in a finally; hold it with a guard instead (using var … = ParserScope.Enter(ref context.{0}, value), ParserScope.Save, or a ParserContext Enter* method)",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A ParserContext field held for a lexical scope is restored by a ParserScope guard declared with using, which restores on every exit path and keeps the save and the restore in one expression. A finally that assigns a saved local back to the field it was read from is the hand-written form of the same thing. Constant resets, counter adjustments and restores outside a finally are not flagged.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var parserContext = start.Compilation.GetTypeByMetadataName("SqlServerSimulator.Parser.ParserContext");
            if (parserContext is null)
                return;

            start.RegisterOperationAction(
                operationContext => AnalyzeAssignment(operationContext, parserContext),
                OperationKind.SimpleAssignment);
        });
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, INamedTypeSymbol parserContext)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is not IFieldReferenceOperation { Field: { IsStatic: false } field }
            || !SymbolEqualityComparer.Default.Equals(field.ContainingType, parserContext))
        {
            return;
        }
        if (Unwrap(assignment.Value) is not ILocalReferenceOperation { Local: { } local })
            return;
        if (!InFinally(assignment.Syntax))
            return;
        if (!InitializedFrom(local, field, assignment.SemanticModel, context.CancellationToken))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.Syntax.GetLocation(), field.Name, local.Name));
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
            operation = conversion.Operand;
        return operation;
    }

    /// <summary>
    /// Whether <paramref name="node"/> sits in a <c>finally</c> block of the
    /// member it belongs to, not reaching past a lambda or local function.
    /// </summary>
    private static bool InFinally(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case FinallyClauseSyntax:
                    return true;
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case MemberDeclarationSyntax:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="local"/>'s declaration initializes it by reading
    /// <paramref name="field"/>.
    /// </summary>
    private static bool InitializedFrom(ILocalSymbol local, IFieldSymbol field, SemanticModel? model, CancellationToken cancellationToken)
    {
        if (model is null)
            return false;
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is not VariableDeclaratorSyntax { Initializer.Value: { } value })
                continue;
            if (model.GetOperation(value, cancellationToken) is { } initializer
                && Unwrap(initializer) is IFieldReferenceOperation { Field: { } read }
                && SymbolEqualityComparer.Default.Equals(read, field))
            {
                return true;
            }
        }
        return false;
    }
}
