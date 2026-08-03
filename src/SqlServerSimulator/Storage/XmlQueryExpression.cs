using System.Globalization;
using System.Xml;
using System.Xml.XPath;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Static cardinality of an XQuery subexpression. SQL Server types a path off
/// its shape alone and refuses a value comparison — or a <c>.value()</c> — whose
/// operand isn't statically at most one item, so the cardinality has to be
/// carried through the compiled tree rather than measured per instance.
/// </summary>
internal enum XmlOccurrence
{
    /// <summary>Exactly one item — a literal, the context item, <c>string(…)</c>.</summary>
    ExactlyOne,

    /// <summary>At most one item — an attribute step, a positionally filtered step.</summary>
    ZeroOrOne,

    /// <summary>Any number of items — an element / <c>text()</c> step.</summary>
    Many,
}

/// <summary>
/// Static item kind of an XQuery subexpression. It decides what a predicate
/// written over it <em>means</em> (numeric = positional, boolean = filter,
/// node = existence, anything else = Msg 2203) and whether a comparison's two
/// operands are type-compatible at all (Msg 2234).
/// </summary>
internal enum XmlStaticKind
{
    /// <summary>Nodes, which atomize to <c>xdt:untypedAtomic</c>.</summary>
    Node,

    /// <summary>Already-atomized untyped values (<c>data(…)</c>).</summary>
    Untyped,

    /// <summary><c>xs:string</c>.</summary>
    String,

    /// <summary><c>xs:integer</c> / <c>xs:decimal</c> / <c>xs:double</c>.</summary>
    Number,

    /// <summary><c>xs:boolean</c>.</summary>
    Boolean,
}

/// <summary>
/// An atomized node value. Untyped XML atomizes to <c>xdt:untypedAtomic</c>,
/// which takes its comparison type from the <em>other</em> operand — the rule
/// that makes <c>[@x=1]</c> a numeric comparison (so <c>"01"</c> matches) while
/// <c>[@x="1"]</c> is a string one (so it doesn't).
/// </summary>
internal sealed class XmlUntypedAtomic(string text)
{
    /// <summary>The node's string value.</summary>
    public readonly string Text = text;

    public override string ToString() => this.Text;
}

/// <summary>
/// The <c>$</c>-variable bindings live for one evaluation of one compiled
/// expression. A compiled tree is shared across rows and sessions, so a
/// binding can't live on the tree; it lives here, reached through the
/// evaluation frame, and each <c>for</c> / <c>let</c> / quantified binding owns
/// a slot the parser assigned it.
/// </summary>
internal sealed class XmlVariableScope
{
    private List<object>?[] slots = [];

    /// <summary>The sequence bound to <paramref name="slot"/>.</summary>
    public List<object> Read(int slot) => this.slots[slot]!;

    /// <summary>Binds <paramref name="value"/> to <paramref name="slot"/>.</summary>
    public void Write(int slot, List<object> value)
    {
        if (slot >= this.slots.Length)
            Array.Resize(ref this.slots, slot + 4);
        this.slots[slot] = value;
    }
}

/// <summary>
/// One evaluation frame: the context item plus its position within the
/// sequence the enclosing step produced, which <c>position()</c> / <c>last()</c>
/// and a positional predicate read, and the variable bindings in scope.
/// </summary>
internal readonly struct XmlQueryFrame(XPathNavigator context, int position, int size, XmlVariableScope? variables = null)
{
    /// <summary>The context item.</summary>
    public readonly XPathNavigator Context = context;

    /// <summary>1-based position of <see cref="Context"/> in its sequence.</summary>
    public readonly int Position = position;

    /// <summary>Length of the sequence <see cref="Context"/> came from.</summary>
    public readonly int Size = size;

    /// <summary>
    /// The bindings in scope, allocated by the outermost binding construct and
    /// shared by every frame below it; null while no variable is bound.
    /// </summary>
    public readonly XmlVariableScope? Variables = variables;
}

/// <summary>
/// A compiled XQuery subexpression. Every node carries the static kind and
/// cardinality SQL Server's own compiler derives, so the diagnostics that are
/// static on real (Msg 2203 / 2234 / 2389) fire while the statement parses
/// rather than per row.
/// </summary>
internal abstract class XmlQueryExpr(XmlStaticKind kind, XmlOccurrence occurrence, string typeName)
{
    /// <summary>Static item kind.</summary>
    public readonly XmlStaticKind Kind = kind;

    /// <summary>Static cardinality.</summary>
    public readonly XmlOccurrence Occurrence = occurrence;

    /// <summary>Atomized type name, without an occurrence indicator.</summary>
    public readonly string TypeName = typeName;

    /// <summary>Appends this expression's result sequence to <paramref name="results"/>.</summary>
    public abstract void Evaluate(in XmlQueryFrame frame, List<object> results);

    /// <summary>Evaluates into a fresh list.</summary>
    public List<object> Evaluate(in XmlQueryFrame frame)
    {
        var results = new List<object>();
        this.Evaluate(frame, results);
        return results;
    }

    /// <summary>
    /// Real's static-type notation for the <em>atomized</em> form — what the
    /// value-comparison and atomic-argument diagnostics quote
    /// (<c>xdt:untypedAtomic *</c>).
    /// </summary>
    public string AtomizedTypeName() => this.TypeName + OccurrenceSuffix(this.Occurrence);

    /// <summary>
    /// Real's static-type notation without atomization — what a diagnostic over
    /// an <c>item()</c>-typed parameter quotes (<c>element(b,xdt:untyped) *</c>).
    /// </summary>
    public string NodeTypeName() => this.NodeTypeBase() + OccurrenceSuffix(this.Occurrence);

    /// <summary>The un-suffixed node-form type name; only paths have one of their own.</summary>
    public virtual string NodeTypeBase() => this.TypeName;

    /// <summary>The occurrence indicator real appends to a static type name.</summary>
    internal static string OccurrenceSuffix(XmlOccurrence occurrence) => occurrence switch
    {
        XmlOccurrence.ExactlyOne => string.Empty,
        XmlOccurrence.ZeroOrOne => " ?",
        _ => " *",
    };

    /// <summary>Cardinality of a path whose two halves have the given cardinalities.</summary>
    internal static XmlOccurrence Combine(XmlOccurrence left, XmlOccurrence right) =>
        left == XmlOccurrence.Many || right == XmlOccurrence.Many
            ? XmlOccurrence.Many
            : left == XmlOccurrence.ZeroOrOne || right == XmlOccurrence.ZeroOrOne
                ? XmlOccurrence.ZeroOrOne
                : XmlOccurrence.ExactlyOne;
}

/// <summary>A literal or otherwise constant single item.</summary>
internal sealed class XmlLiteralExpr(object value, XmlStaticKind kind, string typeName)
    : XmlQueryExpr(kind, XmlOccurrence.ExactlyOne, typeName)
{
    private readonly object value = value;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results) => results.Add(this.value);
}

/// <summary>The context item, <c>.</c>.</summary>
internal sealed class XmlContextItemExpr() : XmlQueryExpr(XmlStaticKind.Node, XmlOccurrence.ExactlyOne, "xdt:untypedAtomic")
{
    public override void Evaluate(in XmlQueryFrame frame, List<object> results) => results.Add(frame.Context.Clone());

    public override string NodeTypeBase() =>
        "document { (element(*,xdt:untyped) ? & text ? & comment ? & processing-instruction ?) * }";
}

/// <summary>A parenthesized sequence, <c>(a, b)</c> — <c>()</c> included.</summary>
internal sealed class XmlSequenceExpr(XmlQueryExpr[] items)
    : XmlQueryExpr(
        items.Length == 1 ? items[0].Kind : XmlStaticKind.Node,
        items.Length == 1 ? items[0].Occurrence : XmlOccurrence.Many,
        items.Length == 1 ? items[0].TypeName : "xdt:untypedAtomic")
{
    private readonly XmlQueryExpr[] items = items;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        foreach (var item in this.items)
            item.Evaluate(frame, results);
    }

    public override string NodeTypeBase() => this.items.Length == 1 ? this.items[0].NodeTypeBase() : base.NodeTypeBase();
}

/// <summary>
/// A source expression narrowed by one or more predicates —
/// <c>(…)[1]</c>, <c>string(.)[1]</c>. A step's own predicates live on
/// <see cref="XmlStep"/> instead, since they need the step's per-context
/// sequence for <c>position()</c>.
/// </summary>
internal sealed class XmlFilterExpr(XmlQueryExpr source, XmlQueryExpr[] predicates)
    : XmlQueryExpr(source.Kind, XmlStep.Narrow(source.Occurrence, predicates), source.TypeName)
{
    private readonly XmlQueryExpr source = source;
    private readonly XmlQueryExpr[] predicates = predicates;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var items = this.source.Evaluate(frame);
        XmlStep.ApplyPredicates(items, this.predicates, results, frame.Variables);
    }

    public override string NodeTypeBase() => this.source.NodeTypeBase();
}

/// <summary>The document root of the instance being queried — an absolute path's start.</summary>
internal sealed class XmlRootExpr() : XmlQueryExpr(XmlStaticKind.Node, XmlOccurrence.ExactlyOne, "xdt:untypedAtomic")
{
    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var root = frame.Context.Clone();
        root.MoveToRoot();
        results.Add(root);
    }
}

/// <summary>A path expression: an optional start expression followed by location steps.</summary>
internal sealed class XmlPathExpr(XmlQueryExpr start, XmlStep[] steps)
    : XmlQueryExpr(XmlStaticKind.Node, PathOccurrence(start, steps), "xdt:untypedAtomic")
{
    private readonly XmlQueryExpr start = start;
    private readonly XmlStep[] steps = steps;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var current = this.start.Evaluate(frame);
        foreach (var step in this.steps)
        {
            var next = new List<object>();
            step.Evaluate(current, next, frame.Variables);
            current = next;
        }
        results.AddRange(current);
    }

    public override string NodeTypeBase() =>
        this.steps.Length == 0 ? this.start.NodeTypeBase() : this.steps[^1].NodeTypeBase();

    private static XmlOccurrence PathOccurrence(XmlQueryExpr start, XmlStep[] steps)
    {
        var occurrence = start.Occurrence;
        foreach (var step in steps)
            occurrence = Combine(occurrence, step.Occurrence);
        return occurrence;
    }
}

/// <summary>Which nodes a step's axis reaches from its context node.</summary>
internal enum XmlAxis
{
    /// <summary><c>child::</c> — the default axis.</summary>
    Child,

    /// <summary><c>attribute::</c>, written <c>@</c>.</summary>
    Attribute,

    /// <summary><c>self::</c>.</summary>
    Self,

    /// <summary><c>parent::</c>, written <c>..</c>.</summary>
    Parent,

    /// <summary><c>descendant::</c>.</summary>
    Descendant,

    /// <summary><c>descendant-or-self::</c>, the expansion of <c>//</c>.</summary>
    DescendantOrSelf,
}

/// <summary>What a step's node test matches.</summary>
internal enum XmlNodeTestKind
{
    /// <summary>A (possibly namespace-qualified) name.</summary>
    Name,

    /// <summary><c>*</c> — any node of the axis's principal kind.</summary>
    Wildcard,

    /// <summary><c>text()</c>.</summary>
    Text,

    /// <summary><c>node()</c>.</summary>
    Node,

    /// <summary><c>comment()</c>.</summary>
    Comment,

    /// <summary><c>processing-instruction()</c>.</summary>
    ProcessingInstruction,
}

/// <summary>
/// One location step: axis, node test and predicates. Predicates run against
/// the sequence the axis produced <em>per context node</em>, which is what makes
/// <c>a[1]</c> the first <c>a</c> under each parent rather than the first
/// overall.
/// </summary>
internal sealed class XmlStep(
    XmlAxis axis,
    XmlNodeTestKind testKind,
    string localName,
    string namespaceUri,
    XmlQueryExpr[] predicates)
{
    private readonly XmlAxis axis = axis;
    private readonly XmlNodeTestKind testKind = testKind;
    private readonly string localName = localName;
    private readonly string namespaceUri = namespaceUri;
    private readonly XmlQueryExpr[] predicates = predicates;

    /// <summary>Static cardinality this step contributes per context node.</summary>
    public readonly XmlOccurrence Occurrence = Narrow(
        axis switch
        {
            XmlAxis.Attribute => testKind == XmlNodeTestKind.Name ? XmlOccurrence.ZeroOrOne : XmlOccurrence.Many,
            XmlAxis.Parent => XmlOccurrence.ZeroOrOne,
            XmlAxis.Self => XmlOccurrence.ZeroOrOne,
            _ => XmlOccurrence.Many,
        },
        predicates);

    /// <summary>
    /// A positional predicate narrows a sequence to at most one item; a
    /// filtering one doesn't, which is why <c>/r/a[@x="1"]</c> is still
    /// <c>*</c> on real while <c>(/r/a)[1]</c> is <c>?</c>.
    /// </summary>
    public static XmlOccurrence Narrow(XmlOccurrence occurrence, XmlQueryExpr[] predicates)
    {
        foreach (var predicate in predicates)
        {
            if (predicate.Kind == XmlStaticKind.Number)
                return XmlOccurrence.ZeroOrOne;
        }
        return occurrence;
    }

    /// <summary>Real's static-type notation for what this step selects.</summary>
    public string NodeTypeBase() => this.testKind switch
    {
        XmlNodeTestKind.Comment => "comment",
        XmlNodeTestKind.Node => "node()",
        XmlNodeTestKind.ProcessingInstruction => "processing-instruction",
        XmlNodeTestKind.Text => "text",
        XmlNodeTestKind.Wildcard => this.axis == XmlAxis.Attribute
            ? "attribute(*,xdt:untypedAtomic)"
            : "element(*,xdt:untyped)",
        _ => this.axis == XmlAxis.Attribute
            ? $"attribute({this.localName},xdt:untypedAtomic)"
            : $"element({this.localName},xdt:untyped)",
    };

    /// <summary>Runs the step over every item in <paramref name="context"/>.</summary>
    public void Evaluate(List<object> context, List<object> results, XmlVariableScope? variables)
    {
        var start = results.Count;
        var matched = new List<object>();
        foreach (var item in context)
        {
            if (item is not XPathNavigator node)
                continue;
            matched.Clear();
            this.Select(node, matched);
            if (this.predicates.Length == 0)
                results.AddRange(matched);
            else
                ApplyPredicates(matched, this.predicates, results, variables);
        }

        if (context.Count > 1)
            SortIntoDocumentOrder(results, start);
    }

    /// <summary>
    /// Puts a step's output in document order and drops repeats, which is what
    /// <c>/</c> does to the union of the per-context-node sequences. A single
    /// context node's axis output is already ordered and distinct, so only the
    /// multi-node case needs it, and two axes actually disturb it: the
    /// <c>descendant-or-self::node()</c> that <c>//</c> expands to puts a node
    /// and its own descendants in the same context, so the following step
    /// interleaves (<c>//b</c> would otherwise report a <c>b</c> child of the
    /// root ahead of one nested under an earlier sibling), and <c>..</c> reaches
    /// one parent once per child (<c>/r/a/..</c> is one <c>r</c>, not one per
    /// <c>a</c>).
    /// </summary>
    private static void SortIntoDocumentOrder(List<object> results, int start)
    {
        var count = results.Count - start;
        if (count < 2)
            return;

        // The common shape — a child or attribute step over an already-ordered
        // context — comes out ordered and distinct, so check before paying for
        // a sort whose comparisons each walk to a common ancestor.
        var ordered = true;
        for (var i = start + 1; i < results.Count; i++)
        {
            if (((XPathNavigator)results[i - 1]).ComparePosition((XPathNavigator)results[i]) != XmlNodeOrder.Before)
            {
                ordered = false;
                break;
            }
        }
        if (ordered)
            return;

        results.Sort(start, count, DocumentOrderComparer.Instance);
        var write = start + 1;
        for (var read = start + 1; read < results.Count; read++)
        {
            if (!((XPathNavigator)results[write - 1]).IsSamePosition((XPathNavigator)results[read]))
                results[write++] = results[read];
        }
        results.RemoveRange(write, results.Count - write);
    }

    private sealed class DocumentOrderComparer : IComparer<object>
    {
        public static readonly DocumentOrderComparer Instance = new();

        public int Compare(object? x, object? y) =>
            ((XPathNavigator)x!).ComparePosition((XPathNavigator)y!) switch
            {
                XmlNodeOrder.Before => -1,
                XmlNodeOrder.After => 1,
                _ => 0,
            };
    }

    /// <summary>
    /// Filters <paramref name="items"/> through <paramref name="predicates"/>
    /// in order, each seeing the sequence the previous one left — so
    /// <c>[@x="1"][2]</c> takes the second match while <c>[2][@x="1"]</c> tests
    /// the second item.
    /// </summary>
    public static void ApplyPredicates(List<object> items, XmlQueryExpr[] predicates, List<object> results, XmlVariableScope? variables)
    {
        var current = items;
        foreach (var predicate in predicates)
        {
            var kept = new List<object>();
            for (var i = 0; i < current.Count; i++)
            {
                var frame = new XmlQueryFrame(current[i] as XPathNavigator ?? EmptyNavigator, i + 1, current.Count, variables);
                if (XmlQueryValues.PredicateHolds(predicate, frame))
                    kept.Add(current[i]);
            }
            current = kept;
        }
        results.AddRange(current);
    }

    /// <summary>
    /// Stand-in context for a predicate over a non-node item: only
    /// <c>position()</c> / <c>last()</c> can be meaningful there, and both read
    /// the frame rather than the node.
    /// </summary>
    private static readonly XPathNavigator EmptyNavigator = new System.Xml.XmlDocument().CreateNavigator()!;

    private void Select(XPathNavigator node, List<object> matched)
    {
        switch (this.axis)
        {
            case XmlAxis.Attribute:
                var attribute = node.Clone();
                if (!attribute.MoveToFirstAttribute())
                    return;
                do
                {
                    if (this.Matches(attribute))
                        matched.Add(attribute.Clone());
                }
                while (attribute.MoveToNextAttribute());
                return;
            case XmlAxis.Parent:
                var parent = node.Clone();
                if (parent.MoveToParent() && this.Matches(parent))
                    matched.Add(parent);
                return;
            case XmlAxis.Self:
                if (this.Matches(node))
                    matched.Add(node.Clone());
                return;
            case XmlAxis.Descendant:
            case XmlAxis.DescendantOrSelf:
                if (this.axis == XmlAxis.DescendantOrSelf && this.Matches(node))
                    matched.Add(node.Clone());
                this.SelectDescendants(node, matched);
                return;
            default:
                var child = node.Clone();
                if (!child.MoveToFirstChild())
                    return;
                do
                {
                    if (this.Matches(child))
                        matched.Add(child.Clone());
                }
                while (child.MoveToNext());
                return;
        }
    }

    private void SelectDescendants(XPathNavigator node, List<object> matched)
    {
        var child = node.Clone();
        if (!child.MoveToFirstChild())
            return;
        do
        {
            if (this.Matches(child))
                matched.Add(child.Clone());
            this.SelectDescendants(child, matched);
        }
        while (child.MoveToNext());
    }

    private bool Matches(XPathNavigator node) => this.testKind switch
    {
        XmlNodeTestKind.Comment => node.NodeType == XPathNodeType.Comment,
        XmlNodeTestKind.Node => true,
        XmlNodeTestKind.ProcessingInstruction => node.NodeType == XPathNodeType.ProcessingInstruction,
        XmlNodeTestKind.Text => node.NodeType is XPathNodeType.Text or XPathNodeType.SignificantWhitespace or XPathNodeType.Whitespace,
        XmlNodeTestKind.Wildcard => node.NodeType is XPathNodeType.Element or XPathNodeType.Attribute,
        _ => node.NodeType is XPathNodeType.Element or XPathNodeType.Attribute
            && string.Equals(node.LocalName, this.localName, StringComparison.Ordinal)
            && string.Equals(node.NamespaceURI, this.namespaceUri, StringComparison.Ordinal),
    };
}

/// <summary>A general (<c>=</c> …) or value (<c>eq</c> …) comparison.</summary>
internal sealed class XmlComparisonExpr(XmlQueryExpr left, XmlQueryExpr right, string op, bool isValueComparison)
    : XmlQueryExpr(XmlStaticKind.Boolean, XmlOccurrence.ExactlyOne, "xs:boolean")
{
    private readonly XmlQueryExpr left = left;
    private readonly XmlQueryExpr right = right;
    private readonly string op = op;
    private readonly bool isValueComparison = isValueComparison;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var leftItems = this.left.Evaluate(frame);
        var rightItems = this.right.Evaluate(frame);

        // A value comparison over an empty operand answers the empty sequence,
        // which a predicate reads as no-match; a general comparison over one
        // answers false. Both surface as "not selected".
        if (this.isValueComparison && (leftItems.Count == 0 || rightItems.Count == 0))
            return;

        foreach (var leftItem in leftItems)
        {
            foreach (var rightItem in rightItems)
            {
                if (XmlQueryValues.CompareAtomic(XmlQueryValues.Atomize(leftItem), this.op, XmlQueryValues.Atomize(rightItem)))
                {
                    results.Add(true);
                    return;
                }
            }
        }
        results.Add(false);
    }
}

/// <summary><c>and</c> / <c>or</c> over effective boolean values.</summary>
internal sealed class XmlLogicalExpr(XmlQueryExpr left, XmlQueryExpr right, bool isAnd)
    : XmlQueryExpr(XmlStaticKind.Boolean, XmlOccurrence.ExactlyOne, "xs:boolean")
{
    private readonly XmlQueryExpr left = left;
    private readonly XmlQueryExpr right = right;
    private readonly bool isAnd = isAnd;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var leftValue = XmlQueryValues.EffectiveBoolean(this.left.Evaluate(frame));
        results.Add(this.isAnd
            ? leftValue && XmlQueryValues.EffectiveBoolean(this.right.Evaluate(frame))
            : leftValue || XmlQueryValues.EffectiveBoolean(this.right.Evaluate(frame)));
    }
}

/// <summary><c>+</c> / <c>-</c> / <c>*</c> / <c>div</c> / <c>idiv</c> / <c>mod</c>, and unary minus.</summary>
internal sealed class XmlArithmeticExpr(XmlQueryExpr left, XmlQueryExpr? right, char op)
    : XmlQueryExpr(XmlStaticKind.Number, XmlOccurrence.ExactlyOne, "xs:decimal")
{
    private readonly XmlQueryExpr left = left;
    private readonly XmlQueryExpr? right = right;
    private readonly char op = op;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var leftValue = XmlQueryValues.SingleNumber(this.left.Evaluate(frame));
        if (this.right is null)
        {
            results.Add(-leftValue);
            return;
        }
        var rightValue = XmlQueryValues.SingleNumber(this.right.Evaluate(frame));
        results.Add(this.op switch
        {
            '*' => leftValue * rightValue,
            '+' => leftValue + rightValue,
            '-' => leftValue - rightValue,
            'i' => Math.Truncate(leftValue / rightValue),
            'm' => leftValue % rightValue,
            _ => leftValue / rightValue,
        });
    }
}

/// <summary>
/// One <c>for</c> / <c>let</c> / quantified binding: the slot the binding
/// construct writes per iteration and the static type its <c>$</c>-references
/// carry. A <c>for</c> binds one item at a time, so its variable is exactly one
/// item whatever the binding sequence's cardinality; a <c>let</c> binds the
/// whole sequence once and its variable carries that sequence's type as-is
/// (probe-confirmed: <c>let $i := /r/a return string($i)</c> is Msg 2389).
/// </summary>
internal sealed class XmlVariableBinding(string name, int slot, XmlQueryExpr source, bool perItem)
{
    /// <summary>The name as written, without the <c>$</c>.</summary>
    public readonly string Name = name;

    /// <summary>Index into the evaluation's <see cref="XmlVariableScope"/>.</summary>
    public readonly int Slot = slot;

    /// <summary>The binding sequence.</summary>
    public readonly XmlQueryExpr Source = source;

    /// <summary>Whether the binding iterates (<c>for</c>) rather than binding the whole sequence (<c>let</c>).</summary>
    public readonly bool PerItem = perItem;

    /// <summary>Static item kind a reference to this variable carries.</summary>
    public readonly XmlStaticKind Kind = source.Kind;

    /// <summary>Static cardinality a reference to this variable carries.</summary>
    public readonly XmlOccurrence Occurrence = perItem ? XmlOccurrence.ExactlyOne : source.Occurrence;

    /// <summary>Atomized type name a reference to this variable carries.</summary>
    public readonly string TypeName = source.TypeName;

    /// <summary>Un-suffixed node-form type name a reference to this variable carries.</summary>
    public readonly string NodeType = source.NodeTypeBase();
}

/// <summary>A <c>$</c>-variable reference, resolved to its binding at compile time.</summary>
internal sealed class XmlVariableRefExpr(XmlVariableBinding binding)
    : XmlQueryExpr(binding.Kind, binding.Occurrence, binding.TypeName)
{
    private readonly XmlVariableBinding binding = binding;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results) =>
        results.AddRange(frame.Variables!.Read(this.binding.Slot));

    public override string NodeTypeBase() => this.binding.NodeType;
}

/// <summary>One <c>order by</c> item: its key expression and its direction.</summary>
internal sealed class XmlOrderSpec(XmlQueryExpr key, bool descending)
{
    /// <summary>The sort key, evaluated once per tuple.</summary>
    public readonly XmlQueryExpr Key = key;

    /// <summary>Whether the item carried <c>descending</c>.</summary>
    public readonly bool Descending = descending;

    /// <summary>
    /// Whether the key compares numerically. Real compares an untyped key by
    /// code point — <c>"10"</c> sorts before <c>"2"</c> — and only a key it
    /// types as a number numerically (probe-confirmed).
    /// </summary>
    public readonly bool Numeric = key.Kind == XmlStaticKind.Number;
}

/// <summary>
/// One tuple the <c>for</c> / <c>let</c> clauses produced, held back until the
/// whole stream is known because an <c>order by</c> has to sort it.
/// </summary>
internal sealed class XmlFlworTuple(object?[] keys, List<object>[] bindings, int ordinal)
{
    /// <summary>The evaluated sort keys, one per <c>order by</c> item; null where the key was empty.</summary>
    public readonly object?[] Keys = keys;

    /// <summary>The bound sequences, one per binding, restored before the return clause runs.</summary>
    public readonly List<object>[] Bindings = bindings;

    /// <summary>Position in the unsorted stream, which keeps the sort stable.</summary>
    public readonly int Ordinal = ordinal;
}

/// <summary>
/// A FLWOR expression. The result keeps iteration order and every duplicate —
/// it is not folded into document order the way a path step's output is
/// (probe-confirmed: <c>for $i in /r/a return /r/b</c> answers the same
/// <c>b</c> once per <c>a</c>).
/// </summary>
internal sealed class XmlFlworExpr(
    XmlVariableBinding[] bindings,
    XmlQueryExpr? where,
    XmlOrderSpec[] orderBy,
    XmlQueryExpr body)
    : XmlQueryExpr(body.Kind, FlworOccurrence(bindings, body), body.TypeName)
{
    private readonly XmlVariableBinding[] bindings = bindings;
    private readonly XmlQueryExpr? where = where;
    private readonly XmlOrderSpec[] orderBy = orderBy;
    private readonly XmlQueryExpr body = body;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var scope = frame.Variables ?? new XmlVariableScope();
        var inner = new XmlQueryFrame(frame.Context, frame.Position, frame.Size, scope);
        if (this.orderBy.Length == 0)
        {
            this.Bind(0, inner, scope, results, null);
            return;
        }

        var tuples = new List<XmlFlworTuple>();
        this.Bind(0, inner, scope, results, tuples);
        tuples.Sort(this.Compare);
        foreach (var tuple in tuples)
        {
            for (var i = 0; i < this.bindings.Length; i++)
                scope.Write(this.bindings[i].Slot, tuple.Bindings[i]);
            this.body.Evaluate(inner, results);
        }
    }

    public override string NodeTypeBase() => this.body.NodeTypeBase();

    /// <summary>
    /// Cardinality multiplies along the <c>for</c> bindings — each iterates —
    /// while a <c>let</c> binds once and a <c>where</c> doesn't narrow the
    /// static type (probe-confirmed).
    /// </summary>
    private static XmlOccurrence FlworOccurrence(XmlVariableBinding[] bindings, XmlQueryExpr body)
    {
        var occurrence = body.Occurrence;
        foreach (var binding in bindings)
        {
            if (binding.PerItem)
                occurrence = Combine(occurrence, binding.Source.Occurrence);
        }
        return occurrence;
    }

    /// <summary>
    /// Walks the binding clauses, producing one tuple per combination. With no
    /// <c>order by</c> the return clause runs inline; with one, the tuple is
    /// captured and the return clause waits for the sort.
    /// </summary>
    private void Bind(int index, in XmlQueryFrame frame, XmlVariableScope scope, List<object> results, List<XmlFlworTuple>? tuples)
    {
        if (index == this.bindings.Length)
        {
            if (this.where is not null && !XmlQueryValues.EffectiveBoolean(this.where.Evaluate(frame)))
                return;
            if (tuples is null)
            {
                this.body.Evaluate(frame, results);
                return;
            }

            var keys = new object?[this.orderBy.Length];
            for (var i = 0; i < this.orderBy.Length; i++)
            {
                var key = this.orderBy[i].Key.Evaluate(frame);
                keys[i] = key.Count == 0 ? null : XmlQueryValues.Atomize(key[0]);
            }
            var snapshot = new List<object>[this.bindings.Length];
            for (var i = 0; i < this.bindings.Length; i++)
                snapshot[i] = scope.Read(this.bindings[i].Slot);
            tuples.Add(new XmlFlworTuple(keys, snapshot, tuples.Count));
            return;
        }

        var binding = this.bindings[index];
        var items = binding.Source.Evaluate(frame);
        if (!binding.PerItem)
        {
            scope.Write(binding.Slot, items);
            this.Bind(index + 1, frame, scope, results, tuples);
            return;
        }
        foreach (var item in items)
        {
            scope.Write(binding.Slot, [item]);
            this.Bind(index + 1, frame, scope, results, tuples);
        }
    }

    /// <summary>
    /// Orders two tuples. An empty key sorts first ascending — real's default
    /// is <c>empty least</c>, and <c>descending</c> reverses the comparison so
    /// it lands last (probe-confirmed) — and the stream position breaks ties,
    /// which is what makes the sort stable.
    /// </summary>
    private int Compare(XmlFlworTuple left, XmlFlworTuple right)
    {
        for (var i = 0; i < this.orderBy.Length; i++)
        {
            var spec = this.orderBy[i];
            var comparison = CompareKeys(left.Keys[i], right.Keys[i], spec.Numeric);
            if (comparison != 0)
                return spec.Descending ? -comparison : comparison;
        }
        return left.Ordinal.CompareTo(right.Ordinal);
    }

    private static int CompareKeys(object? left, object? right, bool numeric) =>
        left is null || right is null
            ? left is null ? (right is null ? 0 : -1) : 1
            : numeric
                ? XmlQueryValues.ToNumber(left).CompareTo(XmlQueryValues.ToNumber(right))
                : string.CompareOrdinal(XmlQueryValues.StringValue(left), XmlQueryValues.StringValue(right));
}

/// <summary>
/// <c>some $v in … satisfies …</c> / <c>every $v in … satisfies …</c>. An empty
/// binding sequence makes <c>some</c> false and <c>every</c> true
/// (probe-confirmed).
/// </summary>
internal sealed class XmlQuantifiedExpr(XmlVariableBinding[] bindings, XmlQueryExpr satisfies, bool isEvery)
    : XmlQueryExpr(XmlStaticKind.Boolean, XmlOccurrence.ExactlyOne, "xs:boolean")
{
    private readonly XmlVariableBinding[] bindings = bindings;
    private readonly XmlQueryExpr satisfies = satisfies;
    private readonly bool isEvery = isEvery;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var scope = frame.Variables ?? new XmlVariableScope();
        var inner = new XmlQueryFrame(frame.Context, frame.Position, frame.Size, scope);
        results.Add(this.Test(0, inner, scope));
    }

    private bool Test(int index, in XmlQueryFrame frame, XmlVariableScope scope)
    {
        if (index == this.bindings.Length)
            return XmlQueryValues.EffectiveBoolean(this.satisfies.Evaluate(frame));

        var binding = this.bindings[index];
        foreach (var item in binding.Source.Evaluate(frame))
        {
            scope.Write(binding.Slot, [item]);

            // some stops at the first tuple that holds, every at the first that
            // doesn't; either way the decisive answer is the one to report.
            var held = this.Test(index + 1, frame, scope);
            if (held != this.isEvery)
                return held;
        }
        return this.isEvery;
    }
}

/// <summary><c>if (…) then … else …</c>; XQuery's <c>else</c> is mandatory.</summary>
internal sealed class XmlConditionalExpr(XmlQueryExpr condition, XmlQueryExpr thenBranch, XmlQueryExpr elseBranch)
    : XmlQueryExpr(thenBranch.Kind, Combine(thenBranch.Occurrence, elseBranch.Occurrence), thenBranch.TypeName)
{
    private readonly XmlQueryExpr condition = condition;
    private readonly XmlQueryExpr thenBranch = thenBranch;
    private readonly XmlQueryExpr elseBranch = elseBranch;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var taken = XmlQueryValues.EffectiveBoolean(this.condition.Evaluate(frame)) ? this.thenBranch : this.elseBranch;
        taken.Evaluate(frame, results);
    }

    public override string NodeTypeBase() => this.thenBranch.NodeTypeBase();
}

/// <summary>
/// A direct element constructor, held as the literal markup segments the
/// <c>{…}</c> enclosed expressions sit between. Each evaluation splices the
/// evaluated sequences in — as markup in element content, as text in an
/// attribute value — and parses the result, so the constructed node serializes
/// like any other.
/// </summary>
internal sealed class XmlConstructedNodeExpr(string[] literals, XmlQueryExpr[] enclosed, bool[] inAttribute, string elementName)
    : XmlQueryExpr(XmlStaticKind.Node, XmlOccurrence.ExactlyOne, "xdt:untypedAtomic")
{
    private readonly string[] literals = literals;
    private readonly XmlQueryExpr[] enclosed = enclosed;
    private readonly bool[] inAttribute = inAttribute;
    private readonly string elementName = elementName;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        var text = new System.Text.StringBuilder(this.literals[0]);
        for (var i = 0; i < this.enclosed.Length; i++)
        {
            XmlQueryEngine.AppendSequence(text, this.enclosed[i].Evaluate(frame), this.inAttribute[i]);
            _ = text.Append(this.literals[i + 1]);
        }
        results.Add(System.Xml.Linq.XDocument.Parse(text.ToString()).Root!.CreateNavigator());
    }

    public override string NodeTypeBase() => $"element({this.elementName},xdt:untyped)";
}

/// <summary>What a function parameter admits, and therefore what it diagnoses.</summary>
internal enum XmlArgumentRule
{
    /// <summary>Any sequence — <c>count()</c>, <c>sum()</c>, <c>empty()</c>.</summary>
    Sequence,

    /// <summary>A condition: a non-boolean, non-node argument is Msg 2204 (<c>not()</c>).</summary>
    Condition,

    /// <summary>An atomic type: a plural argument is Msg 2389 quoting the atomized type.</summary>
    Atomic,

    /// <summary><c>item()?</c>: a plural argument is Msg 2389 quoting the node type.</summary>
    Item,
}

/// <summary>The XQuery functions the evaluator implements.</summary>
internal enum XmlFunctionId
{
    /// <summary><c>fn:avg</c>.</summary>
    Avg,

    /// <summary><c>fn:ceiling</c>.</summary>
    Ceiling,

    /// <summary><c>fn:concat</c>.</summary>
    Concat,

    /// <summary><c>fn:contains</c>.</summary>
    Contains,

    /// <summary><c>fn:count</c>.</summary>
    Count,

    /// <summary><c>fn:data</c>.</summary>
    Data,

    /// <summary><c>fn:distinct-values</c>.</summary>
    DistinctValues,

    /// <summary><c>fn:empty</c>.</summary>
    Empty,

    /// <summary><c>fn:false</c>.</summary>
    False,

    /// <summary><c>fn:floor</c>.</summary>
    Floor,

    /// <summary><c>fn:last</c>.</summary>
    Last,

    /// <summary><c>fn:local-name</c>.</summary>
    LocalName,

    /// <summary><c>fn:lower-case</c>.</summary>
    LowerCase,

    /// <summary><c>fn:max</c>.</summary>
    Max,

    /// <summary><c>fn:min</c>.</summary>
    Min,

    /// <summary><c>fn:namespace-uri</c>.</summary>
    NamespaceUri,

    /// <summary><c>fn:not</c>.</summary>
    Not,

    /// <summary><c>fn:number</c>.</summary>
    Number,

    /// <summary><c>fn:position</c>.</summary>
    Position,

    /// <summary><c>fn:round</c>.</summary>
    Round,

    /// <summary><c>fn:string</c>.</summary>
    String,

    /// <summary><c>fn:string-length</c>.</summary>
    StringLength,

    /// <summary><c>fn:substring</c>.</summary>
    Substring,

    /// <summary><c>fn:sum</c>.</summary>
    Sum,

    /// <summary><c>fn:true</c>.</summary>
    True,

    /// <summary><c>fn:upper-case</c>.</summary>
    UpperCase,
}

/// <summary>A call to one of the built-in XQuery functions.</summary>
internal sealed class XmlFunctionCallExpr(XmlFunctionId id, XmlQueryExpr[] arguments, XmlStaticKind kind, XmlOccurrence occurrence, string typeName)
    : XmlQueryExpr(kind, occurrence, typeName)
{
    private readonly XmlFunctionId id = id;
    private readonly XmlQueryExpr[] arguments = arguments;

    public override void Evaluate(in XmlQueryFrame frame, List<object> results)
    {
        switch (this.id)
        {
            case XmlFunctionId.Avg:
                var addends = this.Numbers(frame, 0);
                if (addends.Count > 0)
                    results.Add(addends.Sum() / addends.Count);
                return;
            case XmlFunctionId.Ceiling:
                results.Add(Math.Ceiling(this.Number(frame, 0)));
                return;
            case XmlFunctionId.Concat:
                var text = new System.Text.StringBuilder();
                foreach (var argument in this.arguments)
                    _ = text.Append(XmlQueryValues.SingleString(argument.Evaluate(frame)));
                results.Add(text.ToString());
                return;
            case XmlFunctionId.Contains:
                results.Add(XmlQueryValues.SingleString(this.arguments[0].Evaluate(frame))
                    .Contains(XmlQueryValues.SingleString(this.arguments[1].Evaluate(frame)), StringComparison.Ordinal));
                return;
            case XmlFunctionId.Count:
                results.Add((double)this.arguments[0].Evaluate(frame).Count);
                return;
            case XmlFunctionId.Data:
                foreach (var item in this.arguments[0].Evaluate(frame))
                    results.Add(XmlQueryValues.Atomize(item));
                return;
            case XmlFunctionId.DistinctValues:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in this.arguments[0].Evaluate(frame))
                {
                    var atomized = XmlQueryValues.Atomize(item);
                    if (seen.Add(XmlQueryValues.StringValue(atomized)))
                        results.Add(atomized);
                }
                return;
            case XmlFunctionId.Empty:
                results.Add(this.arguments[0].Evaluate(frame).Count == 0);
                return;
            case XmlFunctionId.False:
                results.Add(false);
                return;
            case XmlFunctionId.Floor:
                results.Add(Math.Floor(this.Number(frame, 0)));
                return;
            case XmlFunctionId.Last:
                results.Add((double)frame.Size);
                return;
            case XmlFunctionId.LocalName:
                results.Add(this.ContextOrArgumentNode(frame) is { } named ? named.LocalName : string.Empty);
                return;
            case XmlFunctionId.LowerCase:
#pragma warning disable CA1308 // fn:lower-case() lowercases by definition; the result is the contract.
                results.Add(XmlQueryValues.SingleString(this.arguments[0].Evaluate(frame)).ToLowerInvariant());
#pragma warning restore CA1308
                return;
            case XmlFunctionId.Max:
                var maxima = this.Numbers(frame, 0);
                if (maxima.Count > 0)
                    results.Add(maxima.Max());
                return;
            case XmlFunctionId.Min:
                var minima = this.Numbers(frame, 0);
                if (minima.Count > 0)
                    results.Add(minima.Min());
                return;
            case XmlFunctionId.NamespaceUri:
                results.Add(this.ContextOrArgumentNode(frame) is { } scoped ? scoped.NamespaceURI : string.Empty);
                return;
            case XmlFunctionId.Not:
                results.Add(!XmlQueryValues.EffectiveBoolean(this.arguments[0].Evaluate(frame)));
                return;
            case XmlFunctionId.Number:
                results.Add(this.arguments.Length == 0
                    ? XmlQueryValues.ToNumber(new XmlUntypedAtomic(frame.Context.Value))
                    : this.Number(frame, 0));
                return;
            case XmlFunctionId.Position:
                results.Add((double)frame.Position);
                return;
            case XmlFunctionId.Round:
                results.Add(Math.Round(this.Number(frame, 0), MidpointRounding.AwayFromZero));
                return;
            case XmlFunctionId.String:
                results.Add(this.arguments.Length == 0
                    ? frame.Context.Value
                    : XmlQueryValues.SingleString(this.arguments[0].Evaluate(frame)));
                return;
            case XmlFunctionId.StringLength:
                results.Add((double)(this.arguments.Length == 0
                    ? frame.Context.Value.Length
                    : XmlQueryValues.SingleString(this.arguments[0].Evaluate(frame)).Length));
                return;
            case XmlFunctionId.Substring:
                results.Add(Substring(
                    XmlQueryValues.SingleString(this.arguments[0].Evaluate(frame)),
                    this.Number(frame, 1),
                    this.arguments.Length > 2 ? this.Number(frame, 2) : double.PositiveInfinity));
                return;
            case XmlFunctionId.Sum:
                results.Add(this.Numbers(frame, 0).Sum());
                return;
            case XmlFunctionId.True:
                results.Add(true);
                return;
            case XmlFunctionId.UpperCase:
                results.Add(XmlQueryValues.SingleString(this.arguments[0].Evaluate(frame)).ToUpperInvariant());
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// XQuery's <c>fn:substring</c> is 1-based and rounds its numeric
    /// arguments, and a start before 1 shortens the taken length rather than
    /// shifting it.
    /// </summary>
    private static string Substring(string text, double start, double length)
    {
        if (double.IsNaN(start))
            return string.Empty;
        var first = Math.Round(start, MidpointRounding.AwayFromZero);
        var last = double.IsPositiveInfinity(length) ? double.PositiveInfinity : first + Math.Round(length, MidpointRounding.AwayFromZero);
        var from = (int)Math.Max(1, Math.Min(first, text.Length + 1));
        var to = (int)Math.Max(from, Math.Min(double.IsPositiveInfinity(last) ? text.Length + 1 : last, text.Length + 1));
        return text[(from - 1)..(to - 1)];
    }

    private XPathNavigator? ContextOrArgumentNode(in XmlQueryFrame frame)
    {
        if (this.arguments.Length == 0)
            return frame.Context;
        foreach (var item in this.arguments[0].Evaluate(frame))
        {
            if (item is XPathNavigator node)
                return node;
        }
        return null;
    }

    private double Number(in XmlQueryFrame frame, int index) =>
        XmlQueryValues.SingleNumber(this.arguments[index].Evaluate(frame));

    private List<double> Numbers(in XmlQueryFrame frame, int index)
    {
        var numbers = new List<double>();
        foreach (var item in this.arguments[index].Evaluate(frame))
            numbers.Add(XmlQueryValues.ToNumber(XmlQueryValues.Atomize(item)));
        return numbers;
    }
}

/// <summary>
/// The value rules the compiled tree shares: atomization, XQuery's
/// general-comparison type resolution, the effective boolean value, and the
/// predicate dispatch that makes a numeric predicate positional.
/// </summary>
internal static class XmlQueryValues
{
    /// <summary>A node atomizes to its untyped string value; everything else is itself.</summary>
    public static object Atomize(object item) => item is XPathNavigator node ? new XmlUntypedAtomic(node.Value) : item;

    /// <summary>The item's string form, as <c>.value()</c> and serialization read it.</summary>
    public static string StringValue(object item) => item switch
    {
        XPathNavigator node => node.Value,
        XmlUntypedAtomic untyped => untyped.Text,
        bool boolean => boolean ? "true" : "false",
        double number => XmlConvert.ToString(number),
        _ => (string)item,
    };

    /// <summary>The first item's string form; empty sequence answers the empty string.</summary>
    public static string SingleString(List<object> items) => items.Count == 0 ? string.Empty : StringValue(items[0]);

    /// <summary>The first item's numeric form; empty sequence answers NaN.</summary>
    public static double SingleNumber(List<object> items) => items.Count == 0 ? double.NaN : ToNumber(Atomize(items[0]));

    /// <summary>
    /// The item as a number. A value that won't parse answers NaN, which makes
    /// every comparison over it false — real's own answer for
    /// <c>[@x=1]</c> where <c>@x</c> is <c>"abc"</c> (no error, no match).
    /// </summary>
    public static double ToNumber(object item) => item switch
    {
        bool boolean => boolean ? 1 : 0,
        double number => number,
        _ => double.TryParse(StringValue(item), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
    };

    /// <summary>XQuery's effective boolean value, as <c>and</c> / <c>or</c> / <c>not()</c> read it.</summary>
    public static bool EffectiveBoolean(List<object> items) => items.Count switch
    {
        0 => false,
        1 => items[0] switch
        {
            XPathNavigator => true,
            bool boolean => boolean,
            double number => number != 0 && !double.IsNaN(number),
            XmlUntypedAtomic untyped => untyped.Text.Length > 0,
            _ => ((string)items[0]).Length > 0,
        },
        _ => true,
    };

    /// <summary>
    /// Whether an item survives <paramref name="predicate"/>. A numeric
    /// predicate is positional (<c>[2]</c> is the second item, not a boolean
    /// test), a boolean one filters, and a node one tests for existence.
    /// </summary>
    public static bool PredicateHolds(XmlQueryExpr predicate, in XmlQueryFrame frame)
    {
        var items = predicate.Evaluate(frame);
        return predicate.Kind == XmlStaticKind.Number
            ? items.Count > 0 && ToNumber(Atomize(items[0])) == frame.Position
            : EffectiveBoolean(items);
    }

    /// <summary>
    /// One general-comparison pair. Untyped operands take their type from the
    /// other side: untyped vs a numeric literal compares numerically (so
    /// <c>"01"</c> equals <c>1</c>), untyped vs a string literal compares by
    /// code point (so <c>"01"</c> doesn't equal <c>"1"</c>, and <c>"abc"</c>
    /// sorts before <c>"b"</c>) — probe-confirmed against SQL Server 2025.
    /// </summary>
    public static bool CompareAtomic(object left, string op, object right)
    {
        if (left is bool || right is bool)
            return Satisfies(EffectiveBoolean([left]).CompareTo(EffectiveBoolean([right])), op);
        if (left is double || right is double)
        {
            var leftNumber = ToNumber(left);
            var rightNumber = ToNumber(right);
            return !double.IsNaN(leftNumber) && !double.IsNaN(rightNumber) && Satisfies(leftNumber.CompareTo(rightNumber), op);
        }
        return Satisfies(string.CompareOrdinal(StringValue(left), StringValue(right)), op);
    }

    private static bool Satisfies(int comparison, string op) => op switch
    {
        "!=" => comparison != 0,
        "<" => comparison < 0,
        "<=" => comparison <= 0,
        "=" => comparison == 0,
        ">" => comparison > 0,
        ">=" => comparison >= 0,
        "eq" => comparison == 0,
        "ge" => comparison >= 0,
        "gt" => comparison > 0,
        "le" => comparison <= 0,
        "lt" => comparison < 0,
        _ => comparison != 0,
    };
}
