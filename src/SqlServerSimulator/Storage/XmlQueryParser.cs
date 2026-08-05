using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Recursive-descent parser for the XQuery subset the <c>xml</c> type's methods
/// evaluate: path expressions with predicates, the general and value comparison
/// operators, <c>and</c> / <c>or</c>, arithmetic, parenthesized sequences and
/// the built-in function library. Everything real settles statically — a
/// predicate's meaning, a value comparison's singleton rule, an operand-type
/// mismatch, an unknown function — is settled here, so those diagnostics fire
/// while the SQL statement parses just as they do on SQL Server.
/// </summary>
/// <remarks>
/// The name-scanning rule is XQuery's, not XPath's: <c>-</c> and <c>.</c>
/// continue a name, so <c>@a-1</c> is the attribute named <c>a-1</c> and a
/// subtraction needs the space real needs (probe-confirmed).
/// </remarks>
internal sealed class XmlQueryParser(
    string text,
    string? defaultNamespace,
    Dictionary<string, string> prefixes,
    string method,
    IReadOnlySet<string>? schemaSingletonElements = null)
{
    /// <summary>The XQuery namespace an unprefixed function name lives in.</summary>
    private const string FunctionNamespace = "http://www.w3.org/2004/07/xpath-functions";

    private readonly string text = text;
    private readonly string? defaultNamespace = defaultNamespace;
    private readonly Dictionary<string, string> prefixes = prefixes;
    private readonly string method = method;

    /// <summary>
    /// The element names the receiver's XML schema collection declares at
    /// most once, or null for an untyped receiver. A named child step whose
    /// name is in here is a singleton to the static type checker, which is
    /// what makes <c>.value()</c> accept a schema-typed path real accepts.
    /// </summary>
    private readonly IReadOnlySet<string>? schemaSingletonElements = schemaSingletonElements;

    /// <summary>The <c>$</c>-variable bindings in scope, innermost last.</summary>
    private readonly List<XmlVariableBinding> scope = [];

    private int index;
    private int slotCount;
    private int predicateDepth;

    /// <summary>
    /// Whether the body built a node rather than only selecting one, which is
    /// what <c>value()</c> and <c>nodes()</c> refuse (Msg 2373).
    /// </summary>
    public bool ConstructsXml;

    /// <summary>Parses the whole body, rejecting anything left over.</summary>
    public XmlQueryExpr ParseBody()
    {
        var expression = this.ParseExpr();
        this.SkipWhitespace();
        return this.index < this.text.Length ? throw this.SyntaxError() : expression;
    }

    /// <summary>
    /// XQuery's <c>Expr</c> — a comma-separated sequence. Only the body, a
    /// parenthesized group and an <c>if</c> condition take the comma form; a
    /// predicate, a function argument and every clause of a FLWOR take one
    /// <c>ExprSingle</c> (probe-confirmed: <c>/r/a[., .]</c> is Msg 9303).
    /// </summary>
    private XmlQueryExpr ParseExpr()
    {
        var first = this.ParseExprSingle();
        this.SkipWhitespace();
        if (this.Current != ',')
            return first;

        var items = new List<XmlQueryExpr> { first };
        while (this.Current == ',')
        {
            this.index++;
            items.Add(this.ParseExprSingle());
            this.SkipWhitespace();
        }
        for (var i = 1; i < items.Count; i++)
            this.RequireHomogeneous(items[0], items[i]);
        return new XmlSequenceExpr([.. items]);
    }

    /// <summary>
    /// One expression: a FLWOR, a quantified or conditional expression, or an
    /// ordinary operator expression. Each keyword is also a legal element name,
    /// so what follows it — a variable reference, or <c>(</c> for <c>if</c> —
    /// is what tells them apart, which is real's own rule (probe-confirmed:
    /// <c>for i in …</c> reports a syntax error near <c>for</c>).
    /// </summary>
    private XmlQueryExpr ParseExprSingle()
    {
        this.SkipWhitespace();
        var word = this.PeekWord();
        return word switch
        {
            "every" or "some" when this.FollowedBy(word, '$') => this.ParseQuantified(word),
            "for" or "let" when this.FollowedBy(word, '$') => this.ParseFlwor(),
            "if" when this.FollowedBy(word, '(') => this.ParseConditional(),
            _ => this.ParseOr(),
        };
    }

    /// <summary>Whether the first non-space character after <paramref name="word"/> is <paramref name="expected"/>.</summary>
    private bool FollowedBy(string word, char expected)
    {
        var after = this.index + word.Length;
        while (after < this.text.Length && char.IsWhiteSpace(this.text[after]))
            after++;
        return after < this.text.Length && this.text[after] == expected;
    }

    private XmlQueryExpr ParseOr()
    {
        var left = this.ParseAnd();
        while (this.TryOperatorWord("or"))
        {
            this.RequireCondition(left);
            var right = this.ParseAnd();
            this.RequireCondition(right);
            left = new XmlLogicalExpr(left, right, isAnd: false);
        }
        return left;
    }

    private XmlQueryExpr ParseAnd()
    {
        var left = this.ParseComparison();
        while (this.TryOperatorWord("and"))
        {
            this.RequireCondition(left);
            var right = this.ParseComparison();
            this.RequireCondition(right);
            left = new XmlLogicalExpr(left, right, isAnd: true);
        }
        return left;
    }

    /// <summary>
    /// <c>for</c> / <c>let</c> bindings, then an optional <c>where</c>, then an
    /// optional <c>(stable) order by</c>, then <c>return</c> — real enforces
    /// that order, so an <c>order by</c> ahead of a <c>where</c> is a syntax
    /// error (probe-confirmed).
    /// </summary>
    private XmlFlworExpr ParseFlwor()
    {
        var outerScope = this.scope.Count;
        var bindings = new List<XmlVariableBinding>();
        while (true)
        {
            this.SkipWhitespace();
            var word = this.PeekWord();
            if (word is not ("for" or "let") || !this.FollowedBy(word, '$'))
                break;
            this.ParseBindings(bindings, word, quantified: false);
        }

        XmlQueryExpr? where = null;
        if (this.TryOperatorWord("where"))
        {
            where = this.ParseExprSingle();
            this.RequireCondition(where);
        }
        else if (!this.AtWord("order") && !this.AtWord("return") && !this.AtWord("stable"))
        {
            throw SimulatedSqlException.XQueryFlworClauseExpected(this.method, this.CurrentToken());
        }

        var orderBy = this.ParseOrderBy();
        if (!this.TryOperatorWord("return"))
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "return");

        var body = this.ParseExprSingle();
        this.scope.RemoveRange(outerScope, this.scope.Count - outerScope);
        return new XmlFlworExpr([.. bindings], where, orderBy, body);
    }

    /// <summary><c>some</c> / <c>every</c> over one or more bindings.</summary>
    private XmlQuantifiedExpr ParseQuantified(string keyword)
    {
        var outerScope = this.scope.Count;
        var bindings = new List<XmlVariableBinding>();
        this.ParseBindings(bindings, keyword, quantified: true);
        if (!this.TryOperatorWord("satisfies"))
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "satisfies");

        var satisfies = this.ParseExprSingle();
        this.RequireCondition(satisfies);
        this.scope.RemoveRange(outerScope, this.scope.Count - outerScope);
        return new XmlQuantifiedExpr([.. bindings], satisfies, keyword.Equals("every", StringComparison.Ordinal));
    }

    /// <summary><c>if (…) then … else …</c>; XQuery has no one-armed form.</summary>
    private XmlConditionalExpr ParseConditional()
    {
        this.index += "if".Length;
        this.SkipWhitespace();
        this.index++;
        var condition = this.ParseExpr();
        this.SkipWhitespace();
        if (this.Current != ')')
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), ")");
        this.index++;
        this.RequireCondition(condition);

        if (!this.TryOperatorWord("then"))
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "then");
        var thenBranch = this.ParseExprSingle();
        if (!this.TryOperatorWord("else"))
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "else");
        var elseBranch = this.ParseExprSingle();

        this.RequireHomogeneous(thenBranch, elseBranch);
        return new XmlConditionalExpr(condition, thenBranch, elseBranch);
    }

    /// <summary>
    /// One comma-separated binding list, shared by <c>for</c> / <c>let</c> and
    /// the quantified expressions. Real diagnoses a missing separator
    /// differently for the two: a FLWOR reports Msg 2205 and a quantified
    /// expression Msg 9303 (probe-confirmed).
    /// </summary>
    private void ParseBindings(List<XmlVariableBinding> bindings, string keyword, bool quantified)
    {
        this.index += keyword.Length;
        var perItem = !keyword.Equals("let", StringComparison.Ordinal);
        while (true)
        {
            this.SkipWhitespace();
            if (this.Current != '$')
                throw this.SyntaxError();
            var name = this.ReadVariableName();

            this.SkipWhitespace();
            var modifier = this.PeekWord();
            if (modifier is "as" or "at")
                throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, modifier);

            if (!perItem)
            {
                if (!this.TryConsumeAssign())
                    throw SimulatedSqlException.XQueryTokenExpected(this.method, ":=");
            }
            else if (!this.TryOperatorWord("in"))
            {
                throw quantified
                    ? SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "in")
                    : SimulatedSqlException.XQueryTokenExpected(this.method, "in");
            }

            var binding = new XmlVariableBinding(name, this.slotCount++, this.ParseExprSingle(), perItem);
            bindings.Add(binding);
            this.scope.Add(binding);

            this.SkipWhitespace();
            if (this.Current != ',')
                return;
            this.index++;
        }
    }

    /// <summary>
    /// The <c>(stable) order by</c> clause. Real ships direction and multiple
    /// items but refuses <c>empty greatest</c> / <c>empty least</c> and
    /// <c>collation</c> with Msg 9335 (probe-confirmed).
    /// </summary>
    private XmlOrderSpec[] ParseOrderBy()
    {
        var stable = this.TryOperatorWord("stable");
        if (!this.TryOperatorWord("order"))
            return stable ? throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "order") : [];
        if (!this.TryOperatorWord("by"))
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "by");

        var specs = new List<XmlOrderSpec>();
        while (true)
        {
            var key = this.ParseExprSingle();
            RequireSingleton(key, "order by", this.method);
            var descending = this.TryOperatorWord("descending");
            if (!descending)
                _ = this.TryOperatorWord("ascending");
            this.RejectOrderModifier();
            specs.Add(new XmlOrderSpec(key, descending));

            this.SkipWhitespace();
            if (this.Current != ',')
                return [.. specs];
            this.index++;
        }
    }

    /// <summary>Msg 9335 for the order-modifier words real parses but refuses.</summary>
    private void RejectOrderModifier()
    {
        this.SkipWhitespace();
        var word = this.PeekWord();
        if (word.Equals("collation", StringComparison.Ordinal))
            throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, word);
        if (!word.Equals("empty", StringComparison.Ordinal))
            return;

        var resume = this.index;
        this.index += word.Length;
        this.SkipWhitespace();
        var placement = this.PeekWord();
        this.index = resume;
        throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, placement.Length == 0 ? word : $"{word} {placement}");
    }

    /// <summary>
    /// Msg 2204: a condition — an <c>if</c> test, a <c>where</c>, a
    /// <c>satisfies</c> body, an <c>and</c> / <c>or</c> operand, a
    /// <c>not()</c> argument — real admits only as boolean or nodes. A numeric
    /// one is refused here where a predicate would read it as a position.
    /// </summary>
    private void RequireCondition(XmlQueryExpr expression)
    {
        if (expression.Kind is XmlStaticKind.Boolean or XmlStaticKind.Node)
            return;
        throw SimulatedSqlException.XQueryConditionNotBoolean(this.method, expression.AtomizedTypeName());
    }

    /// <summary>
    /// Msg 2210: a sequence — a comma list, or an <c>if</c>'s two branches —
    /// putting nodes beside atomic values. Real names the atomic type first
    /// whichever side wrote it (probe-confirmed).
    /// </summary>
    private void RequireHomogeneous(XmlQueryExpr left, XmlQueryExpr right)
    {
        var leftIsNode = left.Kind == XmlStaticKind.Node;
        if (leftIsNode == (right.Kind == XmlStaticKind.Node))
            return;
        var (atomic, node) = leftIsNode ? (right, left) : (left, right);
        throw SimulatedSqlException.XQueryHeterogeneousSequence(this.method, atomic.NodeTypeName(), node.NodeTypeName());
    }

    private XmlQueryExpr ParseComparison()
    {
        var left = this.ParseAdditive();
        var (op, isValueComparison) = this.TryComparisonOperator();
        if (op is null)
            return left;

        var right = this.ParseAdditive();
        if (isValueComparison)
        {
            RequireSingleton(left, op, this.method);
            RequireSingleton(right, op, this.method);
        }
        RequireComparableTypes(left, right, op, this.method);
        return new XmlComparisonExpr(left, right, op, isValueComparison);
    }

    private XmlQueryExpr ParseAdditive()
    {
        var left = this.ParseMultiplicative();
        while (true)
        {
            this.SkipWhitespace();
            if (this.Current is not ('+' or '-'))
                return left;
            var op = this.Current;
            this.index++;
            left = new XmlArithmeticExpr(left, this.ParseMultiplicative(), op);
        }
    }

    private XmlQueryExpr ParseMultiplicative()
    {
        var left = this.ParseUnary();
        while (true)
        {
            this.SkipWhitespace();
            if (this.Current == '*')
            {
                this.index++;
                left = new XmlArithmeticExpr(left, this.ParseUnary(), '*');
                continue;
            }
            if (this.TryOperatorWord("div"))
                left = new XmlArithmeticExpr(left, this.ParseUnary(), '/');
            else if (this.TryOperatorWord("idiv"))
                left = new XmlArithmeticExpr(left, this.ParseUnary(), 'i');
            else if (this.TryOperatorWord("mod"))
                left = new XmlArithmeticExpr(left, this.ParseUnary(), 'm');
            else
                return left;
        }
    }

    private XmlQueryExpr ParseUnary()
    {
        this.SkipWhitespace();
        if (this.Current is not ('-' or '+'))
            return this.ParsePathExpr();
        var negate = this.Current == '-';
        this.index++;
        var operand = this.ParseUnary();
        return negate ? new XmlArithmeticExpr(operand, null, '-') : operand;
    }

    private XmlQueryExpr ParsePathExpr()
    {
        this.SkipWhitespace();
        XmlQueryExpr start;
        var steps = new List<XmlStep>();

        if (this.TryConsume('/'))
        {
            start = new XmlRootExpr();
            if (this.TryConsume('/'))
            {
                steps.Add(DescendantOrSelfStep());
                steps.Add(this.ParseStep());
            }
            else if (this.StartsStep())
            {
                steps.Add(this.ParseStep());
            }
        }
        else if (this.StartsStep())
        {
            start = new XmlContextItemExpr();
            steps.Add(this.ParseStep());
        }
        else
        {
            start = this.ParsePrimary();
            var predicates = this.ParsePredicates();
            if (predicates.Length > 0)
                start = new XmlFilterExpr(start, predicates);
        }

        while (true)
        {
            this.SkipWhitespace();
            if (!this.TryConsume('/'))
                break;
            if (this.TryConsume('/'))
                steps.Add(DescendantOrSelfStep());
            steps.Add(this.ParseStep());
        }
        return steps.Count == 0 ? start : new XmlPathExpr(start, [.. steps]);
    }

    /// <summary>The <c>descendant-or-self::node()</c> step <c>//</c> expands to.</summary>
    private static XmlStep DescendantOrSelfStep() =>
        new(XmlAxis.DescendantOrSelf, XmlNodeTestKind.Node, string.Empty, string.Empty, []);

    /// <summary>
    /// Whether the cursor sits on a location step rather than a primary
    /// expression. A word followed by <c>(</c> is a step only for the four node
    /// tests; anything else there is a function call.
    /// </summary>
    private bool StartsStep()
    {
        var c = this.Current;
        if (c is '@' or '*')
            return true;
        if (c == '.')
            return this.Peek(1) == '.';
        if (!IsNameStart(c))
            return false;
        var word = this.PeekWord();
        if (this.StartsComputedConstructor(word))
            return false;
        var after = this.index + word.Length;
        while (after < this.text.Length && char.IsWhiteSpace(this.text[after]))
            after++;
        return after >= this.text.Length || this.text[after] != '(' || NodeTestKind(word) is not null;
    }

    /// <summary>
    /// Whether the cursor opens a computed constructor rather than a name test:
    /// one of the five keywords followed by <c>{</c>, or — for the three that
    /// name what they build — by a QName and then <c>{</c>.
    /// </summary>
    private bool StartsComputedConstructor(string word)
    {
        if (word is not ("attribute" or "comment" or "element" or "processing-instruction" or "text"))
            return false;
        var after = this.SkipSpaceFrom(this.index + word.Length);
        if (after < this.text.Length && this.text[after] == '{')
            return true;
        if (word is "comment" or "text")
            return false;

        var start = after;
        while (after < this.text.Length && IsNameChar(this.text[after]))
            after++;
        if (after == start)
            return false;
        after = this.SkipSpaceFrom(after);
        return after < this.text.Length && this.text[after] == '{';
    }

    private int SkipSpaceFrom(int position)
    {
        while (position < this.text.Length && char.IsWhiteSpace(this.text[position]))
            position++;
        return position;
    }

    private XmlStep ParseStep()
    {
        this.SkipWhitespace();
        if (this.Current == '.' && this.Peek(1) == '.')
        {
            this.index += 2;
            return new XmlStep(XmlAxis.Parent, XmlNodeTestKind.Node, string.Empty, string.Empty, this.ParsePredicates());
        }

        var axis = XmlAxis.Child;
        if (this.Current == '@')
        {
            axis = XmlAxis.Attribute;
            this.index++;
            this.SkipWhitespace();
        }

        if (this.Current == '*')
        {
            this.index++;
            return new XmlStep(axis, XmlNodeTestKind.Wildcard, string.Empty, string.Empty, this.ParsePredicates());
        }

        var name = this.ReadWord();
        if (NodeTestKind(name) is { } nodeTest && this.PeekIsOpenParen())
        {
            this.ConsumeEmptyArgumentList();
            return new XmlStep(axis, nodeTest, string.Empty, string.Empty, this.ParsePredicates());
        }

        var (local, uri) = this.ResolveName(name, axis == XmlAxis.Attribute);
        return new XmlStep(
            axis, XmlNodeTestKind.Name, local, uri, this.ParsePredicates(),
            this.schemaSingletonElements?.Contains(local) == true);
    }

    private XmlQueryExpr[] ParsePredicates()
    {
        List<XmlQueryExpr>? predicates = null;
        while (true)
        {
            this.SkipWhitespace();
            if (this.Current != '[')
                return predicates is null ? [] : [.. predicates];
            this.index++;
            this.predicateDepth++;
            var predicate = this.ParseExprSingle();
            this.predicateDepth--;
            this.SkipWhitespace();
            if (this.Current != ']')
                throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "]");
            this.index++;
            if (predicate.Kind is XmlStaticKind.String or XmlStaticKind.Untyped)
                throw SimulatedSqlException.XQueryPredicateNotBooleanOrNumeric(this.method, predicate.AtomizedTypeName());
            (predicates ??= []).Add(predicate);
        }
    }

    private XmlQueryExpr ParsePrimary()
    {
        this.SkipWhitespace();
        var c = this.Current;
        if (c == '(')
        {
            this.index++;
            this.SkipWhitespace();
            if (this.Current == ')')
            {
                this.index++;
                return new XmlSequenceExpr([]);
            }
            var inner = this.ParseExpr();
            this.SkipWhitespace();
            if (this.Current != ')')
                throw this.SyntaxError();
            this.index++;
            return inner;
        }
        if (c == '.')
        {
            this.index++;
            return new XmlContextItemExpr();
        }
        if (c is '"' or '\'')
            return new XmlLiteralExpr(this.ReadQuoted(c), XmlStaticKind.String, "xs:string");
        if (char.IsAsciiDigit(c))
            return this.ReadNumber();
        if (c == '<')
            return this.ParseElementConstructor();
        if (c == '$')
            return this.ResolveVariable(this.ReadVariableName());
        if (!IsNameStart(c))
            throw this.SyntaxError();

        var word = this.PeekWord();
        if (this.StartsComputedConstructor(word))
            return this.ParseComputedConstructor(word);

        var name = this.ReadWord();
        return this.PeekIsOpenParen() ? this.ParseFunctionCall(name) : throw this.SyntaxError();
    }

    /// <summary>
    /// The computed constructors. Real takes only the constant-QName form —
    /// <c>element {…} {…}</c> is Msg 9315 whatever the name expression holds —
    /// and refuses the comment and processing-instruction forms outright
    /// (Msg 9326 / 9325), in every XML method.
    /// </summary>
    private XmlConstructedNodeExpr ParseComputedConstructor(string word)
    {
        this.index += word.Length;
        this.SkipWhitespace();
        switch (word)
        {
            case "attribute":
                if (this.Current == '{')
                    throw SimulatedSqlException.XQueryComputedNameNotConstant(this.method);
                throw new NotSupportedException("A computed 'attribute name {…}' constructor in an XML query method is not modeled; write the attribute on a direct element constructor.");
            case "comment":
                throw SimulatedSqlException.XQueryComputedConstructorNotSupported(this.method, isComment: true);
            case "element":
                if (this.Current == '{')
                    throw SimulatedSqlException.XQueryComputedNameNotConstant(this.method);
                return this.ParseComputedElement(this.ReadWord());
            case "processing-instruction":
                throw SimulatedSqlException.XQueryComputedConstructorNotSupported(this.method, isComment: false);
            default:
                throw new NotSupportedException("A computed 'text {…}' constructor in an XML query method is not modeled.");
        }
    }

    /// <summary>
    /// <c>element name { … }</c>, compiled into the same literal-markup
    /// template a direct constructor uses so both serialize identically. The
    /// name resolves through the prolog exactly as a path step's does, so an
    /// undeclared prefix is Msg 2229 and an unprefixed name under a
    /// <c>declare default element namespace</c> prolog builds in that namespace.
    /// </summary>
    private XmlConstructedNodeExpr ParseComputedElement(string name)
    {
        this.ConstructsXml = true;
        var declarations = this.ConstructorDeclarations(name);
        var local = name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..];

        this.SkipWhitespace();
        if (this.Current != '{')
            throw this.SyntaxError();
        this.index++;
        this.SkipWhitespace();
        if (this.Current == '}')
        {
            this.index++;
            return new XmlConstructedNodeExpr([$"<{name}{declarations}/>"], [], [], local);
        }

        var content = this.ParseExpr();
        this.SkipWhitespace();
        if (this.Current != '}')
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "}");
        this.index++;
        return new XmlConstructedNodeExpr([$"<{name}{declarations}>", $"</{name}>"], [content], [false], local);
    }

    /// <summary>
    /// The namespace declaration a constructed element's own name needs: the
    /// prefix's binding, or the prolog's default element namespace for an
    /// unprefixed name.
    /// </summary>
    private string ConstructorDeclarations(string name)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
            return this.defaultNamespace is { } uri ? $" xmlns=\"{uri}\"" : string.Empty;
        var prefix = name[..colon];
        return this.prefixes.TryGetValue(prefix, out var mapped)
            ? $" xmlns:{prefix}=\"{mapped}\""
            : throw SimulatedSqlException.XQueryUndeclaredNamespace(this.method, prefix);
    }

    private XmlLiteralExpr ReadNumber()
    {
        var start = this.index;
        var fractional = false;
        while (this.index < this.text.Length && (char.IsAsciiDigit(this.text[this.index]) || this.text[this.index] == '.'))
        {
            fractional |= this.text[this.index] == '.';
            this.index++;
        }
        var span = this.text.AsSpan(start, this.index - start);
        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? new XmlLiteralExpr(number, XmlStaticKind.Number, fractional ? "xs:decimal" : "xs:integer")
            : throw this.SyntaxError();
    }

    private string ReadQuoted(char quote)
    {
        this.index++;
        var value = new StringBuilder();
        while (this.index < this.text.Length)
        {
            var c = this.text[this.index];
            if (c == quote)
            {
                // A doubled delimiter is XQuery's escape for one of itself.
                if (this.Peek(1) == quote)
                {
                    _ = value.Append(quote);
                    this.index += 2;
                    continue;
                }
                this.index++;
                return value.ToString();
            }
            _ = value.Append(c);
            this.index++;
        }
        throw this.SyntaxError();
    }

    private XmlFunctionCallExpr ParseFunctionCall(string name)
    {
        this.SkipWhitespace();
        this.index++;
        var arguments = new List<XmlQueryExpr>();
        this.SkipWhitespace();
        if (this.Current != ')')
        {
            arguments.Add(this.ParseExprSingle());
            this.SkipWhitespace();
            while (this.Current == ',')
            {
                this.index++;
                arguments.Add(this.ParseExprSingle());
                this.SkipWhitespace();
            }
        }
        if (this.Current != ')')
            throw this.SyntaxError();
        this.index++;
        return this.ResolveFunction(name, [.. arguments]);
    }

    /// <summary>
    /// Maps a function name onto the built-in library, applying each
    /// parameter's own singleton rule. A prefixed name resolves through the
    /// prolog first, so an undeclared prefix is Msg 2229 rather than Msg 2395.
    /// </summary>
    private XmlFunctionCallExpr ResolveFunction(string name, XmlQueryExpr[] arguments)
    {
        var local = name;
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var prefix = name[..colon];
            local = name[(colon + 1)..];
            switch (prefix)
            {
                case "fn":
                    break;
                case "sql":
                    throw new NotSupportedException($"XQuery accessor 'sql:{local}()' in a read method is not modeled.");
                case "xs":
                    throw new NotSupportedException($"XQuery constructor function 'xs:{local}()' is not modeled.");
                default:
                    if (!this.prefixes.TryGetValue(prefix, out var uri))
                        throw SimulatedSqlException.XQueryUndeclaredNamespace(this.method, prefix);
                    throw SimulatedSqlException.XQueryNoSuchFunction(this.method, uri, local);
            }
        }

        // Msg 2371: both read the sequence a predicate is filtering, so real
        // refuses them anywhere else — a FLWOR's return clause included
        // (probe-confirmed).
        return this.predicateDepth == 0 && local is "last" or "position"
            ? throw SimulatedSqlException.XQueryPositionOutsidePredicate(this.method, local)
            : this.BuildBuiltIn(local, arguments);
    }

    /// <summary>Checks one call against the library's signature for that name.</summary>
    private XmlFunctionCallExpr BuildBuiltIn(string local, XmlQueryExpr[] arguments) =>
        local switch
        {
            "avg" => this.Build(XmlFunctionId.Avg, arguments, local, 1, 1, XmlStaticKind.Number, "xs:decimal"),
            "ceiling" => this.Build(XmlFunctionId.Ceiling, arguments, local, 1, 1, XmlStaticKind.Number, "xs:decimal", XmlArgumentRule.Atomic),
            "concat" => this.Build(XmlFunctionId.Concat, arguments, local, 2, int.MaxValue, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            "contains" => this.Build(XmlFunctionId.Contains, arguments, local, 2, 2, XmlStaticKind.Boolean, "xs:boolean", XmlArgumentRule.Atomic),
            "count" => this.Build(XmlFunctionId.Count, arguments, local, 1, 1, XmlStaticKind.Number, "xs:integer"),
            "data" => this.Build(XmlFunctionId.Data, arguments, local, 1, 1, XmlStaticKind.Untyped, "xdt:untypedAtomic", occurrenceFromArgument: true),
            "distinct-values" => this.Build(XmlFunctionId.DistinctValues, arguments, local, 1, 1, XmlStaticKind.Untyped, "xdt:untypedAtomic", occurrence: XmlOccurrence.Many),
            "empty" => this.Build(XmlFunctionId.Empty, arguments, local, 1, 1, XmlStaticKind.Boolean, "xs:boolean"),
            "false" => this.Build(XmlFunctionId.False, arguments, local, 0, 0, XmlStaticKind.Boolean, "xs:boolean"),
            "floor" => this.Build(XmlFunctionId.Floor, arguments, local, 1, 1, XmlStaticKind.Number, "xs:decimal", XmlArgumentRule.Atomic),
            "last" => this.Build(XmlFunctionId.Last, arguments, local, 0, 0, XmlStaticKind.Number, "xs:integer"),
            "local-name" => this.Build(XmlFunctionId.LocalName, arguments, local, 0, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Item),
            "lower-case" => this.Build(XmlFunctionId.LowerCase, arguments, local, 1, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            "max" => this.Build(XmlFunctionId.Max, arguments, local, 1, 1, XmlStaticKind.Number, "xs:decimal"),
            "min" => this.Build(XmlFunctionId.Min, arguments, local, 1, 1, XmlStaticKind.Number, "xs:decimal"),
            "namespace-uri" => this.Build(XmlFunctionId.NamespaceUri, arguments, local, 0, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Item),
            "not" => this.Build(XmlFunctionId.Not, arguments, local, 1, 1, XmlStaticKind.Boolean, "xs:boolean", XmlArgumentRule.Condition),
            "number" => this.Build(XmlFunctionId.Number, arguments, local, 0, 1, XmlStaticKind.Number, "xs:decimal", XmlArgumentRule.Atomic),
            "position" => this.Build(XmlFunctionId.Position, arguments, local, 0, 0, XmlStaticKind.Number, "xs:integer"),
            "round" => this.Build(XmlFunctionId.Round, arguments, local, 1, 1, XmlStaticKind.Number, "xs:decimal", XmlArgumentRule.Atomic),
            "string" => this.Build(XmlFunctionId.String, arguments, local, 0, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Item),
            "string-length" => this.Build(XmlFunctionId.StringLength, arguments, local, 0, 1, XmlStaticKind.Number, "xs:decimal", XmlArgumentRule.Atomic),
            "substring" => this.Build(XmlFunctionId.Substring, arguments, local, 2, 3, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            "sum" => this.Build(XmlFunctionId.Sum, arguments, local, 1, 1, XmlStaticKind.Number, "xs:decimal"),
            "true" => this.Build(XmlFunctionId.True, arguments, local, 0, 0, XmlStaticKind.Boolean, "xs:boolean"),
            "upper-case" => this.Build(XmlFunctionId.UpperCase, arguments, local, 1, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            _ => throw SimulatedSqlException.XQueryNoSuchFunction(this.method, FunctionNamespace, local),
        };

    /// <summary>
    /// Checks one call against its signature and builds it. Arity is part of
    /// the signature on real too, so too few arguments is Msg 2236 and too many
    /// Msg 2238.
    /// </summary>
    private XmlFunctionCallExpr Build(
        XmlFunctionId id,
        XmlQueryExpr[] arguments,
        string name,
        int minimumArity,
        int maximumArity,
        XmlStaticKind kind,
        string typeName,
        XmlArgumentRule rule = XmlArgumentRule.Sequence,
        XmlOccurrence occurrence = XmlOccurrence.ExactlyOne,
        bool occurrenceFromArgument = false)
    {
        if (arguments.Length < minimumArity)
            throw SimulatedSqlException.XQueryTooFewArguments(this.method, name);
        if (arguments.Length > maximumArity)
            throw SimulatedSqlException.XQueryTooManyArguments(this.method, name);

        foreach (var argument in arguments)
        {
            switch (rule)
            {
                case XmlArgumentRule.Atomic:
                    RequireSingleton(argument, $"{name}()", this.method);
                    break;
                case XmlArgumentRule.Condition:
                    this.RequireCondition(argument);
                    break;
                case XmlArgumentRule.Item when argument.Occurrence == XmlOccurrence.Many:
                    // A parameter typed item()? quotes the node static type
                    // rather than the atomized one (probe-confirmed for
                    // string()).
                    throw SimulatedSqlException.XQueryNotSingleton(this.method, $"{name}()", argument.NodeTypeName());
                default:
                    break;
            }
        }
        return new XmlFunctionCallExpr(
            id,
            arguments,
            kind,
            occurrenceFromArgument ? arguments[0].Occurrence : occurrence,
            typeName);
    }

    /// <summary>
    /// Msg 2389: an operand real types as more than one item where the
    /// construct admits at most one.
    /// </summary>
    internal static void RequireSingleton(XmlQueryExpr operand, string construct, string method)
    {
        if (operand.Occurrence == XmlOccurrence.Many)
            throw SimulatedSqlException.XQueryNotSingleton(method, construct, operand.AtomizedTypeName());
    }

    /// <summary>
    /// Msg 2234: two operands whose static types are both known and don't
    /// compare. Untyped operands take their type from the other side, so only a
    /// pair of typed operands can mismatch.
    /// </summary>
    private static void RequireComparableTypes(XmlQueryExpr left, XmlQueryExpr right, string op, string method)
    {
        if (left.Kind is XmlStaticKind.Node or XmlStaticKind.Untyped || right.Kind is XmlStaticKind.Node or XmlStaticKind.Untyped)
            return;
        if (left.Kind != right.Kind)
            throw SimulatedSqlException.XQueryOperatorTypeMismatch(method, op, left.TypeName, right.TypeName);
    }

    private (string? Operator, bool IsValueComparison) TryComparisonOperator()
    {
        this.SkipWhitespace();
        switch (this.Current)
        {
            case '!' when this.Peek(1) == '=':
                this.index += 2;
                return ("!=", false);
            case '<':
                this.index++;
                return this.TryConsume('=') ? ("<=", false) : ("<", false);
            case '=':
                this.index++;
                return ("=", false);
            case '>':
                this.index++;
                return this.TryConsume('=') ? (">=", false) : (">", false);
            default:
                break;
        }

        foreach (var word in ValueComparisonOperators)
        {
            if (this.TryOperatorWord(word))
                return (word, true);
        }
        this.RejectUnsupportedSyntax();
        return (null, false);
    }

    /// <summary>The value-comparison operator words, in the order they're tried.</summary>
    private static readonly string[] ValueComparisonOperators = ["eq", "ge", "gt", "le", "lt", "ne"];

    /// <summary>
    /// The XQuery operator words real names in Msg 9335 rather than evaluating.
    /// </summary>
    private static readonly string[] UnsupportedOperatorWords = ["castable as", "except", "intersect", "to", "treat as", "union"];

    /// <summary>
    /// Msg 9335: an operator real parses but refuses. <c>|</c> reports as
    /// <c>union</c>, which is the word real quotes (probe-confirmed).
    /// </summary>
    private void RejectUnsupportedSyntax()
    {
        this.SkipWhitespace();
        if (this.Current == '|')
            throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, "union");
        var word = this.PeekWord();
        if (word.Length == 0)
            return;
        foreach (var unsupported in UnsupportedOperatorWords)
        {
            if (unsupported.StartsWith(word, StringComparison.Ordinal)
                && this.text.AsSpan(this.index).StartsWith(unsupported, StringComparison.Ordinal))
            {
                throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, unsupported);
            }
        }
    }

    /// <summary>Consumes <paramref name="word"/> when it sits at the cursor as a whole word.</summary>
    private bool TryOperatorWord(string word)
    {
        this.SkipWhitespace();
        if (!string.Equals(this.PeekWord(), word, StringComparison.Ordinal))
            return false;
        this.index += word.Length;
        return true;
    }

    /// <summary>Whether <paramref name="word"/> sits at the cursor, without consuming it.</summary>
    private bool AtWord(string word)
    {
        this.SkipWhitespace();
        return string.Equals(this.PeekWord(), word, StringComparison.Ordinal);
    }

    /// <summary>Consumes the <c>:=</c> a <c>let</c> binding takes.</summary>
    private bool TryConsumeAssign()
    {
        this.SkipWhitespace();
        if (this.Current != ':' || this.Peek(1) != '=')
            return false;
        this.index += 2;
        return true;
    }

    /// <summary>Reads a <c>$name</c> reference, answering the name without the sigil.</summary>
    private string ReadVariableName()
    {
        this.index++;
        var start = this.index;
        while (this.index < this.text.Length && IsNameChar(this.text[this.index]))
            this.index++;
        return this.index == start ? throw this.SyntaxError() : this.text[start..this.index];
    }

    /// <summary>
    /// Binds a reference to the innermost binding of that name — which is what
    /// makes an inner <c>let</c> shadow an outer <c>for</c> — or Msg 2227.
    /// </summary>
    private XmlVariableRefExpr ResolveVariable(string name)
    {
        for (var i = this.scope.Count - 1; i >= 0; i--)
        {
            if (string.Equals(this.scope[i].Name, name, StringComparison.Ordinal))
                return new XmlVariableRefExpr(this.scope[i]);
        }
        throw SimulatedSqlException.XQueryVariableNotFound(this.method, name);
    }

    /// <summary>
    /// The token real quotes in a syntax error at the cursor: a variable
    /// reference with its sigil, otherwise a whole word, a single character, or
    /// <c>&lt;eof&gt;</c>.
    /// </summary>
    private string CurrentToken()
    {
        this.SkipWhitespace();
        if (this.index >= this.text.Length)
            return "<eof>";
        if (this.Current == '$')
        {
            var end = this.index + 1;
            while (end < this.text.Length && IsNameChar(this.text[end]))
                end++;
            return this.text[this.index..end];
        }
        if (this.Current is '"' or '\'')
        {
            // Real names a string literal by its content, delimiters dropped.
            var quote = this.Current;
            var end = this.index + 1;
            while (end < this.text.Length && this.text[end] != quote)
                end++;
            return this.text[(this.index + 1)..end];
        }
        var word = this.PeekWord();
        return word.Length > 0 ? word : this.text[this.index].ToString();
    }

    /// <summary>
    /// Scans a direct element constructor, keeping its markup as literal
    /// segments with the <c>{…}</c> enclosed expressions between them. Doubled
    /// braces are XQuery's escape for a literal brace, and an expression inside
    /// a quoted attribute value is marked so it atomizes rather than splicing
    /// markup.
    /// </summary>
    private XmlConstructedNodeExpr ParseElementConstructor()
    {
        this.ConstructsXml = true;
        var literals = new List<string>();
        var enclosed = new List<XmlQueryExpr>();
        var inAttribute = new List<bool>();
        var segment = new StringBuilder();
        var depth = 0;
        var inTag = false;
        var closingTag = false;
        var quote = '\0';
        while (this.index < this.text.Length)
        {
            var c = this.text[this.index];
            if (c is '{' or '}' && this.Peek(1) == c)
            {
                _ = segment.Append(c);
                this.index += 2;
                continue;
            }
            if (c == '{')
            {
                literals.Add(segment.ToString());
                _ = segment.Clear();
                this.index++;
                enclosed.Add(this.ParseExpr());
                inAttribute.Add(quote != '\0');
                this.SkipWhitespace();
                if (this.Current != '}')
                    throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "}");
                this.index++;
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                _ = segment.Append(c);
                this.index++;
                continue;
            }
            if (!inTag)
            {
                if (c == '<')
                {
                    inTag = true;
                    closingTag = this.Peek(1) == '/';
                }
                _ = segment.Append(c);
                this.index++;
                continue;
            }
            if (c is '"' or '\'')
                quote = c;
            if (c == '/' && this.Peek(1) == '>')
            {
                _ = segment.Append("/>");
                this.index += 2;
                inTag = false;
                if (depth == 0)
                    return this.Finish(literals, enclosed, inAttribute, segment);
                continue;
            }
            if (c == '>')
            {
                _ = segment.Append('>');
                this.index++;
                inTag = false;
                if (!closingTag)
                {
                    depth++;
                    continue;
                }
                depth--;
                if (depth == 0)
                    return this.Finish(literals, enclosed, inAttribute, segment);
                continue;
            }
            _ = segment.Append(c);
            this.index++;
        }
        throw this.SyntaxError();
    }

    private XmlConstructedNodeExpr Finish(
        List<string> literals,
        List<XmlQueryExpr> enclosed,
        List<bool> inAttribute,
        StringBuilder segment)
    {
        literals.Add(segment.ToString());
        var name = ConstructedElementName(literals[0]);
        literals[0] = this.DeclarePrologNamespaces(literals[0], name, literals);
        return new XmlConstructedNodeExpr([.. literals], [.. enclosed], [.. inAttribute], name);
    }

    /// <summary>
    /// Writes the prolog's namespace bindings onto a direct constructor's
    /// outermost element, so its names resolve the way a path step's do — the
    /// default element namespace when one is declared, plus each declared
    /// prefix the markup actually writes (a declaration nothing uses is
    /// omitted, as real omits it).
    /// </summary>
    private string DeclarePrologNamespaces(string opening, string name, List<string> literals)
    {
        var declarations = new StringBuilder();
        if (this.defaultNamespace is { } uri)
            _ = declarations.Append(" xmlns=\"").Append(uri).Append('"');
        foreach (var (prefix, mapped) in this.prefixes)
        {
            if (literals.Exists(literal => literal.Contains(prefix + ":", StringComparison.Ordinal)))
                _ = declarations.Append(" xmlns:").Append(prefix).Append("=\"").Append(mapped).Append('"');
        }
        if (declarations.Length == 0)
            return opening;
        var end = opening.IndexOf('<', StringComparison.Ordinal) + 1 + name.Length;
        return opening[..end] + declarations.ToString() + opening[end..];
    }

    /// <summary>The constructed element's name, which its static type quotes.</summary>
    private static string ConstructedElementName(string opening)
    {
        var start = opening.IndexOf('<', StringComparison.Ordinal) + 1;
        var end = start;
        while (end < opening.Length && IsNameChar(opening[end]))
            end++;
        return opening[start..end];
    }

    private static XmlNodeTestKind? NodeTestKind(string word) => word switch
    {
        "comment" => XmlNodeTestKind.Comment,
        "node" => XmlNodeTestKind.Node,
        "processing-instruction" => XmlNodeTestKind.ProcessingInstruction,
        "text" => XmlNodeTestKind.Text,
        _ => null,
    };

    /// <summary>
    /// Splits a name test into local name and namespace URI. An unprefixed
    /// element name takes the prolog's default element namespace; an attribute
    /// never does (XQuery's scoping rule).
    /// </summary>
    private (string Local, string Uri) ResolveName(string name, bool isAttribute)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
            return (name, isAttribute ? string.Empty : this.defaultNamespace ?? string.Empty);
        var prefix = name[..colon];
        return this.prefixes.TryGetValue(prefix, out var uri)
            ? (name[(colon + 1)..], uri)
            : throw SimulatedSqlException.XQueryUndeclaredNamespace(this.method, prefix);
    }

    private void ConsumeEmptyArgumentList()
    {
        this.SkipWhitespace();
        this.index++;
        this.SkipWhitespace();
        if (this.Current != ')')
            throw this.SyntaxError();
        this.index++;
    }

    private bool PeekIsOpenParen()
    {
        var peek = this.index;
        while (peek < this.text.Length && char.IsWhiteSpace(this.text[peek]))
            peek++;
        return peek < this.text.Length && this.text[peek] == '(';
    }

    private string ReadWord()
    {
        var word = this.PeekWord();
        if (word.Length == 0)
            throw this.SyntaxError();
        if (word.Contains("::", StringComparison.Ordinal))
            throw new NotSupportedException($"XQuery axis step '{word}' is not modeled.");
        this.index += word.Length;
        return word;
    }

    private string PeekWord()
    {
        if (!IsNameStart(this.Current))
            return string.Empty;
        var end = this.index;
        while (end < this.text.Length && IsNameChar(this.text[end]))
            end++;
        return this.text[this.index..end];
    }

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':';

    /// <summary>Consumes <paramref name="c"/> when it sits at the cursor.</summary>
    private bool TryConsume(char c)
    {
        if (this.index >= this.text.Length || this.text[this.index] != c)
            return false;
        this.index++;
        return true;
    }

    private char Current => this.index < this.text.Length ? this.text[this.index] : '\0';

    private char Peek(int offset) => this.index + offset < this.text.Length ? this.text[this.index + offset] : '\0';

    private void SkipWhitespace()
    {
        while (this.index < this.text.Length && char.IsWhiteSpace(this.text[this.index]))
            this.index++;
    }

    /// <summary>
    /// Msg 2209 naming the token at the cursor — real quotes the offending
    /// word, or <c>&lt;eof&gt;</c> when the text ran out.
    /// </summary>
    private SimulatedSqlException SyntaxError()
    {
        this.SkipWhitespace();
        if (this.index >= this.text.Length)
            return SimulatedSqlException.XQuerySyntaxError(this.method, "<eof>");
        var word = this.PeekWord();
        return SimulatedSqlException.XQuerySyntaxError(this.method, word.Length > 0 ? word : this.text[this.index].ToString());
    }
}
