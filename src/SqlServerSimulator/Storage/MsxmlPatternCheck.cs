using System.Xml;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Reads an <c>OPENXML</c> row or column pattern as MSXML's XPath parser does,
/// for the errors it reports ahead of evaluation — which .NET's XPath engine,
/// doing the evaluating, words differently (probed 2026-10-20 against SQL
/// Server 2025). A pattern is a path or a union of them: anything after one is
/// <c>Expected token 'eof' found '…'</c>, and one that parses but names no
/// nodes — a literal, a number, a function call — is
/// <c>Expression does not return a DOM node.</c> Each complaint is followed by
/// the pattern with <c>--&gt;</c> and <c>&lt;--</c> around the token it stopped
/// at, or <c>&lt;--</c> after the whole pattern when it stopped at its end.
/// </summary>
internal sealed class MsxmlPatternCheck
{
    private enum TokenKind : byte
    {
        End,
        Name,
        Number,
        String,
        Variable,
        Symbol,
    }

    private readonly struct Token(TokenKind kind, int start, int end, string text)
    {
        public readonly TokenKind Kind = kind;
        public readonly int Start = start;
        public readonly int End = end;
        public readonly string Text = text;

        /// <summary>The token as MSXML names it in an expected-token complaint.</summary>
        public string Display => this.Kind switch
        {
            TokenKind.End => "eof",
            TokenKind.Name => "NAME",
            TokenKind.Number => "NUMBER",
            TokenKind.String => "STRING",
            _ => this.Text,
        };
    }

    /// <summary>The functions MSXML knows, with the fewest and most arguments each takes (-1 for any number).</summary>
    private static (int Min, int Max)? Arity(string name) => name switch
    {
        "last" or "position" or "true" or "false" => (0, 0),
        "count" or "boolean" or "not" or "lang" or "floor" or "ceiling" or "round" or "sum" or "id" => (1, 1),
        "local-name" or "namespace-uri" or "name" or "string" or "string-length" or "normalize-space" or "number" => (0, 1),
        "starts-with" or "contains" or "substring-before" or "substring-after" => (2, 2),
        "substring" => (2, 3),
        "translate" => (3, 3),
        "concat" => (2, -1),
        _ => null,
    };

    private readonly string pattern;
    private readonly XmlNamespaceManager namespaces;
    private readonly List<Token> tokens;
    private int position;

    private MsxmlPatternCheck(string pattern, XmlNamespaceManager namespaces, List<Token> tokens)
    {
        this.pattern = pattern;
        this.namespaces = namespaces;
        this.tokens = tokens;
    }

    /// <summary>Raises the Msg 6603 MSXML would for <paramref name="pattern"/>, or returns when it reads as a path.</summary>
    public static void Check(string pattern, XmlNamespaceManager namespaces)
    {
        var check = new MsxmlPatternCheck(pattern, namespaces, Tokenize(pattern));
        var start = check.Current;
        // What can't begin a path leaves nothing read before it.
        if (!check.StartsPath())
            throw check.Expected("eof", start);
        var returnsNodes = check.Union();
        if (check.Current.Kind != TokenKind.End)
            throw check.Expected("eof", check.Current);
        if (!returnsNodes)
            throw SimulatedSqlException.XmlPatternInvalid("Expression does not return a DOM node.", check.Marked(start.Start, pattern.Length));
    }

    private Token Current => this.tokens[this.position];

    private Token Peek(int ahead) => this.tokens[Math.Min(this.position + ahead, this.tokens.Count - 1)];

    private bool IsSymbol(string text) => this.Current is { Kind: TokenKind.Symbol } token && token.Text == text;

    private void Advance() => this.position++;

    private void Require(string symbol)
    {
        if (!this.IsSymbol(symbol))
            throw this.Expected(symbol, this.Current);
        this.Advance();
    }

    /// <summary>Union := Path ('|' Path)*, answering whether it names nodes.</summary>
    private bool Union()
    {
        var nodes = this.Path();
        while (this.IsSymbol("|"))
        {
            this.Advance();
            if (this.Current.Kind == TokenKind.End)
                throw SimulatedSqlException.XmlPatternInvalid("Expression does not return a DOM node.", this.MarkedAtEnd());
            _ = this.Path();
            nodes = true;
        }
        return nodes;
    }

    /// <summary>A location path or a filter expression, answering whether it names nodes.</summary>
    private bool Path()
    {
        if (this.IsSymbol("/") || this.IsSymbol("//"))
        {
            var descendant = this.IsSymbol("//");
            this.Advance();
            if (this.StartsStep())
                this.RelativePath();
            else if (descendant && this.Current.Kind != TokenKind.End)
                throw this.ExpressionExpected();
            return true;
        }
        if (this.StartsStep())
        {
            this.RelativePath();
            return true;
        }
        var primary = this.Current;
        var nodes = this.Primary();
        while (this.IsSymbol("["))
            this.Predicate();
        if (this.IsSymbol("/") || this.IsSymbol("//"))
        {
            // A step can only follow nodes: `1/x` is refused at its separator.
            if (!nodes)
                throw SimulatedSqlException.XmlPatternInvalid("Expression does not return a DOM node.", this.Marked(primary.Start, this.Current.End));
            this.Advance();
            this.RelativePath();
            return true;
        }
        return nodes;
    }

    private bool StartsPath() => this.IsSymbol("/") || this.IsSymbol("//") || this.IsSymbol("(") || this.StartsStep()
        || this.Current.Kind is TokenKind.String or TokenKind.Number or TokenKind.Variable
        || (this.Current.Kind == TokenKind.Name && this.Peek(1).Text == "(");

    private bool StartsStep() => this.Current switch
    {
        { Kind: TokenKind.Symbol, Text: "." or ".." or "@" or "*" } => true,
        { Kind: TokenKind.Name } name => !this.Peek(1).Text.Equals("(", StringComparison.Ordinal) || name.Text is "node" or "text" or "comment" or "processing-instruction",
        _ => false,
    };

    private void RelativePath()
    {
        this.Step();
        while (this.IsSymbol("/") || this.IsSymbol("//"))
        {
            this.Advance();
            // A path may end in its separator, which MSXML reads as the nodes beneath.
            if (this.Current.Kind == TokenKind.End || !this.StartsStep())
                return;
            this.Step();
        }
    }

    private void Step()
    {
        if (this.IsSymbol(".") || this.IsSymbol(".."))
        {
            this.Advance();
            while (this.IsSymbol("["))
                this.Predicate();
            return;
        }
        if (this.IsSymbol("@"))
        {
            this.Advance();
            this.NodeTest();
        }
        else if (this.Current.Kind == TokenKind.Name && this.Peek(1) is { Kind: TokenKind.Symbol, Text: "::" })
        {
            this.Advance();
            this.Advance();
            this.NodeTest();
        }
        else
        {
            this.NodeTest();
        }
        while (this.IsSymbol("["))
            this.Predicate();
    }

    private void NodeTest()
    {
        var token = this.Current;
        if (this.IsSymbol("*"))
        {
            this.Advance();
            return;
        }
        if (token.Kind != TokenKind.Name)
            throw SimulatedSqlException.XmlPatternInvalid("NodeTest expected here.", token.Kind == TokenKind.End ? this.MarkedAtEnd() : this.Marked(token.Start, token.End));
        this.Advance();
        if (token.Text is "node" or "text" or "comment" or "processing-instruction" && this.IsSymbol("("))
        {
            this.Advance();
            if (token.Text == "processing-instruction" && this.Current.Kind == TokenKind.String)
                this.Advance();
            this.Require(")");
            return;
        }
        var colon = token.Text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && this.namespaces.LookupNamespace(token.Text[..colon]) is null)
            throw SimulatedSqlException.XmlPatternInvalid($"Reference to undeclared namespace prefix: '{token.Text[..colon]}'.", null);
    }

    private void Predicate()
    {
        this.Advance();
        this.Expression();
        this.Require("]");
    }

    /// <summary>A primary expression, answering whether it names nodes.</summary>
    private bool Primary()
    {
        var token = this.Current;
        switch (token.Kind)
        {
            case TokenKind.String or TokenKind.Number:
                this.Advance();
                return false;
            case TokenKind.Variable:
                throw SimulatedSqlException.XmlPatternInvalid($"{token.Text[1..]} may not appear to the right of / or // or be used with |.", null);
            case TokenKind.Symbol when token.Text == "(":
                this.Advance();
                this.Expression();
                this.Require(")");
                return true;
            case TokenKind.Name when this.Peek(1).Text == "(":
                this.FunctionCall();
                return false;
            default:
                throw this.ExpressionExpected();
        }
    }

    private void FunctionCall()
    {
        var name = this.Current;
        this.Advance();
        this.Advance();
        var arity = Arity(name.Text);
        var arguments = 0;
        var firstArgument = this.Current;
        if (!this.IsSymbol(")"))
        {
            // A function taking no arguments wants its parenthesis closed.
            if (arity is (0, 0))
                throw this.Expected(")", this.Current);
            this.Expression();
            arguments++;
            while (this.IsSymbol(","))
            {
                if (arity is { Max: >= 0 } bounded && arguments >= bounded.Max)
                    throw this.Expected(")", this.Current);
                this.Advance();
                this.Expression();
                arguments++;
            }
        }
        if (arity is { } known && arguments < known.Min)
        {
            // not() and concat() word their shortfalls their own way.
            if (name.Text == "not")
                throw SimulatedSqlException.XmlPatternInvalid("Boolean parameter expected.", this.Marked(name.Start, this.Current.End));
            if (known.Max < 0)
                throw SimulatedSqlException.XmlPatternInvalid("Invalid number of arguments.", this.Marked(firstArgument.Start, this.Current.End));
            if (arguments == 0)
                throw this.ExpressionExpected();
            throw this.Expected(",", this.Current);
        }
        this.Require(")");
        if (arity is null)
            throw SimulatedSqlException.XmlPatternInvalid("Unknown method.", this.Marked(name.Start, this.tokens[this.position - 1].End));
    }

    /// <summary>A predicate's or argument's expression: XPath's full grammar.</summary>
    private void Expression()
    {
        this.Binary(0);
    }

    private static readonly string[][] Levels =
    [
        ["or"],
        ["and"],
        ["=", "!="],
        ["<", ">", "<=", ">="],
        ["+", "-"],
        ["*", "div", "mod"],
    ];

    private void Binary(int level)
    {
        if (level == Levels.Length)
        {
            while (this.IsSymbol("-"))
                this.Advance();
            _ = this.Union();
            return;
        }
        this.Binary(level + 1);
        while (this.Current is { Kind: TokenKind.Symbol or TokenKind.Name } token && Array.IndexOf(Levels[level], token.Text) >= 0
            && (token.Kind == TokenKind.Symbol || token.Text is "or" or "and" or "div" or "mod"))
        {
            this.Advance();
            this.Binary(level + 1);
        }
    }

    private SimulatedSqlException ExpressionExpected() =>
        SimulatedSqlException.XmlPatternInvalid("Expression expected.", this.Current.Kind == TokenKind.End ? this.MarkedAtEnd() : this.Marked(this.Current.Start, this.Current.End));

    private SimulatedSqlException Expected(string expected, Token found) =>
        SimulatedSqlException.XmlPatternInvalid($"Expected token '{expected}' found '{found.Display}'.", found.Kind == TokenKind.End ? this.MarkedAtEnd() : this.Marked(found.Start, found.End));

    private string Marked(int start, int end) => $"{this.pattern[..start]}-->{this.pattern[start..end]}<--{this.pattern[end..]}";

    private string MarkedAtEnd() => this.pattern + "<--";

    private static List<Token> Tokenize(string pattern)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (true)
        {
            while (i < pattern.Length && char.IsWhiteSpace(pattern[i]))
                i++;
            if (i >= pattern.Length)
            {
                tokens.Add(new Token(TokenKind.End, i, i, ""));
                return tokens;
            }
            var start = i;
            var c = pattern[i];
            if (c is '"' or '\'')
            {
                var close = pattern.IndexOf(c, i + 1);
                if (close < 0)
                    throw SimulatedSqlException.XmlPatternInvalid("A string literal was not closed.", $"{pattern[..start]}-->{pattern[start..]}<--");
                tokens.Add(new Token(TokenKind.String, start, close + 1, pattern[start..(close + 1)]));
                i = close + 1;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < pattern.Length && char.IsDigit(pattern[i + 1])))
            {
                while (i < pattern.Length && (char.IsDigit(pattern[i]) || pattern[i] == '.'))
                    i++;
                tokens.Add(new Token(TokenKind.Number, start, i, pattern[start..i]));
                continue;
            }
            if (c == '$' || char.IsLetter(c) || c == '_')
            {
                i++;
                while (i < pattern.Length && (char.IsLetterOrDigit(pattern[i]) || pattern[i] is '_' or '-' or '.' || (pattern[i] == ':' && i + 1 < pattern.Length && pattern[i + 1] != ':')))
                    i++;
                tokens.Add(new Token(c == '$' ? TokenKind.Variable : TokenKind.Name, start, i, pattern[start..i]));
                continue;
            }
            var two = i + 1 < pattern.Length ? pattern.Substring(i, 2) : "";
            var symbol = two is "//" or ".." or "::" or "!=" or "<=" or ">=" ? two : c.ToString();
            i += symbol.Length;
            tokens.Add(new Token(TokenKind.Symbol, start, i, symbol));
        }
    }
}
