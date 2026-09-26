using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Globalization;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Renders a CHECK, DEFAULT or computed-column expression in the form SQL
/// Server stores in its <c>definition</c> column rather than as written
/// (probed 2026-09-26 against SQL Server 2025): names bracketed, numeric
/// literals parenthesized, operators unspaced, <c>IN</c> an OR chain over its
/// list reversed, <c>BETWEEN</c> a pair of comparisons, <c>CAST</c> a
/// <c>CONVERT</c>, <c>IIF</c> a <c>CASE</c>, and every written parenthesis
/// replaced by the ones real's own precedence rules put back.
/// The walk reads the expression's tokens, which is all the rendering needs;
/// a shape outside the grammar it knows yields null, and the caller keeps
/// the source text instead.
/// </summary>
internal sealed class CanonicalDefinition
{
    /// <summary>
    /// What a rendered piece is, which is what decides whether a parent
    /// wraps it in parentheses.
    /// </summary>
    private enum Shape
    {
        Atom,
        NumericLiteral,
        Negation,
        BitNot,
        Additive,
        Multiplicative,
        Predicate,
        Not,
        And,
        Or,
    }

    private readonly struct Piece(string text, Shape shape)
    {
        public readonly string Text = text;
        public readonly Shape Shape = shape;
    }

    private readonly List<Token> tokens;
    private int position;

    private CanonicalDefinition(List<Token> tokens) => this.tokens = tokens;

    /// <summary>
    /// The canonical text of the expression <paramref name="tokens"/> spell
    /// (whitespace and comments already dropped), without the outer
    /// parentheses every definition column adds — a CHECK's
    /// <paramref name="predicate"/>, or the value of a DEFAULT or computed
    /// column; null when some part of it is outside the modeled grammar.
    /// </summary>
    public static string? Render(List<Token> tokens, bool predicate)
    {
        var walk = new CanonicalDefinition(tokens);
        return (predicate ? walk.Or() : walk.Additive()) is { } piece && walk.position == tokens.Count ? piece.Text : null;
    }

    private Token? Current => this.position < this.tokens.Count ? this.tokens[this.position] : null;

    private Token? Next => this.position + 1 < this.tokens.Count ? this.tokens[this.position + 1] : null;

    private bool AtOperator(char character) => this.Current is Operator op && op.Character == character;

    private bool AtWord(string word) => IsWord(this.Current, word);

    private static bool IsWord(Token? token, string word) =>
        token is UnquotedString or ReservedKeyword && token.Source.Equals(word, StringComparison.OrdinalIgnoreCase);

    private bool TakeOperator(char character)
    {
        if (!this.AtOperator(character))
            return false;
        this.position++;
        return true;
    }

    private bool TakeWord(string word)
    {
        if (!this.AtWord(word))
            return false;
        this.position++;
        return true;
    }

    // An operator pair written with nothing between its characters (`<<`).
    private bool AtAdjacentPair(char first, char second) =>
        this.Current is Operator left && left.Character == first
        && this.Next is Operator right && right.Character == second
        && right.StartIndex == left.EndIndex;

    private Piece? Or()
    {
        if (this.And() is not { } left)
            return null;
        while (this.TakeWord("OR"))
        {
            if (this.And() is not { } right)
                return null;
            left = Combine(left, right, Shape.Or);
        }
        return left;
    }

    private Piece? And()
    {
        if (this.Not() is not { } left)
            return null;
        while (this.TakeWord("AND"))
        {
            if (this.Not() is not { } right)
                return null;
            left = Combine(left, right, Shape.And);
        }
        return left;
    }

    /// <summary>
    /// A left operand of its own kind stays bare and a right one keeps its
    /// parentheses; an OR under an AND is parenthesized either side.
    /// </summary>
    private static Piece Combine(Piece left, Piece right, Shape shape)
    {
        var word = shape == Shape.And ? " AND " : " OR ";
        var leftText = shape == Shape.And && left.Shape == Shape.Or ? $"({left.Text})" : left.Text;
        var rightText = right.Shape == Shape.Or || (shape == Shape.And && right.Shape == Shape.And) ? $"({right.Text})" : right.Text;
        return new(leftText + word + rightText, shape);
    }

    private Piece? Not()
    {
        if (!this.TakeWord("NOT"))
            return this.Predicate();
        return this.Not() is { } operand ? Negate(operand) : null;
    }

    private static Piece Negate(Piece operand) =>
        new(operand.Shape is Shape.And or Shape.Or ? $"NOT ({operand.Text})" : $"NOT {operand.Text}", Shape.Not);

    private Piece? Predicate()
    {
        if (this.AtOperator('('))
        {
            var start = this.position;
            this.position++;
            if (this.Or() is { Shape: Shape.Predicate or Shape.Not or Shape.And or Shape.Or } inner && this.TakeOperator(')'))
                return inner;
            this.position = start;
        }

        if (this.Additive() is not { } left)
            return null;

        if (this.Comparison() is { } comparison)
            return this.Additive() is { } right ? Compare(left, comparison, right) : null;

        if (this.TakeWord("IS"))
        {
            var negated = this.TakeWord("NOT");
            return this.TakeWord("NULL") ? new($"{ComparisonOperand(left)} IS {(negated ? "NOT " : "")}NULL", Shape.Predicate) : null;
        }

        var not = this.TakeWord("NOT");
        Piece? predicate;
        if (this.TakeWord("LIKE"))
        {
            predicate = this.Like(left);
        }
        else if (this.TakeWord("BETWEEN"))
        {
            predicate = this.Additive() is { } low && this.TakeWord("AND") && this.Additive() is { } high
                ? Combine(Compare(left, ">=", low), Compare(left, "<=", high), Shape.And)
                : null;
        }
        else if (this.TakeWord("IN"))
        {
            predicate = this.In(left);
        }
        else
        {
            return null;
        }
        return not && predicate is { } positive ? Negate(positive) : predicate;
    }

    private string? Comparison()
    {
        if (this.TakeOperator('='))
            return "=";
        if (this.TakeOperator('<'))
            return this.TakeOperator('=') ? "<=" : this.TakeOperator('>') ? "<>" : "<";
        if (this.TakeOperator('>'))
            return this.TakeOperator('=') ? ">=" : ">";
        if (!this.TakeOperator('!'))
            return null;
        return this.TakeOperator('=') ? "<>" : this.TakeOperator('<') ? ">=" : this.TakeOperator('>') ? "<=" : null;
    }

    private static Piece Compare(Piece left, string comparison, Piece right) =>
        new(ComparisonOperand(left) + comparison + ComparisonOperand(right), Shape.Predicate);

    // A comparison binds at the additive level: an additive operand and a
    // negation are parenthesized, a multiplicative one isn't.
    private static string ComparisonOperand(Piece operand) =>
        operand.Shape is Shape.Additive or Shape.Negation ? $"({operand.Text})" : operand.Text;

    private Piece? Like(Piece subject)
    {
        if (this.Additive() is not { } pattern)
            return null;
        if (!this.TakeWord("ESCAPE"))
            return new($"{subject.Text} like {pattern.Text}", Shape.Predicate);
        return this.Additive() is { } escape ? new($"{subject.Text} like {pattern.Text} escape {escape.Text} ", Shape.Predicate) : null;
    }

    private Piece? In(Piece subject)
    {
        if (!this.TakeOperator('('))
            return null;
        var items = new List<Piece>();
        do
        {
            if (this.Additive() is not { } item)
                return null;
            items.Add(item);
        }
        while (this.TakeOperator(','));
        if (!this.TakeOperator(')'))
            return null;

        var chain = Compare(subject, "=", items[^1]);
        for (var i = items.Count - 2; i >= 0; i--)
            chain = Combine(chain, Compare(subject, "=", items[i]), Shape.Or);
        return chain;
    }

    private Piece? Additive()
    {
        if (this.Term() is not { } left)
            return null;
        while (true)
        {
            if (this.AtAdjacentPair('<', '<') || this.AtAdjacentPair('>', '>'))
            {
                var name = this.AtOperator('<') ? "left_shift" : "right_shift";
                this.position += 2;
                if (this.Term() is not { } shift)
                    return null;
                left = new($"{name}({left.Text},{shift.Text})", Shape.Atom);
                continue;
            }
            if (this.Current is not Operator { Character: '+' or '-' or '&' or '|' or '^' } additive)
                return left;
            var character = additive.Character;
            this.position++;
            if (this.Term() is not { } right)
                return null;
            left = new(ArithmeticOperand(left, Shape.Additive) + character + ArithmeticOperand(right, Shape.Additive), Shape.Additive);
        }
    }

    private Piece? Term()
    {
        if (this.Unary() is not { } left)
            return null;
        while (this.Current is Operator { Character: '*' or '/' or '%' } multiplicative)
        {
            var character = multiplicative.Character;
            this.position++;
            if (this.Unary() is not { } right)
                return null;
            left = new(ArithmeticOperand(left, Shape.Multiplicative) + character + ArithmeticOperand(right, Shape.Multiplicative), Shape.Multiplicative);
        }
        return left;
    }

    // An operand binding no tighter than its operator is parenthesized — both
    // sides, so `a+b-c` renders `([a]+[b])-[c]` — and so is a negation.
    private static string ArithmeticOperand(Piece operand, Shape level) =>
        operand.Shape is Shape.Negation or Shape.Additive || (operand.Shape == Shape.Multiplicative && level == Shape.Multiplicative)
            ? $"({operand.Text})"
            : operand.Text;

    private static string UnaryOperand(Piece operand) =>
        operand.Shape is Shape.Negation or Shape.BitNot or Shape.Additive or Shape.Multiplicative ? $"({operand.Text})" : operand.Text;

    /// <summary>
    /// A minus takes a whole multiplicative term (<c>-a*b</c> is
    /// <c>-(a*b)</c>) and folds into a numeric literal it lands on; a plus
    /// vanishes; <c>~</c> binds tightest of all.
    /// </summary>
    private Piece? Unary()
    {
        if (this.TakeOperator('-'))
        {
            if (this.Term() is not { } operand)
                return null;
            return operand.Shape == Shape.NumericLiteral
                ? new(NegateLiteral(operand.Text), Shape.NumericLiteral)
                : new($" -{UnaryOperand(operand)}", Shape.Negation);
        }
        if (this.TakeOperator('+'))
            return this.Unary();
        if (this.TakeOperator('~'))
            return this.Unary() is { } operand ? new($"~{UnaryOperand(operand)}", Shape.BitNot) : null;
        return this.Postfix();
    }

    // The literal renders as `(<value>)`; a money one as `($<value>)`.
    private static string NegateLiteral(string literal)
    {
        var money = literal[1] == '$';
        var body = literal[(money ? 2 : 1)..^1];
        body = body.StartsWith('-') ? body[1..] : $"-{body}";
        // Only int's own minimum reads as an integer once negated.
        if (body == "-2147483648.")
            body = "-2147483648";
        return money ? $"(${body})" : $"({body})";
    }

    private Piece? Postfix()
    {
        if (this.Primary() is not { } primary)
            return null;
        while (true)
        {
            if (this.TakeWord("COLLATE"))
            {
                if (this.Current is not UnquotedString collation)
                    return null;
                this.position++;
                primary = new($"({primary.Text}) collate {collation.Source}", Shape.Atom);
                continue;
            }
            if (this.AtWord("AT") && IsWord(this.Next, "TIME"))
            {
                this.position += 2;
                if (!this.TakeWord("ZONE") || this.Primary() is not { } zone)
                    return null;
                primary = new($"({primary.Text} AT TIME ZONE {zone.Text})", Shape.Atom);
                continue;
            }
            return primary;
        }
    }

    private Piece? Primary()
    {
        var token = this.Current;
        switch (token)
        {
            case Operator { Character: '(' }:
                this.position++;
                return this.Additive() is { } inner && this.TakeOperator(')') ? inner : null;
            case Numeric numeric:
                this.position++;
                return new($"({NumericText(numeric)})", Shape.NumericLiteral);
            case Literal literal:
                this.position++;
                return LiteralText(literal.Value);
            case DoubleAtPrefixedString variable:
                this.position++;
                return new($"@@{Lower(variable.Span)}", Shape.Atom);
            case ReservedKeyword { Keyword: Keyword.Null }:
                this.position++;
                return new("NULL", Shape.Atom);
            case ReservedKeyword { Keyword: Keyword.Case }:
                this.position++;
                return this.Case();
            case ReservedKeyword or UnquotedString when this.Next is Operator { Character: '(' }:
                return this.BuiltIn();
            case ReservedKeyword or UnquotedString when Niladic(token.Source) is { } niladic:
                this.position++;
                return new(niladic, Shape.Atom);
            case UnquotedString or DelimitedIdentifier:
                return this.NameOrCall();
            default:
                return null;
        }
    }

    private static string? Niladic(ReadOnlySpan<char> word)
    {
        Span<char> upper = stackalloc char[word.Length];
        return word.ToUpperInvariant(upper) switch
        {
            4 => upper switch
            {
                "USER" => "user_name()",
                _ => null,
            },
            11 => upper switch
            {
                "SYSTEM_USER" => "suser_sname()",
                _ => null,
            },
            12 => upper switch
            {
                "CURRENT_DATE" => "current_date",
                "CURRENT_USER" => "user_name()",
                "SESSION_USER" => "user_name()",
                _ => null,
            },
            17 => upper switch
            {
                "CURRENT_TIMESTAMP" => "getdate()",
                _ => null,
            },
            _ => null,
        };
    }

    private static string NumericText(Numeric numeric)
    {
        var value = numeric.Value;
        if (value.Type == SqlType.Int32)
            return value.AsInt32.ToString(CultureInfo.InvariantCulture);
        if (value.Type == SqlType.Float)
            return value.AsDouble.ToString("0.0000000000000000e+000", CultureInfo.InvariantCulture);
        var text = value.AsDecimal38.ToString();
        return text.Contains('.', StringComparison.Ordinal) ? text : $"{text}.";
    }

    private static Piece? LiteralText(SqlValue value) => value.Type switch
    {
        MoneySqlType => new($"(${value.AsMoney.ToString("0.0000", CultureInfo.InvariantCulture)})", Shape.NumericLiteral),
        VarbinarySqlType or BinarySqlType => new($"0x{Convert.ToHexString(value.AsBytes)}", Shape.Atom),
        NVarcharSqlType or NCharSqlType => new($"N'{value.AsString.Replace("'", "''", StringComparison.Ordinal)}'", Shape.Atom),
        VarcharSqlType or CharSqlType => new($"'{value.AsString.Replace("'", "''", StringComparison.Ordinal)}'", Shape.Atom),
        _ => null,
    };

    private Piece? NameOrCall()
    {
        var parts = new List<string>();
        while (true)
        {
            if (this.Current is not (UnquotedString or DelimitedIdentifier))
                return null;
            parts.Add($"[{((Name)this.Current).Value.Replace("]", "]]", StringComparison.Ordinal)}]");
            this.position++;
            if (!this.TakeOperator('.'))
                break;
        }
        var name = string.Join('.', parts);
        if (!this.AtOperator('('))
            return new(name, Shape.Atom);
        // A single-part call is a built-in, which BuiltIn reads undelimited.
        return parts.Count > 1 && this.Arguments() is { } arguments ? new($"{name}({arguments})", Shape.Atom) : null;
    }

    /// <summary>
    /// A parenthesized, comma-separated argument list with the parentheses
    /// consumed, rendered without its parentheses; <c>JSON_OBJECT</c>'s
    /// <c>key:value</c> pairs render as written.
    /// </summary>
    private string? Arguments() =>
        !this.TakeOperator('(') ? null : this.TakeOperator(')') ? "" : this.ArgumentList();

    private string? ArgumentList()
    {
        var arguments = new List<string>();
        do
        {
            if (this.Additive() is not { } argument)
                return null;
            var text = argument.Text;
            if (this.TakeOperator(':'))
            {
                if (this.Additive() is not { } value)
                    return null;
                text = $"{text}:{value.Text}";
            }
            arguments.Add(text);
        }
        while (this.TakeOperator(','));
        return this.TakeOperator(')') ? string.Join(',', arguments) : null;
    }

    private Piece? BuiltIn()
    {
        var word = this.Current!.Source.ToString();
        Span<char> upper = stackalloc char[word.Length];
        _ = word.AsSpan().ToUpperInvariant(upper);
        this.position++;
        var text = upper switch
        {
            "CAST" => this.CastCall(plain: true),
            "CONVERT" => this.ConvertCall(plain: true),
            "DATEADD" => this.DatePartCall(Lower(word)),
            "DATEDIFF" => this.DatePartCall(Lower(word)),
            "DATEDIFF_BIG" => this.DatePartCall(Lower(word)),
            "DATENAME" => this.DatePartCall(Lower(word)),
            "DATEPART" => this.DatePartCall(Lower(word)),
            "DATETRUNC" => this.DatePartCall(Lower(word)),
            "DATE_BUCKET" => this.DatePartCall("Date_Bucket"),
            "DAY" => this.DatePartShorthand("day"),
            "IIF" => this.Iif(),
            "MONTH" => this.DatePartShorthand("month"),
            "PARSE" => this.ParseCall("parse"),
            "TRIM" => this.Trim(),
            "TRY_CAST" => this.CastCall(plain: false),
            "TRY_CONVERT" => this.ConvertCall(plain: false),
            "TRY_PARSE" => this.ParseCall("try_parse"),
            "YEAR" => this.DatePartShorthand("year"),
            _ => this.Arguments() is { } arguments ? $"{FunctionName(upper, word)}({arguments})" : null,
        };
        return text is null ? null : new(text, Shape.Atom);
    }

    // Real keeps a handful of built-ins in a mixed case of its own.
    private static string FunctionName(ReadOnlySpan<char> upper, string written) => upper switch
    {
        "COMPRESS" => "Compress",
        "CRYPT_GEN_RANDOM" => "Crypt_Gen_Random",
        "DECOMPRESS" => "Decompress",
        _ => Lower(written),
    };

    // YEAR, MONTH and DAY are stored as the DATEPART they mean.
    private string? DatePartShorthand(string part) =>
        this.Arguments() is { } argument ? $"datepart({part},{argument})" : null;

    // Real stores built-in, type and date-part names lowercased.
#pragma warning disable CA1308
    private static string Lower(ReadOnlySpan<char> text) => text.ToString().ToLowerInvariant();
#pragma warning restore CA1308

    private string? CastCall(bool plain)
    {
        if (!this.TakeOperator('(') || this.Additive() is not { } operand || !this.TakeWord("AS") || this.TypeName() is not { } type || !this.TakeOperator(')'))
            return null;
        return plain ? $"CONVERT({type},{operand.Text})" : $"TRY_CAST({operand.Text} AS {type})";
    }

    // TRY_CONVERT without a style is stored as the TRY_CAST it means.
    private string? ConvertCall(bool plain)
    {
        if (!this.TakeOperator('(') || this.TypeName() is not { } type || !this.TakeOperator(',') || this.Additive() is not { } operand)
            return null;
        Piece? style = null;
        if (this.TakeOperator(','))
        {
            if (this.Additive() is not { } written)
                return null;
            style = written;
        }
        if (!this.TakeOperator(')'))
            return null;
        return style is { } s
            ? $"{(plain ? "CONVERT" : "TRY_CONVERT")}({type},{operand.Text},{s.Text})"
            : plain ? $"CONVERT({type},{operand.Text})" : $"TRY_CAST({operand.Text} AS {type})";
    }

    /// <summary>
    /// A type as a CONVERT target renders it: the system name bracketed, a
    /// synonym folded to the name it means, and its length or precision
    /// arguments unspaced.
    /// </summary>
    private string? TypeName()
    {
        if (this.Current is not (UnquotedString or DelimitedIdentifier or ReservedKeyword))
            return null;
        var first = this.Current is Name name ? name.Value : this.Current.Source.ToString();
        this.position++;
        string canonical;
        if (Collation.Baseline.Equals(first, "double") && this.TakeWord("PRECISION"))
            canonical = "float";
        else if (Collation.Baseline.Equals(first, "national") && (this.TakeWord("CHARACTER") || this.TakeWord("CHAR")))
            canonical = this.TakeWord("VARYING") ? "nvarchar" : "nchar";
        else if (Collation.Baseline.Equals(first, "national") && this.TakeWord("TEXT"))
            canonical = "ntext";
        else if ((Collation.Baseline.Equals(first, "character") || Collation.Baseline.Equals(first, "char")) && this.TakeWord("VARYING"))
            canonical = "varchar";
        else if (Collation.Baseline.Equals(first, "binary") && this.TakeWord("VARYING"))
            canonical = "varbinary";
        else if (this.AtOperator('.'))
            return null;
        else
            canonical = CanonicalTypeName(first);

        if (!this.TakeOperator('('))
            return $"[{canonical}]";
        var arguments = new List<string>();
        do
        {
            if (this.Current is Numeric { Value.Type: var argumentType } argument && argumentType == SqlType.Int32)
                arguments.Add(argument.Value.AsInt32.ToString(CultureInfo.InvariantCulture));
            else if (this.AtWord("MAX"))
                arguments.Add("max");
            else
                return null;
            this.position++;
        }
        while (this.TakeOperator(','));
        return this.TakeOperator(')') ? $"[{canonical}]({string.Join(',', arguments)})" : null;
    }

    private static string CanonicalTypeName(string written)
    {
        Span<char> upper = stackalloc char[written.Length];
        return written.AsSpan().ToUpperInvariant(upper) switch
        {
            3 => upper switch
            {
                "DEC" => "decimal",
                _ => Lower(written),
            },
            7 => upper switch
            {
                "INTEGER" => "int",
                _ => Lower(written),
            },
            9 => upper switch
            {
                "CHARACTER" => "char",
                _ => Lower(written),
            },
            10 => upper switch
            {
                "ROWVERSION" => "timestamp",
                _ => Lower(written),
            },
            _ => Lower(written),
        };
    }

    private string? DatePartCall(string name)
    {
        if (!this.TakeOperator('(') || this.Current is not (UnquotedString or ReservedKeyword) || DatePartKinds.Resolve(this.Current.Source.ToString()) is not { } kind)
            return null;
        this.position++;
        if (!this.TakeOperator(','))
            return this.TakeOperator(')') ? $"{name}({DatePartName(kind)})" : null;
        return this.ArgumentList() is { } arguments ? $"{name}({DatePartName(kind)},{arguments})" : null;
    }

    private static string DatePartName(DatePartKind kind) => kind switch
    {
        DatePartKind.Year => "year",
        DatePartKind.Quarter => "quarter",
        DatePartKind.Month => "month",
        DatePartKind.DayOfYear => "dayofyear",
        DatePartKind.Day => "day",
        DatePartKind.Week => "week",
        DatePartKind.IsoWeek => "iso_week",
        DatePartKind.Weekday => "weekday",
        DatePartKind.Hour => "hour",
        DatePartKind.Minute => "minute",
        DatePartKind.Second => "second",
        DatePartKind.Millisecond => "millisecond",
        DatePartKind.Microsecond => "microsecond",
        DatePartKind.Nanosecond => "nanosecond",
        _ => "tzoffset",
    };

    private string? Iif()
    {
        if (!this.TakeOperator('(') || this.Or() is not { } condition || !this.TakeOperator(',')
            || this.Additive() is not { } whenTrue || !this.TakeOperator(',') || this.Additive() is not { } whenFalse || !this.TakeOperator(')'))
        {
            return null;
        }
        return $"case when {condition.Text} then {whenTrue.Text} else {whenFalse.Text} end";
    }

    private Piece? Case()
    {
        var text = "case ";
        var simple = !this.AtWord("WHEN");
        if (simple)
        {
            if (this.Additive() is not { } operand)
                return null;
            text += $"{operand.Text} ";
        }
        var any = false;
        while (this.TakeWord("WHEN"))
        {
            var condition = simple ? this.Additive() : this.Or();
            if (condition is not { } when || !this.TakeWord("THEN") || this.Additive() is not { } then)
                return null;
            text += $"when {when.Text} then {then.Text} ";
            any = true;
        }
        if (!any)
            return null;
        if (this.TakeWord("ELSE"))
        {
            if (this.Additive() is not { } otherwise)
                return null;
            text += $"else {otherwise.Text} ";
        }
        else
        {
            // Real writes the absent ELSE as an empty one: two spaces before END.
            text += " ";
        }
        return this.TakeWord("END") ? new($"{text}end", Shape.Atom) : null;
    }

    // Real puts two spaces ahead of AS and leaves the type as written.
    private string? ParseCall(string name)
    {
        if (!this.TakeOperator('(') || this.Additive() is not { } operand || !this.TakeWord("AS") || this.Current is not (UnquotedString or ReservedKeyword))
            return null;
        var type = Lower(this.Current.Source);
        this.position++;
        var culture = "";
        if (this.TakeWord("USING"))
        {
            if (this.Additive() is not { } written)
                return null;
            culture = $" USING {written.Text}";
        }
        return this.TakeOperator(')') ? $"{name}({operand.Text}  AS {type}{culture})" : null;
    }

    private string? Trim()
    {
        if (!this.TakeOperator('('))
            return null;
        var side = this.TakeWord("LEADING") ? "LEADING " : this.TakeWord("TRAILING") ? "TRAILING " : this.TakeWord("BOTH") ? "BOTH " : "";
        if (this.Additive() is not { } first)
            return null;
        if (this.TakeWord("FROM"))
            return this.Additive() is { } subject && this.TakeOperator(')') ? $"Trim({side}{first.Text} FROM {subject.Text})" : null;
        return side.Length == 0 && this.TakeOperator(')') ? $"Trim({first.Text})" : null;
    }
}
