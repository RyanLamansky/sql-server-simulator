using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Validates an <c>xml</c> instance against the schema collection its target
/// is bound to, and returns the instance <b>re-serialized in canonical form</b>
/// — the two halves of what real does on every write to an
/// <c>xml(&lt;collection&gt;)</c> column, variable or CAST target.
/// </summary>
/// <remarks>
/// <para>
/// The walk is the simulator's own rather than a pass of .NET's validating
/// reader, because real's diagnostics distinguish cases .NET separates only by
/// English prose: an out-of-order child and an over-occurring one are both
/// "invalid child element" there, where real splits them into <b>Msg 6965</b>
/// and <b>Msg 6923</b>. Compilation is still .NET's — the post-compilation
/// infoset (<c>ElementSchemaType</c>, <c>ContentTypeParticle</c>,
/// <c>AttributeUses</c>) is exactly the resolved shape this walk needs, and
/// getting it right by hand would be the whole of XSD.
/// </para>
/// <para>
/// The content-model matcher is a straightforward recursive particle walk over
/// the children in document order, which is enough for the deterministic
/// content models XSD requires. Where it can't place a particle it lets the
/// children through rather than refusing them — the direction that can only
/// lose fidelity, never reject an instance real accepts. Every message and
/// location string was probed against SQL Server 2025 on 2026-08-08.
/// </para>
/// </remarks>
internal static class XmlSchemaValidation
{
    /// <summary>
    /// Validates <paramref name="xmlText"/> against <paramref name="collection"/>
    /// and returns its canonical serialization, raising real's own
    /// <c>XML Validation:</c> error for the first failure in document order.
    /// Returns the text unchanged when the collection's XSD doesn't compile or
    /// the text isn't parseable XML — the untyped behavior, since the parse
    /// error belongs to whoever stored the value.
    /// </summary>
    internal static string ValidateAndNormalize(Schemas.XmlSchemaCollection collection, string xmlText)
    {
        if (collection.GetCompiledSchemas() is not { } schemas)
            return xmlText;

        XDocument document;
        try
        {
            // CONTENT-typed, like every other xml value here: several top-level
            // elements and top-level text are both legal, so the instance is
            // read as a fragment under a synthetic root.
            document = XDocument.Parse($"<{FragmentRoot}>{xmlText}</{FragmentRoot}>", LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return xmlText;
        }

        var changed = false;
        foreach (var element in document.Root!.Elements())
        {
            var declaration = GlobalElement(schemas, element.Name)
                ?? throw SimulatedSqlException.XmlValidationDeclarationNotFound(QualifiedName(element.Name), LocationOf(element));
            changed |= ValidateElement(element, declaration.ElementSchemaType, schemas);
        }

        return changed ? Reserialize(document) : xmlText;
    }

    /// <summary>
    /// The synthetic wrapper the CONTENT-typed instance is parsed under; it is
    /// stripped again on the way out and never reaches a diagnostic, since a
    /// location trail is built from the instance's own top-level elements.
    /// </summary>
    private const string FragmentRoot = "sss-typed-xml-root";

    /// <summary>
    /// Validates one element against <paramref name="type"/>, canonicalizing
    /// every simple value it or its descendants carry. Returns whether any text
    /// changed, so an instance real would store verbatim keeps the bytes it
    /// arrived with.
    /// </summary>
    private static bool ValidateElement(XElement element, XmlSchemaType? type, XmlSchemaSet schemas)
    {
        // No type means nothing placed this element — a wildcard matched it and
        // no global declaration answers its name. Real leaves such a subtree
        // alone, and so must this: refusing its attributes would reject the
        // instance real accepts.
        if (type is null)
            return false;

        var changed = ValidateAttributes(element, type);
        switch (type)
        {
            case XmlSchemaSimpleType simple:
                if (element.HasElements)
                {
                    throw SimulatedSqlException.XmlValidationUnexpectedElement(
                        string.Empty, QualifiedName(element.Elements().First().Name), LocationOf(element.Elements().First()));
                }

                return NormalizeSimpleValue(element, simple) || changed;

            case XmlSchemaComplexType complex:
                return ValidateComplexContent(element, complex, schemas) || changed;

            default:
                return changed;
        }
    }

    /// <summary>
    /// Checks an element's attributes against its type's attribute uses and
    /// canonicalizes each declared one's value. An element whose type declares
    /// no attributes at all still rejects one, which is real's Msg 6905.
    /// </summary>
    private static bool ValidateAttributes(XElement element, XmlSchemaType? type)
    {
        var uses = (type as XmlSchemaComplexType)?.AttributeUses;
        var wildcard = (type as XmlSchemaComplexType)?.AttributeWildcard;
        var changed = false;
        foreach (var attribute in element.Attributes())
        {
            // A namespace declaration is not an attribute to XSD.
            if (attribute.IsNamespaceDeclaration)
                continue;
            if (FindAttribute(uses, attribute.Name) is not { } declared)
            {
                if (wildcard is not null)
                    continue;
                throw SimulatedSqlException.XmlValidationAttributeNotPermitted(
                    attribute.Name.LocalName, $"{LocationOf(element)}/@*:{attribute.Name.LocalName}");
            }

            if (declared.AttributeSchemaType is not { } attributeType)
                continue;
            var canonical = CanonicalValue(attributeType, attribute.Value, element, attribute.Name.LocalName);
            if (!string.Equals(canonical, attribute.Value, StringComparison.Ordinal))
            {
                attribute.Value = canonical;
                changed = true;
            }
        }

        foreach (var use in EnumerateUses(uses))
        {
            if (use.Use == XmlSchemaUse.Required && element.Attribute(NameOf(use)) is null)
                throw SimulatedSqlException.XmlValidationRequiredAttributeMissing(use.Name ?? use.QualifiedName.Name, LocationOf(element));
        }

        return changed;
    }

    private static IEnumerable<XmlSchemaAttribute> EnumerateUses(XmlSchemaObjectTable? uses)
    {
        if (uses is null)
            yield break;
        foreach (var entry in uses)
        {
            if (entry is System.Collections.DictionaryEntry { Value: XmlSchemaAttribute attribute })
                yield return attribute;
        }
    }

    private static XmlSchemaAttribute? FindAttribute(XmlSchemaObjectTable? uses, XName name)
    {
        foreach (var attribute in EnumerateUses(uses))
        {
            if (NameOf(attribute) == name)
                return attribute;
        }

        return null;
    }

    private static XName NameOf(XmlSchemaAttribute attribute) =>
        attribute.QualifiedName.Namespace.Length == 0
            ? XName.Get(attribute.QualifiedName.Name)
            : XName.Get(attribute.QualifiedName.Name, attribute.QualifiedName.Namespace);

    /// <summary>
    /// Matches an element's children against its complex type's content
    /// particle, recursing into each child with the declaration the match
    /// bound it to. Simple content (an <c>xsd:extension</c> over a simple type)
    /// canonicalizes the element's own text instead.
    /// </summary>
    private static bool ValidateComplexContent(XElement element, XmlSchemaComplexType complex, XmlSchemaSet schemas)
    {
        // Simple content — an `xsd:extension` / `xsd:restriction` over a simple
        // type — carries a value of its own and no children.
        if (complex.ContentType == XmlSchemaContentType.TextOnly)
            return !element.HasElements && NormalizeSimpleValue(element, SimpleTypeFor(complex));

        if (complex.ContentType == XmlSchemaContentType.Empty && element.HasElements)
        {
            var first = element.Elements().First();
            throw SimulatedSqlException.XmlValidationUnexpectedElement(string.Empty, QualifiedName(first.Name), LocationOf(first));
        }

        if (complex.ContentType == XmlSchemaContentType.ElementOnly && HasText(element))
            throw SimulatedSqlException.XmlValidationTextNotAllowed(LocationOf(element));

        var children = element.Elements().ToArray();
        if (complex.ContentTypeParticle is not { } particle)
            return false;

        var cursor = new ParticleCursor(children);
        Match(particle, cursor, element);
        if (cursor.Index < children.Length)
        {
            // Real splits the leftover on whether the model could still have
            // taken anything here: a particle that was willing and got the
            // wrong name is Msg 6965 naming it, while a model with nothing left
            // to offer — every particle at its maxOccurs — is Msg 6923.
            // `<v><nope/></v>` against `dec?` is the first, `<v><dec/><nope/></v>`
            // the second (probed 2026-08-08).
            var stray = children[cursor.Index];
            var expected = cursor.ExpectedHere();
            throw expected.Length == 0
                ? SimulatedSqlException.XmlValidationTooManyOccurrences(QualifiedName(stray.Name), LocationOf(stray))
                : SimulatedSqlException.XmlValidationUnexpectedElement(
                    expected, QualifiedName(stray.Name), LocationOf(stray));
        }

        var changed = false;
        foreach (var (child, declaration, wildcard) in cursor.Bound)
        {
            // A wildcard binds no declaration of its own, so the child is
            // resolved globally — real's `strict` processing, and what types
            // AdventureWorks' `ContactRecord` inside the `xsd:any` its
            // `AdditionalContactInfo` declares. Under `strict` a name nothing
            // declares is Msg 6913, exactly as an undeclared root is; `lax` and
            // `skip` let it through unvalidated.
            var resolved = declaration ?? GlobalElement(schemas, child.Name);
            if (resolved is null
                && wildcard is not null
                && wildcard.ProcessContents is XmlSchemaContentProcessing.Strict or XmlSchemaContentProcessing.None)
            {
                throw SimulatedSqlException.XmlValidationDeclarationNotFound(QualifiedName(child.Name), LocationOf(child));
            }

            changed |= ValidateElement(child, resolved?.ElementSchemaType, schemas);
        }

        return changed;
    }

    /// <summary>
    /// The simple type a text-only complex type ultimately holds its value as,
    /// found by climbing the base chain — an <c>xsd:extension base="xs:decimal"</c>
    /// declares a complex type whose value is still a decimal.
    /// </summary>
    private static XmlSchemaSimpleType? SimpleTypeFor(XmlSchemaComplexType complex)
    {
        for (XmlSchemaType? type = complex; type is not null; type = type.BaseXmlSchemaType)
        {
            if (type is XmlSchemaSimpleType simple)
                return simple;
            if (ReferenceEquals(type, type.BaseXmlSchemaType))
                break;
        }

        return null;
    }

    /// <summary>Whether the element carries character data of its own (a comment or PI doesn't count).</summary>
    private static bool HasText(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text && text.Value.AsSpan().Trim().Length > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Walks <paramref name="particle"/> against the cursor's remaining
    /// children, consuming what it can and recording the declaration each
    /// consumed child bound to. Raises only where a required particle can't be
    /// satisfied; anything left over afterwards is the caller's to report, so
    /// the two shapes real distinguishes stay distinguishable.
    /// </summary>
    private static void Match(XmlSchemaParticle particle, ParticleCursor cursor, XElement parent)
    {
        // A type with empty content carries the sentinel particle, which admits
        // nothing and requires nothing.
        if (particle is not (XmlSchemaElement or XmlSchemaAny or XmlSchemaGroupBase))
            return;

        var occurrences = 0;
        while (occurrences < particle.MaxOccurs)
        {
            var before = cursor.Index;
            bool matched;
            try
            {
                matched = MatchOnce(particle, cursor, parent);
            }
            catch (SimulatedSqlException) when (occurrences >= particle.MinOccurs)
            {
                // A repetition past the required minimum that doesn't fit just
                // ends the repetition — the children it half-consumed go back.
                cursor.Rewind(before);
                break;
            }

            if (!matched)
            {
                cursor.Rewind(before);
                break;
            }

            occurrences++;

            // An all-optional group matches without consuming anything, which
            // satisfies its own minimum and must not then loop forever.
            if (cursor.Index == before)
                break;
        }

        if (occurrences >= particle.MinOccurs)
            return;

        // Real splits the two ways a required particle goes unsatisfied: a
        // child is sitting there that the model didn't want here (Msg 6965,
        // naming both sides and the child's own location), against the parent
        // simply ending too early (Msg 6908, naming the parent).
        throw cursor.Index < cursor.Children.Length
            ? SimulatedSqlException.XmlValidationUnexpectedElement(
                ExpectedNamesOf(particle),
                QualifiedName(cursor.Children[cursor.Index].Name),
                LocationOf(cursor.Children[cursor.Index]))
            : SimulatedSqlException.XmlValidationIncompleteContent(ExpectedNamesOf(particle), LocationOf(parent));
    }

    /// <summary>
    /// One occurrence of <paramref name="particle"/>. Answers false when the
    /// particle doesn't apply here at all (leaving the cursor for the caller to
    /// rewind), and raises when it applies but its own content is wrong.
    /// </summary>
    private static bool MatchOnce(XmlSchemaParticle particle, ParticleCursor cursor, XElement parent)
    {
        switch (particle)
        {
            case XmlSchemaElement declaration:
                var wanted = NameOf(declaration);
                if (cursor.Index >= cursor.Children.Length || cursor.Children[cursor.Index].Name != wanted)
                {
                    // What the model would have taken at this position, which
                    // is what real lists when it reports the child it got.
                    cursor.NoteExpected(QualifiedName(wanted));
                    return false;
                }

                cursor.Bind(declaration);
                return true;

            case XmlSchemaAny wildcard:
                if (cursor.Index >= cursor.Children.Length
                    || !WildcardAdmits(wildcard, cursor.Children[cursor.Index].Name.NamespaceName, parent.Name.NamespaceName))
                {
                    // Real writes a wildcard into its expected list as the
                    // namespaces it names with a `*` local part.
                    foreach (var offered in WildcardNames(wildcard, parent.Name.NamespaceName))
                        cursor.NoteExpected(offered);
                    return false;
                }

                cursor.Bind(null, wildcard);
                return true;

            case XmlSchemaSequence sequence:
                // Every item in turn; one that can't meet its own minimum
                // raises out of here, which is what reports the offending
                // child rather than silently accepting the sequence.
                foreach (var item in sequence.Items)
                {
                    if (item is XmlSchemaParticle inner)
                        Match(inner, cursor, parent);
                }

                return true;

            case XmlSchemaChoice choice:
                foreach (var item in choice.Items)
                {
                    if (item is not XmlSchemaParticle inner)
                        continue;
                    var before = cursor.Index;
                    try
                    {
                        Match(inner, cursor, parent);
                    }
                    catch (SimulatedSqlException)
                    {
                        cursor.Rewind(before);
                        continue;
                    }

                    if (cursor.Index > before)
                        return true;
                }

                // No branch took anything. That satisfies the choice only when
                // some branch was willing to match nothing at all.
                return AnyBranchIsOptional(choice);

            case XmlSchemaAll all:
                var matchedAny = false;
                bool progressed;
                do
                {
                    progressed = false;
                    foreach (var item in all.Items)
                    {
                        if (item is not XmlSchemaParticle inner)
                            continue;
                        var before = cursor.Index;
                        if (MatchOnce(inner, cursor, parent) && cursor.Index > before)
                            progressed = matchedAny = true;
                    }
                }
                while (progressed);
                return matchedAny || AllItemsAreOptional(all);

            default:
                return false;
        }
    }

    private static bool AnyBranchIsOptional(XmlSchemaChoice choice)
    {
        if (choice.MinOccurs == 0)
            return true;
        foreach (var item in choice.Items)
        {
            if (item is XmlSchemaParticle { MinOccurs: 0 })
                return true;
        }

        return false;
    }

    private static bool AllItemsAreOptional(XmlSchemaAll all)
    {
        foreach (var item in all.Items)
        {
            if (item is XmlSchemaParticle { MinOccurs: > 0 })
                return false;
        }

        return true;
    }

    /// <summary>
    /// How <paramref name="wildcard"/> reads in real's expected-element list:
    /// one <c>{uri}*</c> per namespace it names, and a bare <c>*</c> for the
    /// forms that name no single URI.
    /// </summary>
    private static IEnumerable<string> WildcardNames(XmlSchemaAny wildcard, string targetNamespace)
    {
        var spec = wildcard.Namespace;
        if (string.IsNullOrWhiteSpace(spec) || spec == "##any" || spec == "##other")
        {
            yield return "*";
            yield break;
        }

        foreach (var entry in spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry switch
            {
                "##local" => "*",
                "##targetNamespace" => targetNamespace.Length == 0 ? "*" : $"{{{targetNamespace}}}*",
                _ => $"{{{entry}}}*",
            };
        }
    }

    /// <summary>
    /// Whether <paramref name="wildcard"/> admits an element in
    /// <paramref name="candidate"/>'s namespace. The <c>namespace</c> attribute
    /// is a whitespace-separated list of URIs and the four keywords, with an
    /// empty entry meaning no namespace.
    /// </summary>
    private static bool WildcardAdmits(XmlSchemaAny wildcard, string candidate, string targetNamespace)
    {
        var spec = wildcard.Namespace;
        if (string.IsNullOrWhiteSpace(spec) || spec == "##any")
            return true;
        foreach (var entry in spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var admitted = entry switch
            {
                "##local" => candidate.Length == 0,
                "##other" => candidate.Length > 0 && candidate != targetNamespace,
                "##targetNamespace" => candidate == targetNamespace,
                _ => candidate == entry,
            };
            if (admitted)
                return true;
        }

        return false;
    }

    private static XName NameOf(XmlSchemaElement declaration) =>
        declaration.QualifiedName.Namespace.Length == 0
            ? XName.Get(declaration.QualifiedName.Name)
            : XName.Get(declaration.QualifiedName.Name, declaration.QualifiedName.Namespace);

    /// <summary>
    /// The comma-separated element names real lists as expected. A group lists
    /// the names its first required position admits.
    /// </summary>
    private static string ExpectedNamesOf(XmlSchemaParticle particle)
    {
        var names = new List<string>();
        Collect(particle, names);
        return string.Join("', '", names);

        static void Collect(XmlSchemaParticle particle, List<string> into)
        {
            switch (particle)
            {
                case XmlSchemaElement declaration:
                    into.Add(QualifiedName(NameOf(declaration)));
                    break;
                case XmlSchemaGroupBase group:
                    foreach (var item in group.Items)
                    {
                        if (item is XmlSchemaParticle inner)
                            Collect(inner, into);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Canonicalizes an element's own text under <paramref name="simpleType"/>,
    /// returning whether it changed. An element with no simple type behind it
    /// keeps its text.
    /// </summary>
    private static bool NormalizeSimpleValue(XElement element, XmlSchemaSimpleType? simpleType)
    {
        if (simpleType is null)
            return false;
        var raw = element.Value;
        var canonical = CanonicalValue(simpleType, raw, element, attributeName: null);
        if (string.Equals(canonical, raw, StringComparison.Ordinal))
            return false;
        element.SetValue(canonical);
        return true;
    }

    /// <summary>
    /// The canonical text for <paramref name="raw"/> under
    /// <paramref name="type"/>, raising Msg 6926 when the value isn't one the
    /// type admits — which is what carries a facet violation and an
    /// out-of-range integer alike, since <c>ParseValue</c> applies the compiled
    /// facets. <paramref name="attributeName"/> names the attribute the value
    /// came from, or null for the element's own text.
    /// </summary>
    /// <remarks>
    /// The location trail is built only on the failing path. Composing it walks
    /// every ancestor and each one's preceding siblings, so computing it for
    /// every value would make validating a wide document quadratic in its
    /// width — per row, on every write.
    /// </remarks>
    private static string CanonicalValue(XmlSchemaType type, string raw, XElement element, string? attributeName)
    {
        var simpleType = type as XmlSchemaSimpleType ?? (type is XmlSchemaComplexType complex ? SimpleTypeFor(complex) : null);
        if (simpleType?.Datatype is not { } datatype)
            return raw;

        // The end-of-day spelling is rolled first: XSD admits `24:00:00` and
        // real accepts it, but .NET's own parser does not, so it has to become
        // the following midnight before the value is checked.
        var normalized = XsdCanonical.PreParse(datatype, XsdCanonical.ApplyWhitespaceFacet(datatype, raw));
        try
        {
            _ = datatype.ParseValue(normalized, null, null);
        }
        catch (Exception e) when (e is XmlSchemaException or FormatException or OverflowException or ArgumentException)
        {
            var location = attributeName is null
                ? LocationOf(element)
                : $"{LocationOf(element)}/@*:{attributeName}";
            throw SimulatedSqlException.XmlValidationInvalidSimpleTypeValue(raw, location);
        }

        return XsdCanonical.Render(simpleType, normalized);
    }

    private static XmlSchemaElement? GlobalElement(XmlSchemaSet schemas, XName name)
    {
        var qualified = new XmlQualifiedName(name.LocalName, name.NamespaceName);
        return schemas.GlobalElements[qualified] as XmlSchemaElement;
    }

    /// <summary>
    /// Real's location trail for a node: each ancestor written
    /// <c>/*:name[ordinal]</c>, the ordinal counting same-named siblings from
    /// one. The synthetic fragment root contributes nothing.
    /// </summary>
    private static string LocationOf(XElement element)
    {
        var parts = new List<string>();
        for (var current = element; current is not null && current.Name.LocalName != FragmentRoot; current = current.Parent)
        {
            var ordinal = 1;
            for (var sibling = current.PreviousNode; sibling is not null; sibling = sibling.PreviousNode)
            {
                if (sibling is XElement previous && previous.Name == current.Name)
                    ordinal++;
            }

            parts.Add($"/*:{current.Name.LocalName}[{ordinal}]");
        }

        parts.Reverse();
        return string.Concat(parts);
    }

    /// <summary>Real writes a namespaced name <c>{uri}local</c> and a bare one as itself.</summary>
    private static string QualifiedName(XName name) =>
        name.NamespaceName.Length == 0 ? name.LocalName : $"{{{name.NamespaceName}}}{name.LocalName}";

    /// <summary>
    /// Writes the (possibly multi-element) instance back out without the
    /// synthetic root, through the same serializer <c>.modify()</c> uses — real
    /// self-closes an empty element with no space before the slash, which
    /// <see cref="XDocument"/>'s own writer does not.
    /// </summary>
    private static string Reserialize(XDocument document) => Parser.XmlDml.Serialize(document.Root!);

    /// <summary>
    /// The children of one element plus the walk's position in them, together
    /// with the declaration each consumed child bound to — what lets the
    /// recursion report an over-occurring element (a name it already matched)
    /// apart from an unexpected one.
    /// </summary>
    private sealed class ParticleCursor(XElement[] children)
    {
        public readonly XElement[] Children = children;

        public readonly List<(XElement Child, XmlSchemaElement? Declaration, XmlSchemaAny? Wildcard)> Bound = [];

        public int Index;

        /// <summary>
        /// The element names the model was willing to take at the position the
        /// walk stopped at, in the order it offered them — real's
        /// <c>Expected element(s)</c> list. Reset whenever the cursor moves, so
        /// only the final position's offers survive.
        /// </summary>
        private readonly List<string> expectedHere = [];

        private int expectedAt = -1;

        public void Bind(XmlSchemaElement? declaration, XmlSchemaAny? wildcard = null)
        {
            var child = this.Children[this.Index++];
            this.Bound.Add((child, declaration, wildcard));
        }

        public void NoteExpected(string name)
        {
            if (this.expectedAt != this.Index)
            {
                this.expectedAt = this.Index;
                this.expectedHere.Clear();
            }

            if (!this.expectedHere.Contains(name))
                this.expectedHere.Add(name);
        }

        public string ExpectedHere() =>
            this.expectedAt == this.Index ? string.Join("', '", this.expectedHere) : string.Empty;

        public void Rewind(int index)
        {
            if (this.Index != index)
                this.expectedAt = -1;
            while (this.Index > index)
            {
                this.Index--;
                this.Bound.RemoveAt(this.Bound.Count - 1);
            }
        }
    }
}
