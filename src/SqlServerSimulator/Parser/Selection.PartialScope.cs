using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

internal sealed partial class Selection
{
    /// <summary>
    /// Set while <see cref="PartialScopeBinding"/> asks a resolver chain which
    /// source binds a name: <see cref="ResolveColumnTypeAcrossSources"/> then
    /// records the first source that answers in <see cref="probedSource"/>.
    /// </summary>
    [ThreadStatic]
    private static bool probingBinding;

    /// <inheritdoc cref="probingBinding"/>
    [ThreadStatic]
    private static FromSource? probedSource;

    /// <summary>
    /// The names a FROM clause's <c>ON</c> predicates and <c>APPLY</c> right
    /// sides bound against part of that clause, kept until the clause is whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>ON</c> sees only its own chain's sources up to and including its
    /// join's right operand, and an <c>APPLY</c> right side only its chain's
    /// sources to its left (probed 2026-10-08 against SQL Server 2025): an
    /// unqualified name binds there even when a source joined later, or an
    /// earlier comma-separated item, carries a column of the same name, and a
    /// name none of them carries binds to an enclosing query even when a later
    /// source does. Every resolution after the parse — the planner's key,
    /// pushdown and reorder analysis, the per-row tuple resolver, a correlated
    /// body's outer resolver — reads the whole FROM clause by name, so such a
    /// name left as written is ambiguous there (Msg 209) or binds to the wrong
    /// source.
    /// </para>
    /// <para>
    /// So each one-part name resolved through a partial scope is recorded, and
    /// once the clause is whole (<see cref="Pin"/>) a name some source outside
    /// that scope also carries is qualified by the exposed name of the source
    /// that bound it, in every reference parsed with it — the <c>ON</c>'s own,
    /// and those in subqueries and <c>APPLY</c> bodies, which correlate through
    /// the same scope. After that every later resolution agrees with the bind
    /// by construction. A binding source that exposes no name (an unaliased
    /// rowset function), or an enclosing query's source whose exposed name a
    /// source of this clause repeats, can't be named this way and stays as
    /// written.
    /// </para>
    /// </remarks>
    internal sealed class PartialScopeBinding(List<Reference>? enclosingReferences)
    {
        private readonly List<BoundName> names = [];

        /// <summary>
        /// The <c>ON</c> predicates parsed, whose own references bind only as
        /// the query compiles — after the clause is pinned — so
        /// <see cref="Pin"/> resolves them itself.
        /// </summary>
        private List<OnPredicate>? predicates;

        /// <summary>
        /// Every reference parsed inside an <c>ON</c> or <c>APPLY</c> right
        /// side of this clause — shared with an enclosing clause's when this
        /// one is nested in such a region, whose names may sit in this one's
        /// subqueries.
        /// </summary>
        private List<Reference>? references = enclosingReferences;

        /// <summary>Set by the first <see cref="Pin"/>; a resolver handed out earlier records nothing after it.</summary>
        private bool closed;

        /// <summary>The list <see cref="ParserContext.PartialScopeReferences"/> collects into while a region of this clause parses.</summary>
        public List<Reference> References() => this.references ??= [];

        /// <summary>
        /// A column-type resolver over <paramref name="scope"/>, part of this
        /// clause, falling back to <paramref name="outer"/>, which records each
        /// one-part name it is asked for.
        /// </summary>
        public Func<MultiPartName, SqlType> ResolverOver(FromSource[] scope, Func<MultiPartName, SqlType>? outer) => name =>
        {
            if (name.Count == 1 && !this.closed && !RowLocator.IsLocatorName(name))
                this.names.Add(new BoundName(name, scope, outer));
            return ResolveColumnTypeAcrossSources(scope, name, outer);
        };

        /// <summary>
        /// Records an <c>ON</c> predicate bound against <paramref name="scope"/>,
        /// falling back to <paramref name="outer"/>.
        /// </summary>
        public void AddOn(BooleanExpression predicate, FromSource[] scope, Func<MultiPartName, SqlType>? outer) =>
            (this.predicates ??= []).Add(new OnPredicate(predicate, scope, outer));

        /// <summary>
        /// Qualifies each recorded name a source of <paramref name="sources"/>
        /// outside its scope also carries. Called once the clause is parsed,
        /// and again by a joined <c>UPDATE</c> / <c>DELETE</c> that appends a
        /// target its <c>FROM</c> didn't name.
        /// </summary>
        public void Pin(List<FromSource> sources)
        {
            this.closed = true;
            if (this.references is not { Count: > 0 } references)
                return;
            if (this.predicates is { } predicates)
            {
                // A predicate's own references, not a subquery's, which bound
                // through the recording resolver as the subquery parsed.
                this.predicates = null;
                foreach (var on in predicates)
                {
                    var state = (this.names, on);
                    on.Predicate.Walk(ref state, static (node, _, ref state) =>
                    {
                        if (node is Reference { ReferencedName.Count: 1 } reference && !RowLocator.IsLocatorName(reference.ReferencedName))
                            state.names.Add(new BoundName(reference.ReferencedName, state.on.Scope, state.on.Outer));
                        return true;
                    });
                }
            }
            foreach (var bound in this.names)
            {
                if (!CarriedOutside(sources, bound.Scope, bound.Name) || BindingSource(bound) is not { Qualifier: { } qualifier } source)
                    continue;
                // An enclosing query's source can be named only where no
                // source of this clause takes the name for itself.
                if (!sources.Contains(source) && sources.Exists(s => s.Qualifier is { } exposed && BuiltInToken.Equals(exposed, qualifier)))
                    continue;
                var pinned = bound.Name.WithQualifier(qualifier);
                foreach (var reference in references)
                {
                    if (reference.ReferencedName.IsSameInstanceAs(bound.Name))
                        reference.ReferencedName = pinned;
                }
            }
        }

        /// <summary>Whether a source of <paramref name="sources"/> that <paramref name="scope"/> doesn't hold carries <paramref name="name"/>.</summary>
        private static bool CarriedOutside(List<FromSource> sources, FromSource[] scope, MultiPartName name)
        {
            foreach (var source in sources)
            {
                if (Array.IndexOf(scope, source) >= 0)
                    continue;
                foreach (var column in source.ColumnNames)
                {
                    if (BuiltInToken.Equals(column, name.Leaf))
                        return true;
                }
                if (name.Leaf.StartsWith('$') && TryResolveSourceColumn([source], name) is not null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The source <paramref name="bound"/>'s name binds to through the
        /// resolver chain that bound it — its own scope's, or an enclosing
        /// query's — or null when none answers with a source.
        /// </summary>
        private static FromSource? BindingSource(BoundName bound)
        {
            probingBinding = true;
            probedSource = null;
            try
            {
                _ = ResolveColumnTypeAcrossSources(bound.Scope, bound.Name, bound.Outer);
                return probedSource;
            }
            catch (SimulatedSqlException)
            {
                return null;
            }
            finally
            {
                probingBinding = false;
                probedSource = null;
            }
        }

        private readonly struct OnPredicate(BooleanExpression predicate, FromSource[] scope, Func<MultiPartName, SqlType>? outer)
        {
            public readonly BooleanExpression Predicate = predicate;
            public readonly FromSource[] Scope = scope;
            public readonly Func<MultiPartName, SqlType>? Outer = outer;
        }

        private readonly struct BoundName(MultiPartName name, FromSource[] scope, Func<MultiPartName, SqlType>? outer)
        {
            public readonly MultiPartName Name = name;
            public readonly FromSource[] Scope = scope;
            public readonly Func<MultiPartName, SqlType>? Outer = outer;
        }
    }
}
