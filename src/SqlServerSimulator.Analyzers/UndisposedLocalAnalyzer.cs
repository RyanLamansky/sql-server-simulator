using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a disposable object that is created and then abandoned undisposed in
/// one of two plainly visible shapes: a local declared (outside <c>using</c>)
/// from a <c>new</c> of a disposable class, every later use of which is a
/// member access other than <c>Dispose</c> / <c>DisposeAsync</c> /
/// <c>Close</c>; and a <c>new</c> of a disposable class whose value is thrown
/// away at once, as a statement of its own or as the receiver of a call whose
/// result isn't that object. It stands in for the SDK's CA2000, whose
/// whole-method dispose dataflow was among the costliest analyzers in a build.
/// </summary>
/// <remarks>
/// <para>
/// Anything that could hand the object on is taken as a transfer of ownership
/// and ends the check: returning, assigning, passing it as an argument (an
/// extension-method call included), capturing it in a lambda or local function,
/// storing it in an initializer, or reaching it through <c>?.</c>. That is
/// narrower than CA2000, which tracks the object through those paths and
/// across branches, so an object disposed on one path and leaked on another,
/// or leaked through a helper, goes unreported. Only a <c>new</c> counts as
/// creating the object: a method's result may be borrowed (a cached instance,
/// a <c>Volatile.Read</c> of a field) rather than owned, which only CA2000's
/// interprocedural look into the method can tell apart.
/// </para>
/// <para>
/// Value types (pattern enumerators among them) and the BCL types whose
/// <c>Dispose</c> releases nothing that CA2000 also exempts
/// (<c>MemoryStream</c>, <c>StringReader</c>, <c>StringWriter</c>, <c>Task</c>)
/// are never flagged.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedLocalAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS018",
        title: "Disposable object never disposed",
        messageFormat: "'{0}' is an {1} that is never disposed or handed on; wrap it in 'using' (or 'using var')",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A disposable object created with 'new' and only ever used through its members, or discarded as soon as it's created, is never disposed, so what it holds (a command's batch, a lock, a stream) is released only by the finalizer, if at all. Declare it with 'using var' or a 'using' block. The rule treats any return, assignment, argument, capture or initializer use as an ownership transfer and stays silent; where ownership passes in a way it can't see, suppress with #pragma warning disable SSS018 and a rationale. It replaces the SDK's CA2000, which is disabled for build speed.");

    private static readonly ImmutableArray<string> ExemptTypes =
    [
        "System.IO.MemoryStream",
        "System.IO.StringReader",
        "System.IO.StringWriter",
        "System.IO.UnmanagedMemoryStream",
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task`1",
    ];

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var disposable = start.Compilation.GetSpecialType(SpecialType.System_IDisposable);
            var asyncDisposable = start.Compilation.GetTypeByMetadataName("System.IAsyncDisposable");
            var exempt = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
            foreach (var name in ExemptTypes)
            {
                if (start.Compilation.GetTypeByMetadataName(name) is { } type)
                    exempt.Add(type);
            }

            var types = new DisposableTypes(disposable, asyncDisposable, exempt.ToImmutable());
            start.RegisterSyntaxNodeAction(context => AnalyzeDeclaration(context, types), SyntaxKind.LocalDeclarationStatement);
            start.RegisterSyntaxNodeAction(context => AnalyzeCreation(context, types), SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        });
    }

    private static void AnalyzeDeclaration(SyntaxNodeAnalysisContext context, DisposableTypes types)
    {
        var statement = (LocalDeclarationStatementSyntax)context.Node;
        if (!statement.UsingKeyword.IsKind(SyntaxKind.None) || statement.Parent is not (BlockSyntax or SwitchSectionSyntax))
            return;

        foreach (var variable in statement.Declaration.Variables)
        {
            if (variable.Initializer?.Value is not BaseObjectCreationExpressionSyntax)
                continue;

            if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not ILocalSymbol local
                || !types.MustDispose(local.Type))
            {
                continue;
            }

            if (IsOnlyUsedThroughMembers(local, statement, context.SemanticModel, context.CancellationToken))
                context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), local.Name, local.Type.Name));
        }
    }

    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context, DisposableTypes types)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        SyntaxNode node = creation;
        while (node.Parent is ParenthesizedExpressionSyntax)
            node = node.Parent;

        switch (node.Parent)
        {
            case ExpressionStatementSyntax:
                break;
            case MemberAccessExpressionSyntax access when access.Expression == node:
                // `new D().Member`: the object survives only if the member hands back
                // something that could be the object itself.
                if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not { } created
                    || context.SemanticModel.GetTypeInfo(access.Parent is InvocationExpressionSyntax invocation ? invocation : access, context.CancellationToken).Type is not { } result
                    || result.SpecialType == SpecialType.System_Void
                    || !context.SemanticModel.Compilation.ClassifyConversion(created, result).IsImplicit)
                {
                    break;
                }

                return;
            default:
                return;
        }

        if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is { } type && types.MustDispose(type))
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString(), type.Name));
    }

    /// <summary>
    /// True when every reference to <paramref name="local"/> after its
    /// declaration is the receiver of a member access that neither disposes it
    /// nor passes it on as an extension method's argument, with none inside a
    /// lambda or local function.
    /// </summary>
    private static bool IsOnlyUsedThroughMembers(ILocalSymbol local, LocalDeclarationStatementSyntax declaration, SemanticModel model, System.Threading.CancellationToken cancellationToken)
    {
        foreach (var node in declaration.Parent!.DescendantNodes())
        {
            if (node is not IdentifierNameSyntax identifier
                || identifier.Identifier.ValueText != local.Name
                || identifier.SpanStart < declaration.Span.End
                || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, local))
            {
                continue;
            }

            if (identifier.Parent is not MemberAccessExpressionSyntax access || access.Expression != identifier)
                return false;

            switch (access.Name.Identifier.ValueText)
            {
                case "Close":
                case "Dispose":
                case "DisposeAsync":
                    return false;
            }

            if (model.GetSymbolInfo(access, cancellationToken).Symbol is IMethodSymbol { IsExtensionMethod: true })
                return false;

            for (var ancestor = identifier.Parent; ancestor != declaration.Parent; ancestor = ancestor.Parent!)
            {
                if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                    return false;
            }
        }

        return true;
    }

    private sealed class DisposableTypes(INamedTypeSymbol disposable, INamedTypeSymbol? asyncDisposable, ImmutableArray<INamedTypeSymbol> exempt)
    {
        /// <summary>
        /// True when <paramref name="type"/> is a class implementing
        /// <see cref="System.IDisposable"/> or <c>IAsyncDisposable</c> that isn't
        /// one of the exempt BCL types or derived from one.
        /// </summary>
        public bool MustDispose(ITypeSymbol type)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Interface))
                return false;

            var implements = SymbolEqualityComparer.Default.Equals(type, disposable) || SymbolEqualityComparer.Default.Equals(type, asyncDisposable);
            foreach (var candidate in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(candidate, disposable) || SymbolEqualityComparer.Default.Equals(candidate, asyncDisposable))
                    implements = true;
            }

            if (!implements)
                return false;

            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                foreach (var candidate in exempt)
                {
                    if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, candidate))
                        return false;
                }
            }

            return true;
        }
    }
}
