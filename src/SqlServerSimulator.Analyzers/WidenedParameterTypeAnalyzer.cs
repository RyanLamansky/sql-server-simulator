using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a parameter of a non-public method, constructor or local function
/// whose declared type is one of the general-purpose collection interfaces
/// from <c>System.Collections</c>, <c>System.Collections.Generic</c> or
/// <c>System.Collections.Immutable</c> when every call site in the compilation
/// passes an argument of the same concrete type. The parameter half of
/// <see cref="WidenedFieldTypeAnalyzer"/> (SSS002).
/// </summary>
/// <remarks>
/// <para>
/// An interface-typed parameter buys flexibility the callers never use: every
/// read through it is an interface dispatch (a vtable lookup the concrete type
/// would have resolved statically, and one the JIT can't devirtualize when the
/// declared type is all it knows), and the members the concrete type adds are
/// invisible at the use sites. When the whole call chain hands over one type,
/// declaring that type costs nothing and removes the indirection.
/// </para>
/// <para>
/// The value-type case runs the *opposite* way from SSS002's. That rule exempts
/// a value-typed initializer because narrowing an <c>object</c>-typed field to
/// the struct would remove a deliberate boxing boundary; here the boxing is the
/// injury — passing an <see cref="ImmutableArray{T}"/> or any other struct
/// collection as an interface boxes it on every call — so those hits are the
/// most valuable ones the rule finds.
/// </para>
/// <para>
/// Exemptions. Public and protected members of public types are API surface,
/// where the interface is the documented contract. Overrides, abstract and
/// virtual members, and implicit or explicit interface implementations don't
/// own their signature. Partial methods split the parameter across two symbols
/// (call sites bind to the defining part alone), so they're skipped rather than
/// half-reported. <c>params</c> arrays, by-reference parameters, and lambda /
/// delegate parameter positions are out of scope. A parameter whose type
/// mentions a type parameter is skipped: the concrete argument type at a call
/// site is a substituted one, which says nothing about how to spell the
/// replacement in the declaration.
/// </para>
/// <para>
/// Call-site evidence. An argument that is <see langword="null"/>, an omitted
/// optional argument, or <c>default</c> carries no type, so it neither
/// contributes one nor disqualifies — but if the concrete type turns out to be
/// a value type, a null argument (or an <c>= null</c> default) makes the
/// replacement inexpressible and the parameter goes unreported. An argument
/// whose own compile-time type is an interface — it arrived through another
/// interface-typed parameter or field — settles the parameter as genuinely
/// interface-fed, so nothing is reported. Fixing that upstream declaration
/// turns the argument concrete and the rule then fires here, so a fix pass
/// iterates to a fixed point rather than converging in one sweep.
/// </para>
/// <para>
/// A method whose group is converted to a delegate anywhere in the compilation
/// is exempt: the conversion pins the signature. Polymorphism that only shows
/// up outside the compilation — a call from a test assembly reaching an
/// <c>internal</c> member, or a reflective invoke — takes a
/// <c>#pragma warning disable SSS010</c> with a one-line rationale, the escape
/// hatch <see cref="SortedConstantSwitchAnalyzer"/> (SSS005) and
/// <see cref="UnfrozenStaticCollectionAnalyzer"/> (SSS008) use.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WidenedParameterTypeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS010",
        title: "Parameter declared as a collection interface every call site satisfies with one concrete type",
        messageFormat: "Parameter '{0}' of '{1}' is declared as '{2}', but every call site passes '{3}'; declare the parameter as '{3}' so the calls read it directly instead of through interface dispatch",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A non-public method, constructor or local function whose collection-interface parameter is satisfied by one concrete type at every call site gains nothing from the interface: each read pays a dispatch the concrete type would have resolved statically, a struct collection boxes on every call, and the concrete type's own members are hidden from the body. Declare the concrete type. Public API surface, overrides and interface implementations are exempt, as is a parameter fed an interface-typed argument anywhere — fixing that upstream declaration is what makes this one flaggable.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // The verdict depends on every call site in the compilation, so the
        // per-compilation state has to outlive the individual actions and the
        // report has to wait for the last of them. State lives on a local
        // object created here rather than on the analyzer (which is shared
        // across compilations).
        context.RegisterCompilationStartAction(start =>
        {
            var state = new CompilationState();
            start.RegisterSyntaxNodeAction(state.CollectCandidate, SyntaxKind.Parameter);
            start.RegisterOperationAction(state.CollectArguments, OperationKind.Invocation, OperationKind.ObjectCreation);
            start.RegisterOperationAction(state.CollectMethodGroupUse, OperationKind.MethodReference);
            start.RegisterCompilationEndAction(state.Report);
        });
    }

    /// <summary>
    /// Per-compilation scratch: the parameters worth reporting, what the call
    /// sites passed to each, and the methods a delegate conversion pinned.
    /// Every collection is concurrent because the actions above run in
    /// parallel across the compilation's trees.
    /// </summary>
    private sealed class CompilationState
    {
        private readonly ConcurrentDictionary<IParameterSymbol, Candidate> candidates =
            new(SymbolEqualityComparer.Default);

        private readonly ConcurrentDictionary<IParameterSymbol, Evidence> evidence =
            new(SymbolEqualityComparer.Default);

        private readonly ConcurrentDictionary<IMethodSymbol, byte> delegateConverted =
            new(SymbolEqualityComparer.Default);

        /// <summary>
        /// Records a parameter this rule could report — the shape checks that
        /// don't depend on call sites all happen here, so a parameter that
        /// reaches the dictionary needs only its evidence to be judged.
        /// </summary>
        internal void CollectCandidate(SyntaxNodeAnalysisContext context)
        {
            var syntax = (ParameterSyntax)context.Node;

            // A lambda parameter can be written without a type; its symbol's
            // type is inferred and the declaration has nowhere to put a
            // concrete one. (The method-kind filter below excludes lambdas
            // anyway — this is the cheap way out before touching the model.)
            if (syntax.Type is null)
                return;

            if (context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not IParameterSymbol parameter)
                return;

            if (parameter.ContainingSymbol is not IMethodSymbol method)
                return;

            if (!CollectionInterfaces.IsFlaggableMethod(method))
                return;

            // params arrays bind a variable-length argument list, by-reference
            // parameters demand an exact type match at the call site, and an
            // optional parameter's own default is checked at report time
            // against the concrete type it would have to be spelled in.
            if (parameter.IsParams || parameter.RefKind != RefKind.None)
                return;

            if (parameter.Type is not INamedTypeSymbol { TypeKind: TypeKind.Interface } declaredType)
                return;

            if (!CollectionInterfaces.IsGoverned(declaredType))
                return;

            // `IReadOnlyList<T>` in a generic method or type: the argument at a
            // call site is `List<string>`, which doesn't say whether the
            // declaration should read `List<T>` or something else entirely.
            if (CollectionInterfaces.MentionsTypeParameter(declaredType))
                return;

            _ = this.candidates.TryAdd(parameter, new Candidate(
                syntax.Identifier.GetLocation(),
                declaredType,
                method,
                NullIsInexpressible(parameter)));
        }

        /// <summary>
        /// Folds one call's arguments into the evidence for the parameters they
        /// bound to. Runs for every invocation and object creation in the
        /// compilation, so it exits on the first cheap check for the vast
        /// majority that pass no collection interface anywhere.
        /// </summary>
        /// <remarks>
        /// The filter here repeats the declaration-side type test rather than
        /// asking whether the parameter is already a known candidate: the
        /// actions run concurrently, so a call site can be reached before the
        /// declaration it binds to and a candidate lookup would silently drop
        /// that call site's evidence.
        /// </remarks>
        internal void CollectArguments(OperationAnalysisContext context)
        {
            var arguments = context.Operation switch
            {
                IInvocationOperation invocation => invocation.Arguments,
                IObjectCreationOperation creation => creation.Arguments,
                _ => [],
            };

            foreach (var argument in arguments)
            {
                if (argument.Parameter is not { } parameter)
                    continue;

                var target = Canonical(parameter);
                if (target.Type is not INamedTypeSymbol { TypeKind: TypeKind.Interface } declaredType)
                    continue;
                if (!CollectionInterfaces.IsGoverned(declaredType))
                    continue;

                // Only a parameter declared in this compilation can be
                // rewritten; a call into a referenced assembly is noise.
                if (target.DeclaringSyntaxReferences.IsEmpty)
                    continue;

                // An argument the caller didn't write — an omitted optional
                // one — says nothing about what the caller has in hand.
                if (argument.ArgumentKind != ArgumentKind.Explicit)
                    continue;

                this.Record(target, argument.Value);
            }
        }

        /// <summary>
        /// Marks a method whose group was converted to a delegate. The
        /// conversion's target signature pins the parameter types, so
        /// rewriting one would break the conversion rather than improve it.
        /// </summary>
        internal void CollectMethodGroupUse(OperationAnalysisContext context)
        {
            if (context.Operation is IMethodReferenceOperation { Method: { } method })
                _ = this.delegateConverted.TryAdd(method.OriginalDefinition, 0);
        }

        /// <summary>
        /// Reports every candidate whose evidence settled on one concrete type.
        /// Ordered by source position so a build's diagnostic list is stable
        /// across runs despite the concurrent collection above.
        /// </summary>
        internal void Report(CompilationAnalysisContext context)
        {
            var reports = new List<Diagnostic>();

            foreach (var entry in this.candidates)
            {
                var candidate = entry.Value;

                if (!this.evidence.TryGetValue(entry.Key, out var settled))
                    continue;
                if (settled.Conflicted || settled.Type is null)
                    continue;

                // A struct concrete type can't absorb the nulls the interface
                // accepted, whether they came from a call site or from the
                // parameter's own `= null` default.
                if (settled.Type.IsValueType && (settled.SawNull || candidate.NullIsInexpressible))
                    continue;

                if (this.delegateConverted.ContainsKey(candidate.Method.OriginalDefinition))
                    continue;

                reports.Add(Diagnostic.Create(
                    Rule,
                    candidate.Location,
                    entry.Key.Name,
                    candidate.Method.MethodKind == MethodKind.Constructor
                        ? candidate.Method.ContainingType.Name
                        : candidate.Method.Name,
                    candidate.DeclaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    settled.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }

            reports.Sort(CompareByPosition);
            foreach (var report in reports)
                context.ReportDiagnostic(report);
        }

        /// <summary>
        /// Folds one written argument into <paramref name="target"/>'s
        /// evidence: a concrete type either settles it or clashes with what an
        /// earlier call site settled, an interface-typed one settles the
        /// question the other way, and an untyped one abstains.
        /// </summary>
        private void Record(IParameterSymbol target, IOperation value)
        {
            value = Unwrap(value);

            // `default` names the parameter's own type, and `null` has no type
            // at all — neither reveals anything about the caller's value, but
            // both have to be remembered in case the answer turns out to be a
            // struct.
            if (value is IDefaultValueOperation || value.Type is null)
            {
                _ = this.evidence.AddOrUpdate(target, Evidence.Null, (_, existing) => existing.WithNull());
                return;
            }

            // An interface (the argument came through another interface-typed
            // declaration), a type parameter, or a type the binder couldn't
            // resolve: nothing this rule could put in the declaration.
            var settles = value.Type.TypeKind is not (TypeKind.Interface or TypeKind.TypeParameter or TypeKind.Error);
            if (!settles)
            {
                _ = this.evidence.AddOrUpdate(target, Evidence.Conflict, (_, existing) => existing.AsConflict());
                return;
            }

            _ = this.evidence.AddOrUpdate(
                target,
                _ => new Evidence(value.Type, conflicted: false, sawNull: false),
                (_, existing) => existing.WithType(value.Type));
        }

        private static int CompareByPosition(Diagnostic left, Diagnostic right)
        {
            var byPath = string.CompareOrdinal(
                left.Location.SourceTree?.FilePath ?? string.Empty,
                right.Location.SourceTree?.FilePath ?? string.Empty);
            return byPath != 0 ? byPath : left.Location.SourceSpan.Start.CompareTo(right.Location.SourceSpan.Start);
        }
    }

    /// <summary>A parameter awaiting its call sites' verdict.</summary>
    private sealed class Candidate(Location location, INamedTypeSymbol declaredType, IMethodSymbol method, bool nullIsInexpressible)
    {
        internal readonly Location Location = location;
        internal readonly INamedTypeSymbol DeclaredType = declaredType;
        internal readonly IMethodSymbol Method = method;

        /// <summary>
        /// True when the declaration itself hands the parameter a null the
        /// interface accepted — an <c>= null</c> optional default. A struct
        /// replacement couldn't be spelled without a <c>Nullable&lt;T&gt;</c>
        /// that reintroduces the indirection.
        /// </summary>
        internal readonly bool NullIsInexpressible = nullIsInexpressible;
    }

    /// <summary>What the call sites seen so far said about one parameter.</summary>
    private readonly struct Evidence(ITypeSymbol? type, bool conflicted, bool sawNull)
    {
        internal static readonly Evidence Null = new(null, conflicted: false, sawNull: true);
        internal static readonly Evidence Conflict = new(null, conflicted: true, sawNull: false);

        /// <summary>The one concrete type every call site passed, if any.</summary>
        internal readonly ITypeSymbol? Type = type;

        /// <summary>True once two call sites disagreed or one passed an interface.</summary>
        internal readonly bool Conflicted = conflicted;

        /// <summary>True once a call site passed <c>null</c> or <c>default</c>.</summary>
        internal readonly bool SawNull = sawNull;

        internal Evidence WithNull() => new(this.Type, this.Conflicted, sawNull: true);

        internal Evidence AsConflict() => new(null, conflicted: true, this.SawNull);

        internal Evidence WithType(ITypeSymbol observed) =>
            this.Conflicted ? this
            : this.Type is null ? new Evidence(observed, conflicted: false, this.SawNull)
            : SymbolEqualityComparer.Default.Equals(this.Type, observed) ? this
            : new Evidence(null, conflicted: true, this.SawNull);
    }

    /// <summary>
    /// True when the parameter's declared default is a null the interface
    /// accepted, which a value-typed replacement couldn't restate.
    /// </summary>
    private static bool NullIsInexpressible(IParameterSymbol parameter) =>
        parameter.HasExplicitDefaultValue && parameter.ExplicitDefaultValue is null;

    /// <summary>
    /// Peels the compiler-inserted conversions off an argument so the type
    /// read is the one the caller wrote. An explicit cast is left in place: the
    /// author naming the interface there is the same statement of intent an
    /// interface-typed variable makes.
    /// </summary>
    private static IOperation Unwrap(IOperation value)
    {
        while (value is IConversionOperation { IsImplicit: true, Operand: { } operand })
            value = operand;
        return value;
    }

    /// <summary>
    /// The parameter symbol call-site evidence is keyed by: the definition's,
    /// so every instantiation of a generic method folds into one verdict, and
    /// the unreduced one for an extension method invoked in reduced form.
    /// </summary>
    private static IParameterSymbol Canonical(IParameterSymbol parameter)
    {
        var definition = parameter.OriginalDefinition;
        return definition.ContainingSymbol is IMethodSymbol { ReducedFrom: { } reducedFrom }
            && definition.Ordinal + 1 < reducedFrom.OriginalDefinition.Parameters.Length
            ? reducedFrom.OriginalDefinition.Parameters[definition.Ordinal + 1]
            : definition;
    }
}
