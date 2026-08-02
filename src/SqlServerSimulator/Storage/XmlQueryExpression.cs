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
/// One evaluation frame: the context item plus its position within the
/// sequence the enclosing step produced, which <c>position()</c> / <c>last()</c>
/// and a positional predicate read.
/// </summary>
internal readonly struct XmlQueryFrame(XPathNavigator context, int position, int size)
{
    /// <summary>The context item.</summary>
    public readonly XPathNavigator Context = context;

    /// <summary>1-based position of <see cref="Context"/> in its sequence.</summary>
    public readonly int Position = position;

    /// <summary>Length of the sequence <see cref="Context"/> came from.</summary>
    public readonly int Size = size;
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
        XmlStep.ApplyPredicates(items, this.predicates, results);
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
            step.Evaluate(current, next);
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
    public void Evaluate(List<object> context, List<object> results)
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
                ApplyPredicates(matched, this.predicates, results);
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
    public static void ApplyPredicates(List<object> items, XmlQueryExpr[] predicates, List<object> results)
    {
        var current = items;
        foreach (var predicate in predicates)
        {
            var kept = new List<object>();
            for (var i = 0; i < current.Count; i++)
            {
                var frame = new XmlQueryFrame(current[i] as XPathNavigator ?? EmptyNavigator, i + 1, current.Count);
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

/// <summary>What a function parameter admits, and therefore what it diagnoses.</summary>
internal enum XmlArgumentRule
{
    /// <summary>Any sequence — <c>count()</c>, <c>sum()</c>, <c>not()</c>.</summary>
    Sequence,

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
