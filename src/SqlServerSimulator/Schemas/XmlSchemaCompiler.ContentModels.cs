using System.Xml;

namespace SqlServerSimulator.Schemas;

// Particles: the content models complex types and groups declare, and the
// rules real holds them to.
internal sealed partial class XmlSchemaCompiler
{
    /// <summary>Above this many positions the ambiguity check gives up and accepts, rather than spend unbounded work on a pathological model.</summary>
    private const int PositionBudget = 20_000;

    private Dictionary<XsdNode, List<XmlQualifiedName>>? substitutionMembers;

    /// <summary>The particle a complex type, derivation or group holds, its declarations compiled in written order; null for none.</summary>
    private Particle? ParticleOfContent(XsdNode holder)
    {
        foreach (var child in holder.Children)
        {
            if (child.IsXsd && child.Local is "all" or "choice" or "group" or "sequence")
                return this.BuildParticle(child);
        }
        return null;
    }

    private static int Occurs(XsdNode node, string attribute)
    {
        var written = node.Element.Attribute(attribute)?.Value.Trim();
        if (written is null)
            return 1;
        if (written == "unbounded")
            return int.MaxValue;
        return ulong.TryParse(written, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Min(value, int.MaxValue - 1)
            : 1;
    }

    private Particle? BuildParticle(XsdNode node)
    {
        var min = Occurs(node, "minOccurs");
        var max = Occurs(node, "maxOccurs");
        switch (node.Local)
        {
            case "any" or "anyAttribute":
                var namespaces = (node.Element.Attribute("namespace")?.Value ?? "##any").Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                return new Particle(ParticleKind.Any, min, max)
                {
                    Wildcard = namespaces switch
                    {
                        ["##any"] => null,
                        ["##other"] => [],
                        _ => [.. namespaces.Select(token => token switch
                        {
                            "##local" => string.Empty,
                            "##targetNamespace" => node.TargetNamespace,
                            _ => token,
                        })],
                    },
                    ExcludedNamespace = namespaces is ["##other"] ? node.TargetNamespace : null,
                    ProcessContents = (node.Element.Attribute("processContents")?.Value.Trim()) switch
                    {
                        "lax" => 1,
                        "skip" => 2,
                        _ => 0,
                    },
                };
            case "element":
                var type = this.CompileElement(node);
                var declaration = node;
                XmlQualifiedName name;
                if (node.Element.Attribute("ref")?.Value is { } reference)
                {
                    name = ResolveQualifiedName(node, reference.Trim());
                    if (this.globals.TryGetValue(("element", name.Namespace, name.Name), out var global))
                        declaration = global;
                }
                else
                {
                    name = new XmlQualifiedName(DeclaredName(node), IsQualified(node, "elementFormDefault") ? node.TargetNamespace : string.Empty);
                }
                var particle = new Particle(ParticleKind.Element, min, max)
                {
                    Name = name,
                    Declaration = declaration,
                    Type = type,
                    Nillable = IsTrue(declaration.Element.Attribute("nillable")?.Value),
                    ValueConstraint = declaration.Element.Attribute("default")?.Value is { } defaultValue ? "d" + defaultValue
                        : declaration.Element.Attribute("fixed")?.Value is { } fixedValue ? "f" + fixedValue : null,
                };
                particle.Names.Add(name);
                if (!ReferenceEquals(declaration, node))
                    particle.Names.AddRange(this.SubstitutesFor(declaration));
                return particle;
            case "group":
                if (node.Element.Attribute("ref")?.Value is not { } groupReference)
                    return null;
                var groupName = ResolveQualifiedName(node, groupReference.Trim());
                if (!this.globals.TryGetValue(("group", groupName.Namespace, groupName.Name), out var group))
                    return null;
                if (this.compilingGroups.Contains(group))
                {
                    // A reference straight from a group's own content closes
                    // a loop; one inside an element's type doesn't.
                    var enclosing = node;
                    while (enclosing.Parent!.Parent is not null && enclosing.Local != "element")
                        enclosing = enclosing.Parent;
                    if (enclosing.Local == "group" && enclosing.Parent.Parent is null)
                        throw SimulatedSqlException.XmlSchemaRecursiveGroup(DeclaredName(enclosing));
                    return null;
                }
                if (this.CompileGroup(group) is not { } body)
                    return null;
                var referenced = body.Copy(min, max);
                referenced.ViaReference = true;
                return referenced;
            default:
                var compositor = new Particle(
                    node.Local switch
                    {
                        "all" => ParticleKind.All,
                        "choice" => ParticleKind.Choice,
                        _ => ParticleKind.Sequence,
                    },
                    min,
                    max);
                foreach (var child in node.Children)
                {
                    if (child.IsXsd && child.Local is "all" or "any" or "choice" or "element" or "group" or "sequence" && this.BuildParticle(child) is { } built)
                        compositor.Children.Add(built);
                }
                return compositor;
        }
    }

    /// <summary>The names of the global elements whose substitution group reaches <paramref name="head"/>.</summary>
    private List<XmlQualifiedName> SubstitutesFor(XsdNode head)
    {
        if (this.substitutionMembers is null)
        {
            this.substitutionMembers = [];
            foreach (var ((kind, ns, name), declaration) in this.globals)
            {
                if (kind != "element")
                    continue;
                var seen = new HashSet<XsdNode> { declaration };
                for (var current = declaration; current.Element.Attribute("substitutionGroup")?.Value is { } headName;)
                {
                    var resolved = ResolveQualifiedName(current, headName.Trim());
                    if (!this.globals.TryGetValue(("element", resolved.Namespace, resolved.Name), out var next) || !seen.Add(next))
                        break;
                    if (!this.substitutionMembers.TryGetValue(next, out var members))
                        this.substitutionMembers[next] = members = [];
                    members.Add(new XmlQualifiedName(name, ns));
                    current = next;
                }
            }
        }
        return this.substitutionMembers.GetValueOrDefault(head) ?? [];
    }

    private static bool Emptiable(Particle? particle) => particle switch
    {
        null => true,
        { Min: 0 } => true,
        { Kind: ParticleKind.Any or ParticleKind.Element } => false,
        { Kind: ParticleKind.Choice } => particle.Children.Count == 0 || particle.Children.Exists(static child => Emptiable(child)),
        _ => particle.Children.TrueForAll(static child => Emptiable(child)),
    };

    private static bool IsEmpty(Particle? particle) => particle switch
    {
        null or { Max: 0 } => true,
        { Kind: ParticleKind.Any or ParticleKind.Element } => false,
        _ => particle.Children.TrueForAll(static child => IsEmpty(child)),
    };

    /// <summary>
    /// A complex type's content model, in real's order: an <c>all</c> group
    /// nested in another, repeated or holding a repeated element (Msg 6958),
    /// two elements of one name that differ (Msg 6992), and a model that
    /// violates unique particle attribution (Msg 6916).
    /// </summary>
    private static void RejectContentModel(ComplexXsdType type, Particle? particle)
    {
        if (particle is null)
            return;
        if (InvalidAllGroup(particle) is { } allState)
            throw SimulatedSqlException.XmlSchemaInvalidAllGroup(type.Name, allState);

        var declared = new Dictionary<XmlQualifiedName, Particle>();
        if (InconsistentElement(particle, declared) is { } name)
            throw SimulatedSqlException.XmlSchemaInconsistentElements(type.Name, name);

        if (IsAmbiguous(particle))
            throw SimulatedSqlException.XmlSchemaAmbiguousContentModel(type.Name);
    }

    /// <summary>
    /// Msg 6958's state for an <c>all</c> group occurring more than once
    /// (state 2 when a group reference repeats it) or holding an element that
    /// does (state 1), or null; real doesn't refuse an <c>all</c> nested in
    /// another model group (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static byte? InvalidAllGroup(Particle particle)
    {
        if (particle.Kind == ParticleKind.All)
        {
            if (particle.Max != 1 || particle.Min > 1)
                return particle.ViaReference ? (byte)2 : (byte)1;
            return particle.Children.Exists(static child => child.Max > 1) ? 1 : null;
        }
        foreach (var child in particle.Children)
        {
            if (InvalidAllGroup(child) is { } state)
                return state;
        }
        return null;
    }

    /// <summary>Whether two element particles carry the same value constraint: the same kind, and one value of their type.</summary>
    private static bool SameConstraint(Particle left, Particle right) =>
        left.ValueConstraint is null || right.ValueConstraint is null
            ? left.ValueConstraint == right.ValueConstraint
            : left.ValueConstraint[0] == right.ValueConstraint[0] && SameValue(left.Type, left.ValueConstraint[1..], right.ValueConstraint[1..]);

    /// <summary>The local name of the first element whose declaration differs from an earlier one of its name, or null.</summary>
    private static string? InconsistentElement(Particle particle, Dictionary<XmlQualifiedName, Particle> declared)
    {
        if (particle.Kind == ParticleKind.Element)
        {
            if (!declared.TryGetValue(particle.Name!, out var earlier))
            {
                declared[particle.Name!] = particle;
                return null;
            }
            return ReferenceEquals(earlier.Declaration, particle.Declaration)
                || (ReferenceEquals(earlier.Type, particle.Type) && earlier.Nillable == particle.Nillable && SameConstraint(earlier, particle))
                ? null
                : particle.Name!.Name;
        }
        foreach (var child in particle.Children)
        {
            if (InconsistentElement(child, declared) is { } name)
                return name;
        }
        return null;
    }

    /// <summary>
    /// Whether the content model violates unique particle attribution: some
    /// point of a match where two different particles could take the same
    /// element. Built as a Glushkov automaton over the particles, each
    /// occurrence range unfolded far enough to show every overlap a count can
    /// make (two required copies, then two optional or a repetition).
    /// </summary>
    private static bool IsAmbiguous(Particle particle)
    {
        var budget = PositionBudget;
        if (Fragment.Of(particle, ref budget) is not { } root)
            return false;
        if (Conflicts(root.First))
            return true;
        foreach (var position in root.Positions)
        {
            if (Conflicts(position.Follow))
                return true;
        }
        return false;
    }

    private static bool Conflicts(List<Position> candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (!ReferenceEquals(candidates[i].Leaf, candidates[j].Leaf) && Overlaps(candidates[i].Leaf, candidates[j].Leaf))
                    return true;
            }
        }
        return false;
    }

    private static bool Overlaps(Particle a, Particle b) => (a.Kind, b.Kind) switch
    {
        (ParticleKind.Element, ParticleKind.Element) => a.Names.Exists(b.Names.Contains),
        (ParticleKind.Element, _) => a.Names.Exists(name => Admits(b, name.Namespace)),
        (_, ParticleKind.Element) => b.Names.Exists(name => Admits(a, name.Namespace)),
        _ => a.Wildcard is null || b.Wildcard is null
            || (a.ExcludedNamespace is not null && b.ExcludedNamespace is not null)
            || (a.ExcludedNamespace is { } excludedA ? b.Wildcard.Exists(ns => ns != excludedA && ns.Length != 0)
                : b.ExcludedNamespace is { } excludedB ? a.Wildcard.Exists(ns => ns != excludedB && ns.Length != 0)
                : a.Wildcard.Exists(b.Wildcard.Contains)),
    };

    /// <summary>
    /// Whether <paramref name="derived"/>'s namespaces are a subset of
    /// <paramref name="baseWildcard"/>'s and its processing no laxer.
    /// </summary>
    private static bool WildcardSubset(Particle derived, Particle baseWildcard)
    {
        if (derived.ProcessContents < baseWildcard.ProcessContents)
            return false;
        if (baseWildcard.Wildcard is null)
            return true;
        if (derived.Wildcard is null)
            return false;
        if (derived.ExcludedNamespace is { } excluded)
            return baseWildcard.ExcludedNamespace == excluded;
        return derived.Wildcard.TrueForAll(ns => Admits(baseWildcard, ns));
    }

    /// <summary>Whether a wildcard takes an element of <paramref name="ns"/>; <c>##other</c> takes neither the target namespace nor none.</summary>
    private static bool Admits(Particle wildcard, string ns) =>
        wildcard.Wildcard is null
        || (wildcard.ExcludedNamespace is { } excluded ? ns != excluded && ns.Length != 0 : wildcard.Wildcard.Contains(ns));

    /// <summary>One occurrence of a particle in the unfolded content model, with the positions that may follow it.</summary>
    private sealed class Position(Particle leaf)
    {
        public readonly Particle Leaf = leaf;
        public readonly List<Position> Follow = [];
    }

    /// <summary>A Glushkov fragment: whether it matches nothing, the positions it starts and ends with, and every position in it.</summary>
    private sealed class Fragment
    {
        public bool Nullable = true;
        public readonly List<Position> First = [];
        public readonly List<Position> Last = [];
        public readonly List<Position> Positions = [];

        /// <summary>The fragment for <paramref name="particle"/>'s occurrence range, or null once <paramref name="budget"/> runs out.</summary>
        public static Fragment? Of(Particle particle, ref int budget)
        {
            var required = Math.Min(particle.Min, 2);
            var optional = particle.Max == int.MaxValue ? -1 : Math.Min(particle.Max - particle.Min, 2);
            var result = new Fragment();
            for (var copy = 0; copy < required; copy++)
            {
                if (Body(particle, ref budget) is not { } body)
                    return null;
                result = Sequence(result, body);
            }
            if (optional < 0)
            {
                if (Body(particle, ref budget) is not { } body)
                    return null;
                foreach (var last in body.Last)
                    last.Follow.AddRange(body.First);
                body.Nullable = true;
                result = Sequence(result, body);
            }
            for (var copy = 0; copy < optional; copy++)
            {
                if (Body(particle, ref budget) is not { } body)
                    return null;
                body.Nullable = true;
                result = Sequence(result, body);
            }
            return result;
        }

        private static Fragment? Body(Particle particle, ref int budget)
        {
            var fragment = new Fragment();
            switch (particle.Kind)
            {
                case ParticleKind.Any or ParticleKind.Element:
                    if (--budget < 0)
                        return null;
                    var position = new Position(particle);
                    fragment.Nullable = false;
                    fragment.First.Add(position);
                    fragment.Last.Add(position);
                    fragment.Positions.Add(position);
                    return fragment;
                case ParticleKind.Sequence:
                    foreach (var child in particle.Children)
                    {
                        if (Of(child, ref budget) is not { } part)
                            return null;
                        fragment = Sequence(fragment, part);
                    }
                    return fragment;
                default:
                    // A choice takes one branch; an all group takes its
                    // members in any order, which for ambiguity is a repeated
                    // choice among them.
                    fragment.Nullable = particle.Children.Count == 0;
                    foreach (var child in particle.Children)
                    {
                        if (Of(child, ref budget) is not { } part)
                            return null;
                        fragment.Nullable |= part.Nullable;
                        fragment.First.AddRange(part.First);
                        fragment.Last.AddRange(part.Last);
                        fragment.Positions.AddRange(part.Positions);
                    }
                    if (particle.Kind == ParticleKind.All)
                    {
                        foreach (var last in fragment.Last)
                            last.Follow.AddRange(fragment.First);
                        fragment.Nullable = true;
                    }
                    return fragment;
            }
        }

        private static Fragment Sequence(Fragment left, Fragment right)
        {
            foreach (var last in left.Last)
                last.Follow.AddRange(right.First);
            var result = new Fragment { Nullable = left.Nullable && right.Nullable };
            result.First.AddRange(left.First);
            if (left.Nullable)
                result.First.AddRange(right.First);
            result.Last.AddRange(right.Last);
            if (right.Nullable)
                result.Last.AddRange(left.Last);
            result.Positions.AddRange(left.Positions);
            result.Positions.AddRange(right.Positions);
            return result;
        }
    }

    /// <summary>
    /// A complex-content restriction's particle against its base's: content
    /// emptier or fuller than the base allows (Msg 6991), then the first pair
    /// that doesn't restrict — an occurrence range wider than the base's
    /// (Msg 6980), an element of another type (Msg 6979 state 4) or left over
    /// (Msg 6979 state 1), and a model group that maps onto none (Msg 6990).
    /// Shapes the mapping here doesn't cover are accepted.
    /// </summary>
    private static void RejectParticleRestriction(ComplexXsdType type, Particle? derived, Particle? baseParticle)
    {
        if (IsEmpty(baseParticle))
        {
            if (!IsEmpty(derived))
                throw SimulatedSqlException.XmlSchemaInvalidRestriction(type.Name, 6991);
            return;
        }
        if (IsEmpty(derived))
        {
            if (!Emptiable(baseParticle))
                throw SimulatedSqlException.XmlSchemaInvalidRestriction(type.Name, 6991);
            return;
        }
        if (Restricts(Unwrap(derived!), Unwrap(baseParticle!)) is { } failure)
            throw SimulatedSqlException.XmlSchemaInvalidRestriction(type.Name, failure.Number, failure.State);
    }

    /// <summary>A model group of one particle occurring once is that particle.</summary>
    private static Particle Unwrap(Particle particle)
    {
        while (particle.Kind is ParticleKind.Choice or ParticleKind.Sequence && particle is { Min: 1, Max: 1, Children.Count: 1 })
            particle = particle.Children[0];
        return particle;
    }

    /// <summary>Why <paramref name="derived"/> doesn't restrict <paramref name="baseParticle"/>, or null when it does or the shape isn't covered.</summary>
    private static RestrictionFailure? Restricts(Particle derived, Particle baseParticle)
    {
        var widens = derived.Min < baseParticle.Min || derived.Max > baseParticle.Max;
        switch (derived.Kind, baseParticle.Kind)
        {
            case (ParticleKind.Element, ParticleKind.Element):
                if (derived.Name != baseParticle.Name)
                    return new(6979, 1, nameMismatch: true);
                if (widens)
                    return new(6980, 1, nameMismatch: false);
                if (derived.Nillable && !baseParticle.Nillable)
                    return new(6981, 1, nameMismatch: false);
                if (baseParticle.ValueConstraint is ['f', ..] && (derived.ValueConstraint is not ['f', ..] || !SameConstraint(derived, baseParticle)))
                    return new(6982, 1, nameMismatch: false);
                return derived.Type is not null && baseParticle.Type is not null && !IsDerived(derived.Type, baseParticle.Type) ? new(6979, 4, nameMismatch: false) : null;
            case (ParticleKind.Element, ParticleKind.Any):
                if (!Admits(baseParticle, derived.Name!.Namespace))
                    return new(6984, 1, nameMismatch: true);
                return widens ? new(6980, 1, nameMismatch: false) : null;
            case (ParticleKind.Any, ParticleKind.Any):
                if (widens)
                    return new(6980, 1, nameMismatch: false);
                return WildcardSubset(derived, baseParticle) ? null : new(6985, 1, nameMismatch: false);
            case (ParticleKind.Element, ParticleKind.Choice or ParticleKind.Sequence):
                var wrapped = new Particle(baseParticle.Kind, 1, 1);
                wrapped.Children.Add(derived);
                return Restricts(wrapped, baseParticle);
            case (ParticleKind.Choice or ParticleKind.Sequence or ParticleKind.All, ParticleKind.Element):
                return new(6979, 1, nameMismatch: false);
            case (ParticleKind.Sequence, ParticleKind.Sequence) or (ParticleKind.All, ParticleKind.All):
                if (widens)
                    return new(6980, 1, nameMismatch: false);
                var next = 0;
                foreach (var baseChild in baseParticle.Children)
                {
                    if (next < derived.Children.Count)
                    {
                        var failure = Restricts(Unwrap(derived.Children[next]), Unwrap(baseChild));
                        if (failure is null)
                        {
                            next++;
                            continue;
                        }
                        if (!failure.NameMismatch)
                            return failure;
                    }
                    if (!Emptiable(baseChild))
                        return new(6990, 1, nameMismatch: false);
                }
                return next < derived.Children.Count ? new(6979, 1, nameMismatch: false) : null;
            case (ParticleKind.Choice, ParticleKind.Choice):
                if (widens)
                    return new(6980, 1, nameMismatch: false);
                var from = 0;
                foreach (var derivedChild in derived.Children)
                {
                    var matched = false;
                    while (from < baseParticle.Children.Count && !matched)
                        matched = Restricts(Unwrap(derivedChild), Unwrap(baseParticle.Children[from++])) is null;
                    if (!matched)
                        return new(6990, 2, nameMismatch: false);
                }
                return null;
            default:
                return null;
        }
    }

    private sealed class RestrictionFailure(int number, byte state, bool nameMismatch)
    {
        public readonly int Number = number;
        public readonly byte State = state;
        public readonly bool NameMismatch = nameMismatch;
    }
}

internal enum ParticleKind
{
    All,
    Any,
    Choice,
    Element,
    Sequence,
}

/// <summary>One particle of a content model: an element, a wildcard or a model group, with its occurrence range.</summary>
internal sealed class Particle(ParticleKind kind, int min, int max)
{
    public readonly ParticleKind Kind = kind;
    public readonly int Min = min;

    /// <summary>The upper bound, <see cref="int.MaxValue"/> for unbounded.</summary>
    public readonly int Max = max;

    public readonly List<Particle> Children = [];

    /// <summary>An element's name.</summary>
    public XmlQualifiedName? Name;

    /// <summary>The names an element particle takes: its own and its substitution group's.</summary>
    public readonly List<XmlQualifiedName> Names = [];

    /// <summary>The declaration an element particle uses, a global one for a reference.</summary>
    public XsdNode? Declaration;

    public XsdType? Type;
    public bool Nillable;

    /// <summary>An element's default (<c>d…</c>) or fixed (<c>f…</c>) value.</summary>
    public string? ValueConstraint;

    /// <summary>A wildcard's namespaces, null for <c>##any</c>; empty with <see cref="ExcludedNamespace"/> set for <c>##other</c>.</summary>
    public List<string>? Wildcard;

    public string? ExcludedNamespace;

    /// <summary>A wildcard's <c>processContents</c>: 0 strict, 1 lax, 2 skip.</summary>
    public int ProcessContents;

    /// <summary>Whether the particle is a group reference's copy of the group's.</summary>
    public bool ViaReference;

    /// <summary>
    /// This particle as another reference to its group takes it: a fresh
    /// copy of the whole tree under the reference's occurrence range, so each
    /// reference's particles are particles of their own.
    /// </summary>
    public Particle Copy(int min, int max)
    {
        var copy = new Particle(this.Kind, min, max)
        {
            Name = this.Name,
            Declaration = this.Declaration,
            Type = this.Type,
            Nillable = this.Nillable,
            ValueConstraint = this.ValueConstraint,
            Wildcard = this.Wildcard,
            ExcludedNamespace = this.ExcludedNamespace,
            ProcessContents = this.ProcessContents,
        };
        copy.Names.AddRange(this.Names);
        foreach (var child in this.Children)
            copy.Children.Add(child.Copy(child.Min, child.Max));
        return copy;
    }
}
