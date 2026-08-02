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
internal sealed class XmlQueryParser(string text, string? defaultNamespace, Dictionary<string, string> prefixes, string method)
{
    /// <summary>The XQuery namespace an unprefixed function name lives in.</summary>
    private const string FunctionNamespace = "http://www.w3.org/2004/07/xpath-functions";

    private readonly string text = text;
    private readonly string? defaultNamespace = defaultNamespace;
    private readonly Dictionary<string, string> prefixes = prefixes;
    private readonly string method = method;
    private int index;

    /// <summary>Parses the whole body, rejecting anything left over.</summary>
    public XmlQueryExpr ParseBody()
    {
        this.RejectFlwor();
        var expression = this.ParseExpr();
        this.SkipWhitespace();
        return this.index < this.text.Length ? throw this.SyntaxError() : expression;
    }

    /// <summary>
    /// A body opening a FLWOR / quantified / conditional expression names the
    /// unbuilt construct instead of failing as a malformed path. Each of these
    /// words is also a legal element name, so the following token — a variable
    /// reference, or <c>(</c> for <c>if</c> — is what tells them apart.
    /// </summary>
    private void RejectFlwor()
    {
        this.SkipWhitespace();
        var word = this.PeekWord();
        if (word is not ("every" or "for" or "if" or "let" or "some"))
            return;
        var after = this.index + word.Length;
        while (after < this.text.Length && char.IsWhiteSpace(this.text[after]))
            after++;
        if (after < this.text.Length && this.text[after] == (word == "if" ? '(' : '$'))
            throw new NotSupportedException($"XQuery '{word}' expressions are not modeled.");
    }

    private XmlQueryExpr ParseExpr() => this.ParseOr();

    private XmlQueryExpr ParseOr()
    {
        var left = this.ParseAnd();
        while (this.TryOperatorWord("or"))
            left = new XmlLogicalExpr(left, this.ParseAnd(), isAnd: false);
        return left;
    }

    private XmlQueryExpr ParseAnd()
    {
        var left = this.ParseComparison();
        while (this.TryOperatorWord("and"))
            left = new XmlLogicalExpr(left, this.ParseComparison(), isAnd: true);
        return left;
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
        var after = this.index + word.Length;
        while (after < this.text.Length && char.IsWhiteSpace(this.text[after]))
            after++;
        return after >= this.text.Length || this.text[after] != '(' || NodeTestKind(word) is not null;
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
        return new XmlStep(axis, XmlNodeTestKind.Name, local, uri, this.ParsePredicates());
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
            var predicate = this.ParseExpr();
            this.SkipWhitespace();
            if (this.Current != ']')
                throw this.SyntaxError();
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
            var items = new List<XmlQueryExpr>();
            this.SkipWhitespace();
            if (this.Current != ')')
            {
                items.Add(this.ParseExpr());
                this.SkipWhitespace();
                while (this.Current == ',')
                {
                    this.index++;
                    items.Add(this.ParseExpr());
                    this.SkipWhitespace();
                }
            }
            if (this.Current != ')')
                throw this.SyntaxError();
            this.index++;
            return new XmlSequenceExpr([.. items]);
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
        if (c == '$')
            throw new NotSupportedException("XQuery variable references are not modeled.");
        if (!IsNameStart(c))
            throw this.SyntaxError();

        var name = this.ReadWord();
        return this.PeekIsOpenParen() ? this.ParseFunctionCall(name) : throw this.SyntaxError();
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
            arguments.Add(this.ParseExpr());
            this.SkipWhitespace();
            while (this.Current == ',')
            {
                this.index++;
                arguments.Add(this.ParseExpr());
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

        return local switch
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
            "not" => this.Build(XmlFunctionId.Not, arguments, local, 1, 1, XmlStaticKind.Boolean, "xs:boolean"),
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
    }

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
