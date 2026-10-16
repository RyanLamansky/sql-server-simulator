namespace SqlServerSimulator.Schemas;

// Where each XSD element may sit among its parent's children.
internal sealed partial class XmlSchemaCompiler
{
    private static readonly string[] Particles = ["all", "choice", "group", "sequence"];
    private static readonly string[] AttributeDeclarations = ["attribute", "attributeGroup"];
    private static readonly string[] AttributeWildcard = ["anyAttribute"];
    private static readonly string[] Facets =
        ["enumeration", "fractionDigits", "length", "maxExclusive", "maxInclusive", "maxLength", "minExclusive", "minInclusive", "minLength", "pattern", "totalDigits", "whiteSpace"];

    /// <summary>
    /// The children one XSD element takes, after an optional leading
    /// <c>annotation</c>, as the schema for schemas orders them: a run of slots,
    /// each a set of names taken once or repeatedly, read left to right
    /// (probed 2026-10-16 against SQL Server 2025). A child its parent doesn't
    /// take where it stands is Msg 2297 — located at the parent when the
    /// parent is an <c>all</c> — and real names a few cases apart: a second
    /// <c>annotation</c> (Msg 2384) or one after other content (Msg 2388), a
    /// second inline type or one beside a <c>type</c> attribute (Msg 2305), a
    /// simple type's restriction naming a base and declaring one inline
    /// (Msg 2358), and a facet after a simple-content restriction's attributes
    /// (Msg 2321).
    /// </summary>
    private sealed class Placement
    {
        private readonly XsdNode parent;
        private readonly (string[] Names, bool Repeats)[] slots;

        /// <summary>
        /// Whether a child taking the first slot ends the parent's content, as
        /// a complex type's <c>simpleContent</c> or <c>complexContent</c> does.
        /// </summary>
        private readonly bool firstSlotIsWhole;

        private int slot = -1;
        private bool slotTaken;
        private bool annotationSeen;
        private bool contentSeen;
        private bool whole;

        /// <summary>Whether a child other than an <c>annotation</c> was admitted, which Msg 2327 asks of a type or content element.</summary>
        public bool HoldsDefinition;

        public Placement(XsdNode parent)
        {
            this.parent = parent;
            var element = parent.Element;
            var isReference = element.Attribute("ref") is not null && parent.Parent?.Parent is not null;
            (string[], bool) Once(params string[] names) => (names, false);
            (string[], bool) Many(params string[] names) => (names, true);
            this.slots = parent.Local switch
            {
                "all" => [Many("element")],
                "annotation" => [Many("appinfo", "documentation")],
                "attribute" when !isReference => [Once("simpleType")],
                "attributeGroup" when !isReference => [Many(AttributeDeclarations), Once(AttributeWildcard)],
                "choice" or "sequence" => [Many("any", "choice", "element", "group", "sequence")],
                "complexContent" or "simpleContent" => [Once("extension", "restriction")],
                "complexType" => [Once([.. Particles, "complexContent", "simpleContent"]), Many(AttributeDeclarations), Once(AttributeWildcard)],
                "element" when !isReference => [Once("complexType", "simpleType"), Many("key", "keyref", "unique")],
                "extension" when parent.Parent!.Local == "simpleContent" => [Many(AttributeDeclarations), Once(AttributeWildcard)],
                "extension" => [Once(Particles), Many(AttributeDeclarations), Once(AttributeWildcard)],
                "group" when !isReference => [Once("all", "choice", "sequence")],
                "list" => [Once("simpleType")],
                "restriction" when parent.Parent!.Local == "simpleType" => [Once("simpleType"), Many(Facets)],
                "restriction" when parent.Parent!.Local == "simpleContent" => [Once("simpleType"), Many(Facets), Many(AttributeDeclarations), Once(AttributeWildcard)],
                "restriction" => [Once(Particles), Many(AttributeDeclarations), Once(AttributeWildcard)],
                "simpleType" => [Once("list", "restriction", "union")],
                "union" => [Many("simpleType")],
                _ => [],
            };
            this.firstSlotIsWhole = parent.Local == "complexType";
        }

        /// <summary>Admits <paramref name="child"/> after the children already admitted, or raises real's refusal of its place.</summary>
        public void Admit(XsdNode child)
        {
            var local = child.Local;
            if (local == "annotation" && this.parent.Local is not ("annotation" or "appinfo" or "documentation"))
            {
                if (this.annotationSeen)
                    throw SimulatedSqlException.XmlSchemaAnnotationRepeated(this.parent.Local);
                if (this.contentSeen)
                    throw SimulatedSqlException.XmlSchemaAnnotationNotFirst(this.parent.Local);
                this.annotationSeen = true;
                return;
            }

            var parentLocal = this.parent.Local;
            if (local == "anyAttribute" && this.slotTaken && Array.IndexOf(this.slots[this.slot].Names, local) >= 0)
                throw SimulatedSqlException.XmlSchemaElementRepeated(local, parentLocal);
            if (parentLocal is "attribute" or "element" && local is "complexType" or "simpleType"
                && (this.parent.Element.Attribute("type") is not null || (this.slotTaken && this.slot == 0)))
            {
                throw SimulatedSqlException.XmlSchemaTypeSpecifiedTwice(child.Location);
            }
            if (parentLocal == "list" && local == "simpleType" && this.parent.Element.Attribute("itemType") is not null)
                throw SimulatedSqlException.XmlSchemaElementNotValid(local, child.Location);

            var found = -1;
            if (!this.whole)
            {
                for (var candidate = Math.Max(this.slot, 0); candidate < this.slots.Length; candidate++)
                {
                    if (Array.IndexOf(this.slots[candidate].Names, local) < 0)
                        continue;
                    if (candidate == this.slot && this.slotTaken && !this.slots[candidate].Repeats)
                        continue;
                    found = candidate;
                    break;
                }
            }
            if (found < 0)
            {
                if (IsFacet(local) && parentLocal == "restriction" && this.parent.Parent!.Local == "simpleContent" && this.slot >= 2)
                    throw SimulatedSqlException.XmlSchemaFacetAfterAttributes(local, child.Location);
                throw SimulatedSqlException.XmlSchemaElementNotValid(local, parentLocal == "all" ? this.parent.Location : child.Location);
            }

            if (parentLocal == "restriction" && local == "simpleType" && this.parent.Parent!.Local is "simpleContent" or "simpleType" && this.parent.Element.Attribute("base") is not null)
                throw SimulatedSqlException.XmlSchemaBaseAndInlineType(child.Location);

            this.slotTaken = true;
            this.slot = found;
            this.contentSeen = true;
            this.HoldsDefinition = true;
            if (found == 0 && this.firstSlotIsWhole && local is "complexContent" or "simpleContent")
                this.whole = true;
        }
    }
}
