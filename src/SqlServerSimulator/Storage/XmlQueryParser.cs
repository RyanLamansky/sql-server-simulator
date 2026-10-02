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
    XmlStaticTyping? typing = null,
    XmlSqlAccessorScope? sqlAccessors = null)
{
    /// <summary>The XQuery namespace an unprefixed function name lives in.</summary>
    private const string FunctionNamespace = "http://www.w3.org/2004/07/xpath-functions";

    private readonly string text = text;
    private readonly string? defaultNamespace = defaultNamespace;
    private readonly Dictionary<string, string> prefixes = prefixes;
    private readonly string method = method;

    /// <summary>
    /// The receiver's XML schema collection typing, or null for an untyped
    /// receiver. A named child step the collection declares at most once is a
    /// singleton to the static type checker — what makes <c>.value()</c>
    /// accept a schema-typed path real accepts — and a step naming a simply
    /// typed element or attribute carries that type.
    /// </summary>
    private readonly XmlStaticTyping? typing = typing;

    /// <summary>
    /// Where a <c>sql:</c> accessor records the slot it reads, or null in a
    /// read method — which is what keeps the accessors refused there.
    /// </summary>
    private readonly XmlSqlAccessorScope? sqlAccessors = sqlAccessors;

    /// <summary>The <c>$</c>-variable bindings in scope, innermost last.</summary>
    private readonly List<XmlVariableBinding> scope = [];

    private int index;
    private int slotCount;
    private int predicateDepth;

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

            var source = this.ParseExprSingle();
            RequireNotConstructed(source, $"'{keyword}'", this.method);
            var binding = new XmlVariableBinding(name, this.slotCount++, source, perItem);
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
            RequireNotConstructed(key, "data()", this.method);
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
        var (op, form) = this.TryComparisonOperator();
        if (op is null)
            return left;

        var right = this.ParseAdditive();
        if (form == XmlComparisonForm.Node)
        {
            // The node comparisons read identity and document order, so both
            // operands are single nodes; the empty sequence is a type mismatch
            // real names 'empty' (probe-confirmed).
            if (left is XmlSequenceExpr { IsEmpty: true } || right is XmlSequenceExpr { IsEmpty: true })
                throw SimulatedSqlException.XQueryOperatorTypeMismatch(this.method, op, left.NodeTypeName(), right.NodeTypeName());
            if (IsPlural(left))
                throw SimulatedSqlException.XQueryNotSingleton(this.method, op, left.NodeTypeName());
            if (IsPlural(right))
                throw SimulatedSqlException.XQueryNotSingleton(this.method, op, right.NodeTypeName());
            return new XmlNodeComparisonExpr(left, right, op);
        }

        RequireNotConstructed(left, "data()", this.method);
        RequireNotConstructed(right, "data()", this.method);
        if (form == XmlComparisonForm.Value)
        {
            RequireSingleton(left, op, this.method);
            RequireSingleton(right, op, this.method);
        }
        RequireComparableTypes(left, right, op, this.method);
        return new XmlComparisonExpr(left, right, op, form == XmlComparisonForm.Value);
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
            left = this.Arithmetic(left, this.ParseMultiplicative(), op, op.ToString());
        }
    }

    private XmlQueryExpr ParseMultiplicative()
    {
        var left = this.ParseInstanceOf();
        while (true)
        {
            this.SkipWhitespace();
            if (this.Current == '*')
            {
                this.index++;
                left = this.Arithmetic(left, this.ParseInstanceOf(), '*', "*");
                continue;
            }
            if (this.TryOperatorWord("div"))
                left = this.Arithmetic(left, this.ParseInstanceOf(), '/', "div");
            else if (this.AtWord("idiv"))
                throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, "idiv");
            else if (this.TryOperatorWord("mod"))
                left = this.Arithmetic(left, this.ParseInstanceOf(), 'm', "mod");
            else
                return left;
        }
    }

    /// <summary>
    /// Types one arithmetic operator: each operand must be numeric or untyped
    /// (Msg 9308 otherwise, quoting the operand), a constructed operand is Msg
    /// 2373 (atomization), and the result takes XQuery's promotion of the two.
    /// </summary>
    private XmlArithmeticExpr Arithmetic(XmlQueryExpr left, XmlQueryExpr? right, char op, string written)
    {
        var leftRank = this.ArithmeticRank(left, written);
        var rightRank = right is null ? leftRank : this.ArithmeticRank(right, written);
        var rank = Math.Max(leftRank, rightRank);
        if (rank < 1)
            rank = 4;
        if (op == '/' && rank == 1)
            rank = 2;
        return new XmlArithmeticExpr(left, right, op, XmlAtomicTypes.RankTypeName(rank));
    }

    /// <summary>
    /// An operand's promotion rank. A <c>sql:column</c> whose type the
    /// statement couldn't supply while this compiled — a <c>.modify()</c>'s
    /// <c>with</c> naming the FROM clause an UPDATE parses later — is neutral,
    /// taking the other operand's type: real knows the column's type there,
    /// and it is AdventureWorks' <c>data(…)[1] + sql:column("inserted.LineTotal")</c>
    /// that must stay <c>xs:decimal</c>.
    /// </summary>
    private int ArithmeticRank(XmlQueryExpr operand, string written)
    {
        RequireNotConstructed(operand, "data()", this.method);
        RequireSingleton(operand, written, this.method);
        if (operand is XmlSqlAccessorExpr { TypeUnknown: true })
            return 0;
        var kind = operand.AtomizedKind();
        if (kind == XmlStaticKind.Untyped)
            return 4;
        var rank = kind == XmlStaticKind.Number ? XmlAtomicTypes.NumericRank(operand.TypeName) : 0;
        if (rank == 0 && kind == XmlStaticKind.Number)
            rank = 2;
        return rank > 0 ? rank : throw SimulatedSqlException.XQueryArithmeticOperandType(this.method, written, operand.AtomizedTypeName());
    }

    /// <summary>
    /// Msg 2373: <paramref name="operand"/> may hold a constructed node, which
    /// real can't feed to <paramref name="operation"/>.
    /// </summary>
    internal static void RequireNotConstructed(XmlQueryExpr operand, string operation, string method)
    {
        if (operand.Constructed)
            throw SimulatedSqlException.XQueryConstructedXmlNotSupported(method, operation);
    }

    /// <summary>
    /// <c>expr instance of SequenceType</c>, which XQuery places between the
    /// multiplicative operators and <c>cast as</c>. Real requires a
    /// statically singular operand whatever occurrence the type writes.
    /// </summary>
    private XmlQueryExpr ParseInstanceOf()
    {
        var operand = this.ParseCast();
        if (!this.AtWord("instance") || !this.FollowedByWord("instance", "of"))
            return operand;
        this.index += "instance".Length;
        _ = this.TryOperatorWord("of");

        RequireNotConstructed(operand, "'instance of'", this.method);
        if (IsPlural(operand))
            throw SimulatedSqlException.XQueryNotSingleton(this.method, "instance of", operand.NodeTypeName());
        return new XmlInstanceOfExpr(operand, this.ParseSequenceType());
    }

    private static bool IsPlural(XmlQueryExpr operand) => XmlQueryExpr.IsPlural(operand.Occurrence);

    /// <summary>
    /// A sequence type: <c>empty()</c>, or an item type — an atomic type name
    /// or a kind test — with an optional occurrence indicator.
    /// </summary>
    private XmlSequenceType ParseSequenceType()
    {
        this.SkipWhitespace();
        var name = this.PeekWord();
        if (name.Length == 0)
            throw this.SyntaxError();
        this.index += name.Length;

        XmlSequenceType type;
        if (this.PeekIsOpenParen())
        {
            this.SkipWhitespace();
            this.index++;
            this.SkipWhitespace();
            var argument = string.Empty;
            if (this.Current == '*')
            {
                argument = "*";
                this.index++;
            }
            else if (IsNameStart(this.Current))
            {
                argument = this.ReadWord();
            }
            this.SkipWhitespace();
            if (this.Current == ',')
            {
                // element(name, type): the type half names a schema type, which
                // an untyped instance never matches beyond xdt:untyped / xs:anyType.
                this.index++;
                this.SkipWhitespace();
                _ = this.ReadWord();
                this.SkipWhitespace();
            }
            if (this.Current != ')')
                throw this.SyntaxError();
            this.index++;
            type = name switch
            {
                "attribute" => XmlSequenceType.KindTest(XmlSequenceKind.Attribute, argument is "*" ? string.Empty : this.ResolveName(argument, isAttribute: true).Local),
                "comment" => XmlSequenceType.KindTest(XmlSequenceKind.Comment, string.Empty),
                "document-node" => throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, "document-node()"),
                "element" => XmlSequenceType.KindTest(XmlSequenceKind.Element, argument is "*" ? string.Empty : this.ResolveName(argument, isAttribute: false).Local),
                "empty" => XmlSequenceType.KindTest(XmlSequenceKind.Empty, string.Empty),
                "item" => XmlSequenceType.KindTest(XmlSequenceKind.Item, string.Empty),
                "node" => XmlSequenceType.KindTest(XmlSequenceKind.Node, string.Empty),
                "processing-instruction" => XmlSequenceType.KindTest(XmlSequenceKind.ProcessingInstruction, string.Empty),
                "text" => XmlSequenceType.KindTest(XmlSequenceKind.Text, string.Empty),
                _ => throw this.SyntaxError(),
            };
        }
        else
        {
            type = XmlSequenceType.Atomic(name, this.ResolveAtomicType(name));
        }

        this.SkipWhitespace();
        if (this.Current is '?' or '*' or '+' && type.Kind != XmlSequenceKind.Empty)
        {
            type = type.WithOccurrence(this.Current);
            this.index++;
        }
        return type;
    }

    /// <summary>
    /// Resolves an atomic type name — <c>xs:local</c> or <c>xdt:untypedAtomic</c>
    /// — to its built-in simple type (null for the untyped atomic type), or Msg
    /// 2232 when there is none.
    /// </summary>
    private System.Xml.Schema.XmlSchemaSimpleType? ResolveAtomicType(string name)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? string.Empty : name[..colon];
        var local = name[(colon + 1)..];
        if (prefix == "xdt" && local == "untypedAtomic")
            return null;
        if (prefix == "xs" && XmlAtomicTypes.Resolve(local) is { } type)
            return type;
        if (prefix.Length > 0 && prefix is not ("xs" or "xdt") && !this.prefixes.ContainsKey(prefix))
            throw SimulatedSqlException.XQueryUndeclaredNamespace(this.method, prefix);
        throw SimulatedSqlException.XQueryUndefinedType(this.method, name);
    }

    /// <summary>
    /// <c>expr cast as xs:type?</c>. Real accepts only the optional form (Msg
    /// 9301 otherwise), types the operand as at most one item (Msg 2365
    /// quoting it otherwise), and settles a literal operand while compiling.
    /// </summary>
    private XmlQueryExpr ParseCast()
    {
        var operand = this.ParseUnary();
        if (!this.AtWord("cast") || !this.FollowedByWord("cast", "as"))
            return operand;
        this.index += "cast".Length;
        _ = this.TryOperatorWord("as");
        this.SkipWhitespace();
        var name = this.PeekWord();
        if (name.Length == 0)
            throw this.SyntaxError();
        this.index += name.Length;
        var target = this.ResolveAtomicType(name);
        this.SkipWhitespace();
        if (this.Current != '?')
            throw SimulatedSqlException.XQueryCastRequiresOptional(this.method);
        this.index++;
        return this.BuildCast(operand, name, target, $"{name} ?");
    }

    /// <summary>
    /// The shared half of <c>cast as</c> and a constructor function: the
    /// operand's static checks, and a literal operand converted — or refused
    /// with Msg 9319 — right here.
    /// </summary>
    private XmlCastExpr BuildCast(XmlQueryExpr operand, string targetName, System.Xml.Schema.XmlSchemaSimpleType? target, string quotedTarget)
    {
        RequireNotConstructed(operand, "data()", this.method);
        if (operand is XmlSequenceExpr { IsEmpty: true } || IsPlural(operand))
            throw SimulatedSqlException.XQueryCannotConvert(this.method, operand.AtomizedTypeName(), quotedTarget);
        if (target is { Datatype.TypeCode: System.Xml.Schema.XmlTypeCode.QName or System.Xml.Schema.XmlTypeCode.Notation })
            throw SimulatedSqlException.XQueryCannotConvert(this.method, operand.AtomizedTypeName(), quotedTarget);

        var cast = new XmlCastExpr(operand, target, targetName);
        if (operand is XmlLiteralExpr literal && XmlCastExpr.Convert(literal.Value, target, targetName) is null)
            throw SimulatedSqlException.XQueryStaticInvalidValue(this.method, XmlQueryValues.StringValue(literal.Value));
        return cast;
    }

    /// <summary>Whether <paramref name="second"/> follows <paramref name="first"/> at the cursor as the next word.</summary>
    private bool FollowedByWord(string first, string second)
    {
        var after = this.SkipSpaceFrom(this.index + first.Length);
        if (!this.text.AsSpan(after).StartsWith(second, StringComparison.Ordinal))
            return false;
        var end = after + second.Length;
        return end >= this.text.Length || !IsNameChar(this.text[end]);
    }

    private XmlQueryExpr ParseUnary()
    {
        this.SkipWhitespace();
        if (this.Current is not ('-' or '+'))
            return this.ParsePathExpr();
        var negate = this.Current == '-';
        this.index++;
        var operand = this.ParseUnary();
        return negate ? this.Arithmetic(operand, null, '-', "-") : operand;
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
            this.SkipWhitespace();
            if (this.Current == '[')
                RequireNotConstructed(start, "'[]'", this.method);
            var predicates = this.ParsePredicates();
            if (predicates.Length > 0)
                start = new XmlFilterExpr(start, predicates);
        }

        while (true)
        {
            this.SkipWhitespace();
            if (this.Current != '/')
                break;
            RequireNotConstructed(start, "'/'", this.method);
            this.index++;
            if (this.TryConsume('/'))
                steps.Add(DescendantOrSelfStep());
            var step = this.ParseStep();
            this.RequireSelfStepPossible(start, steps, step);
            this.RequireTextStepPossible(start, steps, step);
            steps.Add(step);
        }
        return steps.Count == 0 ? start : new XmlPathExpr(start, [.. steps]);
    }

    /// <summary>
    /// Msg 9312: a <c>text()</c> step under an element the schema types with
    /// simple content — real keeps such an element's value as a typed value,
    /// not a text node, so the step can never match.
    /// </summary>
    private void RequireTextStepPossible(XmlQueryExpr start, List<XmlStep> steps, XmlStep step)
    {
        if (step.TestKind != XmlNodeTestKind.Text || step.Axis != XmlAxis.Child || steps.Count == 0)
            return;
        var previous = steps[^1];
        if (previous.Axis == XmlAxis.Attribute || previous.TypeName is null)
            return;
        var occurrence = start.Occurrence;
        foreach (var earlier in steps)
            occurrence = XmlQueryExpr.Combine(occurrence, earlier.Occurrence);
        throw SimulatedSqlException.XQueryTextOnSimpleTypedElement(this.method, previous.NodeTypeBase() + XmlQueryExpr.OccurrenceSuffix(occurrence));
    }

    /// <summary>
    /// Msg 2261: a <c>self::name</c> step after a step whose static type is an
    /// element of a different name, which real settles from the types alone.
    /// </summary>
    private void RequireSelfStepPossible(XmlQueryExpr start, List<XmlStep> steps, XmlStep step)
    {
        if (step.Axis != XmlAxis.Self || step.TestKind != XmlNodeTestKind.Name || steps.Count == 0)
            return;
        var previous = steps[^1];
        if (previous.Axis != XmlAxis.Child || previous.TestKind != XmlNodeTestKind.Name || previous.LocalName == step.LocalName)
            return;
        var occurrence = start.Occurrence;
        foreach (var earlier in steps)
            occurrence = XmlQueryExpr.Combine(occurrence, earlier.Occurrence);
        throw SimulatedSqlException.XQueryNoSuchElementInType(
            this.method, step.LocalName, previous.NodeTypeBase() + XmlQueryExpr.OccurrenceSuffix(occurrence));
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
        if (word.Contains("::", StringComparison.Ordinal))
            return true;
        var after = this.index + word.Length;
        if (word is "typeswitch" or "validate" or "ordered" or "unordered")
        {
            var next = this.SkipSpaceFrom(after);
            if (next < this.text.Length && this.text[next] is '(' or '{')
                return false;
        }
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
        else if (this.PeekWord() is var word && word.IndexOf("::", StringComparison.Ordinal) is var split and >= 0)
        {
            axis = this.ResolveAxis(word[..split]);
            this.index += split + 2;
        }

        if (this.Current == '*')
        {
            this.index++;

            // `*:local` names the local part in any namespace (probed
            // 2026-10-02 against SQL Server 2025).
            if (this.index + 1 < this.text.Length && this.text[this.index] == ':' && IsNameStart(this.text[this.index + 1]))
            {
                this.index++;
                var anyNamespaceLocal = this.ReadWord();
                return new XmlStep(axis, XmlNodeTestKind.AnyNamespace, anyNamespaceLocal, string.Empty, this.ParsePredicates());
            }
            return new XmlStep(axis, XmlNodeTestKind.Wildcard, string.Empty, string.Empty, this.ParsePredicates());
        }

        var name = this.ReadWord();

        // `prefix:*` is every local name in the prefix's namespace.
        if (name.EndsWith(':') && this.Current == '*')
        {
            this.index++;
            var prefix = name[..^1];
            return this.prefixes.TryGetValue(prefix, out var prefixUri)
                ? new XmlStep(axis, XmlNodeTestKind.AnyLocalName, string.Empty, prefixUri, this.ParsePredicates())
                : throw SimulatedSqlException.XQueryUndeclaredNamespace(this.method, prefix);
        }
        if (NodeTestKind(name) is { } nodeTest && this.PeekIsOpenParen())
        {
            this.ConsumeEmptyArgumentList();
            return new XmlStep(axis, nodeTest, string.Empty, string.Empty, this.ParsePredicates());
        }

        // An axis written with space before its `::` isn't one; real stops at
        // the word (probe-confirmed: `child :: x` is Msg 2209 near 'child').
        var after = this.SkipSpaceFrom(this.index);
        if (this.text.AsSpan(after).StartsWith("::", StringComparison.Ordinal))
            throw SimulatedSqlException.XQuerySyntaxError(this.method, name);

        var (local, uri) = this.ResolveName(name, axis == XmlAxis.Attribute);
        string? typeName = null;
        _ = axis == XmlAxis.Attribute
            ? this.typing?.AttributeTypes.TryGetValue(local, out typeName)
            : this.typing?.ElementTypes.TryGetValue(local, out typeName);
        return new XmlStep(
            axis, XmlNodeTestKind.Name, local, uri, this.ParsePredicates(),
            axis == XmlAxis.Child && this.typing?.SingletonElements.Contains(local) == true,
            typeName);
    }

    /// <summary>
    /// A named axis. Real evaluates the six forward-and-parent axes, parses the
    /// reverse and sibling ones only to refuse them (Msg 9335), and reports
    /// anything else — <c>namespace::</c> included — as no axis at all (Msg
    /// 2392), all probe-confirmed.
    /// </summary>
    private XmlAxis ResolveAxis(string name) => name switch
    {
        "attribute" => XmlAxis.Attribute,
        "child" => XmlAxis.Child,
        "descendant" => XmlAxis.Descendant,
        "descendant-or-self" => XmlAxis.DescendantOrSelf,
        "parent" => XmlAxis.Parent,
        "self" => XmlAxis.Self,
        "ancestor" or "ancestor-or-self" or "following" or "following-sibling" or "preceding" or "preceding-sibling"
            => throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, name),
        _ => throw SimulatedSqlException.XQueryInvalidAxis(this.method, name),
    };

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
        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(this.Peek(1))))
            return this.ReadNumber();
        if (c == '<')
        {
            if (this.text.AsSpan(this.index).StartsWith("<!--", StringComparison.Ordinal))
                return this.ParseDirectComment();
            if (this.text.AsSpan(this.index).StartsWith("<?", StringComparison.Ordinal))
                return this.ParseDirectProcessingInstruction();
            if (this.text.AsSpan(this.index).StartsWith("<!", StringComparison.Ordinal))
                throw SimulatedSqlException.XQuerySyntaxError(this.method, "<!");
            return this.ParseElementConstructor();
        }
        if (c == '$')
            return this.ResolveVariable(this.ReadVariableName());
        if (!IsNameStart(c))
            throw this.SyntaxError();

        var word = this.PeekWord();
        if (this.StartsComputedConstructor(word))
            return this.ParseComputedConstructor(word);
        if (word is "typeswitch" or "validate" or "ordered" or "unordered")
            throw SimulatedSqlException.XQuerySyntaxNotSupported(this.method, word);

        var name = this.ReadWord();
        return this.PeekIsOpenParen() ? this.ParseFunctionCall(name) : throw this.SyntaxError();
    }

    /// <summary>
    /// The computed constructors. Real takes only the constant-QName form —
    /// <c>element {…} {…}</c> is Msg 9315 whatever the name expression holds —
    /// and refuses the comment and processing-instruction forms outright
    /// (Msg 9326 / 9325), in every XML method.
    /// </summary>
    private XmlQueryExpr ParseComputedConstructor(string word)
    {
        this.index += word.Length;
        this.SkipWhitespace();
        switch (word)
        {
            case "attribute":
                if (this.Current == '{')
                    throw SimulatedSqlException.XQueryComputedNameNotConstant(this.method);
                return this.ParseComputedAttribute(this.ReadWord());
            case "comment":
                throw SimulatedSqlException.XQueryComputedConstructorNotSupported(this.method, isComment: true);
            case "element":
                if (this.Current == '{')
                    throw SimulatedSqlException.XQueryComputedNameNotConstant(this.method);
                return this.ParseComputedElement(this.ReadWord());
            case "processing-instruction":
                throw SimulatedSqlException.XQueryComputedConstructorNotSupported(this.method, isComment: false);
            default:
                return this.ParseComputedText();
        }
    }

    /// <summary>
    /// <c>attribute name { … }</c>. The name resolves through the prolog as an
    /// attribute name does — a prefix must be declared (Msg 2229), an
    /// unprefixed name takes no namespace — and <c>xmlns</c> is refused (Msg
    /// 9316). The content is a whole expression, atomized; an empty body is the
    /// empty string.
    /// </summary>
    private XmlComputedAttributeExpr ParseComputedAttribute(string name)
    {
        if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal))
            throw SimulatedSqlException.XQueryComputedAttributeXmlns(this.method);
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? string.Empty : name[..colon];
        var (local, uri) = this.ResolveName(name, isAttribute: true);

        this.SkipWhitespace();
        if (this.Current != '{')
            throw this.SyntaxError();
        this.index++;
        this.SkipWhitespace();
        if (this.Current == '}')
        {
            this.index++;
            return new XmlComputedAttributeExpr(prefix, local, uri, null);
        }
        var content = this.ParseExpr();
        this.SkipWhitespace();
        if (this.Current != '}')
            throw SimulatedSqlException.XQueryTokenExpected(this.method, "}");
        this.index++;
        RequireNotConstructed(content, "data()", this.method);
        return new XmlComputedAttributeExpr(prefix, local, uri, content);
    }

    /// <summary>
    /// <c>text { … }</c>, whose body real takes as a single expression — a
    /// comma there is Msg 2205 and an empty body Msg 2209 (probe-confirmed).
    /// </summary>
    private XmlComputedTextExpr ParseComputedText()
    {
        if (this.Current != '{')
            throw this.SyntaxError();
        this.index++;
        this.SkipWhitespace();
        if (this.Current == '}')
            throw this.SyntaxError();
        var content = this.ParseExprSingle();
        this.SkipWhitespace();
        if (this.Current != '}')
            throw SimulatedSqlException.XQueryTokenExpected(this.method, "}");
        this.index++;
        RequireNotConstructed(content, "data()", this.method);
        return new XmlComputedTextExpr(content);
    }

    /// <summary>
    /// A direct comment constructor. Its text is literal — braces included —
    /// and may carry neither <c>--</c> nor a trailing <c>-</c> (Msg 9322).
    /// </summary>
    private XmlDirectLeafExpr ParseDirectComment()
    {
        var comment = this.ScanComment();
        return new XmlDirectLeafExpr(isComment: true, string.Empty, comment);
    }

    /// <summary>Scans <c>&lt;!-- … --&gt;</c> at the cursor, answering its text.</summary>
    private string ScanComment()
    {
        var start = this.index + "<!--".Length;
        var end = this.text.IndexOf("-->", start, StringComparison.Ordinal);
        if (end < 0)
            throw SimulatedSqlException.XQuerySyntaxError(this.method, "<eof>");
        var comment = this.text[start..end];
        if (comment.Contains("--", StringComparison.Ordinal) || comment.EndsWith('-'))
            throw SimulatedSqlException.XQueryCommentDoubleHyphen(this.method);
        this.index = end + "-->".Length;
        return comment;
    }

    /// <summary>A direct processing-instruction constructor.</summary>
    private XmlDirectLeafExpr ParseDirectProcessingInstruction()
    {
        var (target, data) = this.ScanProcessingInstruction();
        return new XmlDirectLeafExpr(isComment: false, target, data);
    }

    /// <summary>
    /// Scans <c>&lt;?target data?&gt;</c> at the cursor. The target must follow
    /// the <c>&lt;?</c> directly (Msg 2278 names what did instead) and may not
    /// be <c>xml</c> in any case (Msg 2294); the whitespace separating it from
    /// the data isn't part of the data.
    /// </summary>
    private (string Target, string Data) ScanProcessingInstruction()
    {
        var start = this.index + "<?".Length;
        var end = this.text.IndexOf("?>", start, StringComparison.Ordinal);
        if (end < 0)
            throw SimulatedSqlException.XQuerySyntaxError(this.method, "<eof>");
        if (start < end && !IsNameStart(this.text[start]))
            throw SimulatedSqlException.XQueryTagNameInvalidStart(this.method, this.text[start]);
        var targetEnd = start;
        while (targetEnd < end && IsNameChar(this.text[targetEnd]))
            targetEnd++;
        var target = this.text[start..targetEnd];
        if (target.Equals("xml", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.XQueryProcessingInstructionTargetXml(this.method);
        var data = this.text[targetEnd..end].TrimStart();
        this.index = end + "?>".Length;
        return (target, data);
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
        var declarations = this.ConstructorDeclarations(name);
        string[] declared = declarations.Length == 0 ? [] : [name.Contains(':', StringComparison.Ordinal) ? name[..name.IndexOf(':', StringComparison.Ordinal)] : string.Empty];
        var local = name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..];

        this.SkipWhitespace();
        if (this.Current != '{')
            throw this.SyntaxError();
        this.index++;
        this.SkipWhitespace();
        if (this.Current == '}')
        {
            this.index++;
            return new XmlConstructedNodeExpr([$"<{name}{declarations}/>"], [], [], local, declared);
        }

        var content = this.ParseExpr();
        this.SkipWhitespace();
        if (this.Current != '}')
            throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "}");
        this.index++;
        return new XmlConstructedNodeExpr([$"<{name}{declarations}>", $"</{name}>"], [content], [false], local, declared);
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

        // An exponent makes the literal an xs:double.
        var exponent = false;
        if (this.Current is 'e' or 'E')
        {
            var after = this.index + 1;
            if (after < this.text.Length && this.text[after] is '+' or '-')
                after++;
            if (after < this.text.Length && char.IsAsciiDigit(this.text[after]))
            {
                exponent = true;
                this.index = after;
                while (this.index < this.text.Length && char.IsAsciiDigit(this.text[this.index]))
                    this.index++;
            }
        }

        var span = this.text.AsSpan(start, this.index - start);
        if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw this.SyntaxError();
        return exponent
            ? new XmlLiteralExpr(XmlAtomicTypes.Number(number, "xs:double"), XmlStaticKind.Number, "xs:double")
            : new XmlLiteralExpr(number, XmlStaticKind.Number, fractional ? "xs:decimal" : "xs:integer");
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
            if (c == '&')
            {
                _ = value.Append(this.ReadEntityReference());
                continue;
            }
            _ = value.Append(c);
            this.index++;
        }
        throw this.SyntaxError();
    }

    /// <summary>
    /// An entity or character reference inside a string literal — XQuery
    /// reads <c>&amp;lt;</c> there as <c>&lt;</c>. A name that stops on a
    /// character other than <c>;</c> is Msg 2283 naming it, an <c>&amp;</c>
    /// opening no name or a name XML doesn't predefine Msg 2282, and a
    /// numeric reference that isn't a number Msg 2285 (probed 2026-10-02
    /// against SQL Server 2025).
    /// </summary>
    private string ReadEntityReference()
    {
        var start = this.index + 1;
        if (start < this.text.Length && this.text[start] == '#')
        {
            var close = this.text.IndexOf(';', start);
            var digits = close < 0 ? string.Empty : this.text[(start + 1)..close];
            var number = digits.StartsWith('x')
                ? int.TryParse(digits.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : -1
                : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : -1;
            if (number is < 0 or > 0x10FFFF)
                throw SimulatedSqlException.XQueryInvalidNumericEntityReference(this.method);
            this.index = close + 1;
            return char.ConvertFromUtf32(number);
        }

        if (start >= this.text.Length || !IsNameStart(this.text[start]))
            throw SimulatedSqlException.XQueryInvalidEntityReference(this.method);
        var end = start;
        while (end < this.text.Length && IsNameChar(this.text[end]))
            end++;
        if (end >= this.text.Length || this.text[end] != ';')
            throw SimulatedSqlException.XQueryEntityReferenceCharacter(this.method, end < this.text.Length ? this.text[end] : ' ');
        var replacement = this.text[start..end] switch
        {
            "amp" => "&",
            "apos" => "'",
            "gt" => ">",
            "lt" => "<",
            "quot" => "\"",
            _ => throw SimulatedSqlException.XQueryInvalidEntityReference(this.method),
        };
        this.index = end + 1;
        return replacement;
    }

    private XmlQueryExpr ParseFunctionCall(string name)
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
    /// The <c>xs:</c> names are the constructor functions, each the cast to
    /// its type, and <c>sql:</c> names the two accessors.
    /// </summary>
    private XmlQueryExpr ResolveFunction(string name, XmlQueryExpr[] arguments)
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
                    return this.ResolveSqlAccessor(local, arguments);
                case "xdt":
                    if (local != "untypedAtomic")
                        throw SimulatedSqlException.XQueryNoSuchFunction(this.method, XmlAtomicTypes.DataTypesNamespace, local);
                    return this.ResolveConstructorFunction(name, null, XmlAtomicTypes.DataTypesNamespace, local, arguments);
                case "xs":
                    return this.ResolveConstructorFunction(name, XmlAtomicTypes.Resolve(local), XmlAtomicTypes.SchemaNamespace, local, arguments);
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

    /// <summary>
    /// <c>xs:type(expr)</c>: exactly one argument (Msg 2236 / 2238 name the
    /// function without its parentheses), then the cast. A name XSD has no
    /// atomic type for is Msg 2395 — <c>xs:untypedAtomic</c> included, the
    /// untyped type living under <c>xdt:</c>.
    /// </summary>
    private XmlCastExpr ResolveConstructorFunction(string name, System.Xml.Schema.XmlSchemaSimpleType? type, string namespaceUri, string local, XmlQueryExpr[] arguments)
    {
        var untyped = namespaceUri == XmlAtomicTypes.DataTypesNamespace;
        if (type is null && !untyped)
            throw SimulatedSqlException.XQueryNoSuchFunction(this.method, namespaceUri, local);
        if (arguments.Length == 0)
            throw SimulatedSqlException.XQueryTooFewArgumentsBare(this.method, name);
        if (arguments.Length > 1)
            throw SimulatedSqlException.XQueryTooManyArgumentsBare(this.method, name);
        return this.BuildCast(arguments[0], name, type, name);
    }

    /// <summary>
    /// Compiles <c>sql:variable("@v")</c> / <c>sql:column("c")</c> into a slot
    /// the SQL side fills before evaluation. The scope's resolver — supplied
    /// by the read methods — validates the name and answers the XQuery type the
    /// value will carry; the <c>.modify()</c> value expression compiles with no
    /// resolver and types the accessors untyped.
    /// </summary>
    private XmlSqlAccessorExpr ResolveSqlAccessor(string local, XmlQueryExpr[] arguments)
    {
        if (local is not ("variable" or "column"))
            throw SimulatedSqlException.XQueryNoSuchFunction(this.method, XmlAtomicTypes.SqlNamespace, local);
        if (this.sqlAccessors is not { } accessors)
            throw new NotSupportedException($"XQuery accessor 'sql:{local}()' is not modeled here.");
        if (arguments.Length == 0)
            throw SimulatedSqlException.XQueryTooFewArgumentsBare(this.method, local);
        if (arguments.Length > 1)
            throw SimulatedSqlException.XQueryTooManyArgumentsBare(this.method, local);
        if (arguments[0] is not XmlLiteralExpr { Value: string name })
            throw SimulatedSqlException.XQueryStringLiteralExpected(this.method);

        var isColumn = local[0] == 'c';
        var typeName = accessors.Resolver?.Invoke(isColumn, name);
        var slot = this.slotCount++;
        accessors.Add(isColumn, name, slot);
        if (typeName is null)
            return new XmlSqlAccessorExpr(slot);
        var kind = typeName == "xs:boolean" ? XmlStaticKind.Boolean
            : XmlAtomicTypes.NumericRank(typeName) > 0 ? XmlStaticKind.Number
            : XmlStaticKind.String;
        return new XmlSqlAccessorExpr(slot, kind, typeName);
    }

    /// <summary>
    /// The type a numeric aggregate or rounding function computes in: its
    /// argument's numeric type, an untyped or node argument counting as
    /// <c>xs:double</c> (probe-confirmed: <c>max(/r/z)</c> renders
    /// <c>1.234567E6</c>).
    /// </summary>
    private static string NumericTypeOf(XmlQueryExpr[] arguments, bool averaging = false)
    {
        if (arguments.Length == 0)
            return "xs:double";
        var argument = arguments[0];
        var rank = argument.AtomizedKind() == XmlStaticKind.Untyped ? 4 : XmlAtomicTypes.NumericRank(argument.TypeName);
        if (rank == 0)
            rank = 4;
        if (averaging && rank == 1)
            rank = 2;
        return XmlAtomicTypes.RankTypeName(rank);
    }

    /// <summary>Checks one call against the library's signature for that name.</summary>
    private XmlFunctionCallExpr BuildBuiltIn(string local, XmlQueryExpr[] arguments) =>
        local switch
        {
            "avg" => this.Build(XmlFunctionId.Avg, arguments, local, 1, 1, XmlStaticKind.Number, NumericTypeOf(arguments, averaging: true)),
            "ceiling" => this.Build(XmlFunctionId.Ceiling, arguments, local, 1, 1, XmlStaticKind.Number, NumericTypeOf(arguments), XmlArgumentRule.Atomic),
            "concat" => this.Build(XmlFunctionId.Concat, arguments, local, 2, int.MaxValue, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            "contains" => this.Build(XmlFunctionId.Contains, arguments, local, 2, 2, XmlStaticKind.Boolean, "xs:boolean", XmlArgumentRule.Atomic),
            "count" => this.Build(XmlFunctionId.Count, arguments, local, 1, 1, XmlStaticKind.Number, "xs:integer"),
            "data" => this.Build(
                XmlFunctionId.Data, arguments, local, 1, 1,
                arguments.Length == 1 ? arguments[0].AtomizedKind() : XmlStaticKind.Untyped,
                arguments.Length == 1 ? arguments[0].TypeName : "xdt:untypedAtomic",
                occurrenceFromArgument: true),
            "distinct-values" => this.Build(XmlFunctionId.DistinctValues, arguments, local, 1, 1, XmlStaticKind.Untyped, "xdt:untypedAtomic", occurrence: XmlOccurrence.Many),
            "empty" => this.Build(XmlFunctionId.Empty, arguments, local, 1, 1, XmlStaticKind.Boolean, "xs:boolean"),
            "false" => this.Build(XmlFunctionId.False, arguments, local, 0, 0, XmlStaticKind.Boolean, "xs:boolean"),
            "floor" => this.Build(XmlFunctionId.Floor, arguments, local, 1, 1, XmlStaticKind.Number, NumericTypeOf(arguments), XmlArgumentRule.Atomic),
            "last" => this.Build(XmlFunctionId.Last, arguments, local, 0, 0, XmlStaticKind.Number, "xs:integer"),
            "local-name" => this.Build(XmlFunctionId.LocalName, arguments, local, 0, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Item),
            "lower-case" => this.Build(XmlFunctionId.LowerCase, arguments, local, 1, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            "max" => this.Build(XmlFunctionId.Max, arguments, local, 1, 1, XmlStaticKind.Number, NumericTypeOf(arguments)),
            "min" => this.Build(XmlFunctionId.Min, arguments, local, 1, 1, XmlStaticKind.Number, NumericTypeOf(arguments)),
            "namespace-uri" => this.Build(XmlFunctionId.NamespaceUri, arguments, local, 0, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Item),
            "not" => this.Build(XmlFunctionId.Not, arguments, local, 1, 1, XmlStaticKind.Boolean, "xs:boolean", XmlArgumentRule.Condition),
            "number" => arguments.Length == 1 && arguments[0].Kind != XmlStaticKind.Node
                ? throw SimulatedSqlException.XQueryNodeRequired(this.method, "number()")
                : this.Build(XmlFunctionId.Number, arguments, local, 0, 1, XmlStaticKind.Number, "xs:double", XmlArgumentRule.Atomic),
            "position" => this.Build(XmlFunctionId.Position, arguments, local, 0, 0, XmlStaticKind.Number, "xs:integer"),
            "round" => this.Build(XmlFunctionId.Round, arguments, local, 1, 1, XmlStaticKind.Number, NumericTypeOf(arguments), XmlArgumentRule.Atomic),
            "string" => this.Build(XmlFunctionId.String, arguments, local, 0, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Item),
            "string-length" => this.Build(XmlFunctionId.StringLength, arguments, local, 0, 1, XmlStaticKind.Number, "xs:integer", XmlArgumentRule.Atomic),
            "substring" => this.Build(XmlFunctionId.Substring, arguments, local, 2, 3, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            "sum" => this.Build(XmlFunctionId.Sum, arguments, local, 1, 1, XmlStaticKind.Number, NumericTypeOf(arguments)),
            "true" => this.Build(XmlFunctionId.True, arguments, local, 0, 0, XmlStaticKind.Boolean, "xs:boolean"),
            "upper-case" => this.Build(XmlFunctionId.UpperCase, arguments, local, 1, 1, XmlStaticKind.String, "xs:string", XmlArgumentRule.Atomic),
            _ => throw SimulatedSqlException.XQueryNoSuchFunction(this.method, FunctionNamespace, local),
        };

    /// <summary>
    /// Checks one call against its signature and builds it. Arity is part of
    /// the signature on real too, so too few arguments is Msg 2236 and too many
    /// Msg 2238. Only a condition parameter (<c>not()</c>) may read a
    /// constructed node; every other function is Msg 2373 over one.
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
            if (rule != XmlArgumentRule.Condition)
                RequireNotConstructed(argument, $"{name}()", this.method);
            switch (rule)
            {
                case XmlArgumentRule.Atomic:
                    RequireSingleton(argument, $"{name}()", this.method);
                    break;
                case XmlArgumentRule.Condition:
                    this.RequireCondition(argument);
                    break;
                case XmlArgumentRule.Item when XmlQueryExpr.IsPlural(argument.Occurrence):
                    // A parameter typed item()? quotes the node static type
                    // rather than the atomized one (probe-confirmed for
                    // string()).
                    throw SimulatedSqlException.XQueryNotSingleton(this.method, $"{name}()", argument.NodeTypeName());
                default:
                    break;
            }
        }
        RequireParameterTypes(id, arguments, this.method);
        return new XmlFunctionCallExpr(
            id,
            arguments,
            kind,
            occurrenceFromArgument ? arguments[0].Occurrence : occurrence,
            typeName);
    }

    /// <summary>
    /// Msg 2364: a typed argument the parameter's type takes no implicit
    /// conversion from — a number or boolean for a string parameter, a string
    /// for a number. Untyped values and nodes convert (probed 2026-10-02
    /// against SQL Server 2025, which names the number parameter
    /// <c>xs:decimal</c>).
    /// </summary>
    private static void RequireParameterTypes(XmlFunctionId id, XmlQueryExpr[] arguments, string method)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            var expectsString = id switch
            {
                XmlFunctionId.Concat or XmlFunctionId.Contains or XmlFunctionId.UpperCase or XmlFunctionId.LowerCase or XmlFunctionId.StringLength => true,
                XmlFunctionId.Substring => i == 0,
                _ => (bool?)null,
            };
            if (expectsString is not { } stringParameter)
                return;
            var kind = arguments[i].AtomizedKind();
            if (stringParameter ? kind is XmlStaticKind.Number or XmlStaticKind.Boolean : kind == XmlStaticKind.String)
                throw SimulatedSqlException.XQueryCannotImplicitlyConvert(method, arguments[i].AtomizedTypeName(), stringParameter ? "xs:string" : "xs:decimal");
        }
    }

    /// <summary>
    /// Msg 2389: an operand real types as more than one item where the
    /// construct admits at most one.
    /// </summary>
    internal static void RequireSingleton(XmlQueryExpr operand, string construct, string method)
    {
        if (XmlQueryExpr.IsPlural(operand.Occurrence))
            throw SimulatedSqlException.XQueryNotSingleton(method, construct, operand.AtomizedTypeName());
    }

    /// <summary>
    /// Msg 2234: two operands whose static types are both known and don't
    /// compare. Untyped operands take their type from the other side, so only a
    /// pair of typed operands can mismatch.
    /// </summary>
    private static void RequireComparableTypes(XmlQueryExpr left, XmlQueryExpr right, string op, string method)
    {
        var leftKind = left.AtomizedKind();
        var rightKind = right.AtomizedKind();
        if (leftKind == XmlStaticKind.Untyped || rightKind == XmlStaticKind.Untyped)
            return;
        if (leftKind != rightKind)
            throw SimulatedSqlException.XQueryOperatorTypeMismatch(method, op, left.AtomizedTypeName(), right.AtomizedTypeName());
    }

    private (string? Operator, XmlComparisonForm Form) TryComparisonOperator()
    {
        this.SkipWhitespace();
        switch (this.Current)
        {
            case '!' when this.Peek(1) == '=':
                this.index += 2;
                return ("!=", XmlComparisonForm.General);
            case '<' when this.Peek(1) == '<':
                this.index += 2;
                return ("<<", XmlComparisonForm.Node);
            case '<':
                this.index++;
                return this.TryConsume('=') ? ("<=", XmlComparisonForm.General) : ("<", XmlComparisonForm.General);
            case '=':
                this.index++;
                return ("=", XmlComparisonForm.General);
            case '>' when this.Peek(1) == '>':
                this.index += 2;
                return (">>", XmlComparisonForm.Node);
            case '>':
                this.index++;
                return this.TryConsume('=') ? (">=", XmlComparisonForm.General) : (">", XmlComparisonForm.General);
            default:
                break;
        }

        foreach (var word in ValueComparisonOperators)
        {
            if (this.TryOperatorWord(word))
                return (word, XmlComparisonForm.Value);
        }
        if (this.TryOperatorWord("is"))
            return ("is", XmlComparisonForm.Node);
        this.RejectUnsupportedSyntax();
        return (null, XmlComparisonForm.General);
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
    /// <remarks>
    /// Three rules are real's own (probe-confirmed): <b>boundary whitespace</b>
    /// — a run of content that is only whitespace, between tags and enclosed
    /// expressions — is dropped, so <c>&lt;a&gt;  {1}  &lt;/a&gt;</c> is
    /// <c>&lt;a&gt;1&lt;/a&gt;</c> while <c>  x  </c> survives; an attribute
    /// value is either literal text or exactly one enclosed expression (Msg
    /// 9313 otherwise); and comments, processing instructions and CDATA
    /// sections inside the content are literal, braces included.
    /// </remarks>
    private XmlConstructedNodeExpr ParseElementConstructor()
    {
        var literals = new List<string>();
        var enclosed = new List<XmlQueryExpr>();
        var inAttribute = new List<bool>();
        var segment = new StringBuilder();
        var content = new StringBuilder();
        var depth = 0;
        var inTag = false;
        var closingTag = false;
        var quote = '\0';
        var valueExpressions = 0;
        var valueHasText = false;

        void FlushContent()
        {
            if (content.Length == 0)
                return;
            if (!string.IsNullOrWhiteSpace(content.ToString()))
                _ = segment.Append(content);
            _ = content.Clear();
        }

        while (this.index < this.text.Length)
        {
            var c = this.text[this.index];
            if (quote != '\0')
            {
                if (c is '{' or '}' && this.Peek(1) == c)
                {
                    _ = segment.Append(c);
                    valueHasText = true;
                    this.index += 2;
                    continue;
                }
                if (c == '{')
                {
                    literals.Add(segment.ToString());
                    _ = segment.Clear();
                    this.index++;
                    var expression = this.ParseExpr();
                    RequireNotConstructed(expression, "data()", this.method);
                    enclosed.Add(expression);
                    inAttribute.Add(true);
                    valueExpressions++;
                    this.SkipWhitespace();
                    if (this.Current != '}')
                        throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "}");
                    this.index++;
                    continue;
                }
                if (c == quote)
                {
                    if (valueExpressions > 1 || (valueExpressions == 1 && valueHasText))
                        throw SimulatedSqlException.XQueryAttributeValueMixed(this.method);
                    quote = '\0';
                }
                else
                {
                    valueHasText = true;
                }
                _ = segment.Append(c);
                this.index++;
                continue;
            }
            if (inTag)
            {
                if (c is '"' or '\'')
                {
                    quote = c;
                    valueExpressions = 0;
                    valueHasText = false;
                    _ = segment.Append(c);
                    this.index++;
                    continue;
                }
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
                continue;
            }

            // Element content.
            if (c == '<')
            {
                FlushContent();
                var rest = this.text.AsSpan(this.index);
                if (rest.StartsWith("<!--", StringComparison.Ordinal))
                {
                    var start = this.index;
                    _ = this.ScanComment();
                    _ = segment.Append(this.text, start, this.index - start);
                    continue;
                }
                if (rest.StartsWith("<![CDATA[", StringComparison.Ordinal))
                {
                    var end = this.text.IndexOf("]]>", this.index, StringComparison.Ordinal);
                    if (end < 0)
                        throw this.SyntaxError();
                    _ = segment.Append(this.text, this.index, end + 3 - this.index);
                    this.index = end + 3;
                    continue;
                }
                if (rest.StartsWith("<?", StringComparison.Ordinal))
                {
                    var (target, data) = this.ScanProcessingInstruction();
                    _ = segment.Append("<?").Append(target);
                    if (data.Length > 0)
                        _ = segment.Append(' ').Append(data);
                    _ = segment.Append("?>");
                    continue;
                }
                inTag = true;
                closingTag = this.Peek(1) == '/';
                _ = segment.Append(c);
                this.index++;
                continue;
            }
            if (c is '{' or '}' && this.Peek(1) == c)
            {
                _ = content.Append(c);
                this.index += 2;
                continue;
            }
            if (c == '{')
            {
                FlushContent();
                literals.Add(segment.ToString());
                _ = segment.Clear();
                this.index++;
                enclosed.Add(this.ParseExpr());
                inAttribute.Add(false);
                this.SkipWhitespace();
                if (this.Current != '}')
                    throw SimulatedSqlException.XQuerySyntaxErrorExpecting(this.method, this.CurrentToken(), "}");
                this.index++;
                continue;
            }
            _ = content.Append(c);
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
        (literals[0], var declared) = this.DeclarePrologNamespaces(literals[0], name, literals);
        return new XmlConstructedNodeExpr([.. literals], [.. enclosed], [.. inAttribute], name, declared);
    }

    /// <summary>
    /// Writes the prolog's namespace bindings onto a direct constructor's
    /// outermost element, so its names resolve the way a path step's do — the
    /// default element namespace when one is declared, plus each declared
    /// prefix the markup actually writes (a declaration nothing uses is
    /// omitted, as real omits it).
    /// </summary>
    private (string Opening, string[] Declared) DeclarePrologNamespaces(string opening, string name, List<string> literals)
    {
        // A binding the constructor writes itself stays its own; the prolog
        // only supplies what the tag doesn't.
        var tagEnd = opening.IndexOf('>', StringComparison.Ordinal);
        var tag = tagEnd < 0 ? opening : opening[..tagEnd];
        var declarations = new StringBuilder();
        var declared = new List<string>();
        if (this.defaultNamespace is { } uri && !tag.Contains("xmlns=", StringComparison.Ordinal))
        {
            _ = declarations.Append(" xmlns=\"").Append(uri).Append('"');
            declared.Add(string.Empty);
        }
        foreach (var (prefix, mapped) in this.prefixes)
        {
            if (literals.Exists(literal => literal.Contains(prefix + ":", StringComparison.Ordinal))
                && !tag.Contains($"xmlns:{prefix}=", StringComparison.Ordinal))
            {
                _ = declarations.Append(" xmlns:").Append(prefix).Append("=\"").Append(mapped).Append('"');
                declared.Add(prefix);
            }
        }
        if (declarations.Length == 0)
            return (opening, []);
        var end = opening.IndexOf('<', StringComparison.Ordinal) + 1 + name.Length;
        return (opening[..end] + declarations.ToString() + opening[end..], [.. declared]);
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

/// <summary>Which of XQuery's three comparison families an operator belongs to.</summary>
internal enum XmlComparisonForm
{
    /// <summary><c>=</c> <c>!=</c> <c>&lt;</c> …, existential over both sequences.</summary>
    General,

    /// <summary><c>eq</c> <c>ne</c> <c>lt</c> …, over singletons.</summary>
    Value,

    /// <summary><c>is</c> <c>&lt;&lt;</c> <c>&gt;&gt;</c>, node identity and order.</summary>
    Node,
}
