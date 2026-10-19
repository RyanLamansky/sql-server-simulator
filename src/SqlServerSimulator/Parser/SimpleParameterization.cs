using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The text real's simple parameterization gives a statement, which is what
/// Query Store stores as its <c>query_sql_text</c>: a declaration of the
/// parameters the literals became, then the statement re-rendered from its
/// parse — <c>(@1 tinyint)SELECT * FROM [t] WHERE [a]=@1</c> for
/// <c>select * from t where a = 1</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rendering rules were read off SQL Server 2025 (probed 2026-09-29):
/// keywords upper case except an <c>UPDATE</c>'s <c>set</c> and an
/// <c>INSERT</c>'s <c>values</c>; every identifier bracketed and an
/// <c>AS</c> dropped; no spaces around a comparison or arithmetic operator
/// but one around an assignment's <c>=</c>; two spaces ahead of an
/// <c>UPDATE</c>'s or <c>DELETE</c>'s <c>WHERE</c>, and ahead of a
/// <c>SELECT</c>'s after a table hint; an explicit <c>ASC</c> in an
/// <c>ORDER BY</c>; a <c>BETWEEN</c> split into two comparisons; arithmetic
/// inside a comparison parenthesized, and a select-list number too.
/// </para>
/// <para>
/// A literal compared directly types by its value — <c>tinyint</c> to 255,
/// <c>smallint</c> for the rest of its range, then <c>int</c> and
/// <c>numeric(n,0)</c> — while one in arithmetic, a <c>SET</c> or a
/// <c>VALUES</c> list is <c>int</c>; a decimal is <c>numeric(p,s)</c>, a
/// string <c>varchar(8000)</c> / <c>nvarchar(4000)</c>. Parameters number in
/// text order, and Query Store's text declares an <c>UPDATE</c>'s
/// <c>WHERE</c> clause's ahead of its <c>SET</c> clause's.
/// </para>
/// <para>
/// Which statements qualify is <see cref="BindErrorReport.IsSimplyParameterizable"/>'s
/// rule, narrowed by what real declines on top of it: <c>&lt;&gt;</c>,
/// <c>!=</c> and <c>NOT</c> (probed 2026-09-29), and a call of <c>MIN</c>,
/// <c>MAX</c>, <c>LEN</c>, <c>UPPER</c> or <c>ISNULL</c> (probed
/// 2026-10-19). An aggregate renders in upper case, <c>COUNT(*)</c> included,
/// any other function as written, and a <c>CAST</c> or <c>CONVERT</c> as
/// <c>CONVERT([type](length),expression)</c>. A shape the renderer doesn't
/// know yields null, and the statement is stored as written.
/// </para>
/// </remarks>
internal static class SimpleParameterization
{
    /// <summary>
    /// The parameterized text of the statement <paramref name="tokens"/>
    /// spell (no trailing separator), or null when real wouldn't
    /// parameterize it or its shape is past what the renderer covers: the
    /// form Query Store stores, and the form the prepared plan caches, which
    /// declares every parameter in number order (probed 2026-10-15 against
    /// SQL Server 2025: <c>update t set b = 'z' where a = 1</c> is
    /// <c>(@2 tinyint,@1 varchar(8000))</c> in <c>query_store_query_text</c>
    /// and <c>(@1 varchar(8000),@2 tinyint)</c> under
    /// <c>sys.dm_exec_query_stats</c>).
    /// </summary>
    public static (string QueryStore, string Prepared)? Parameterize(List<Token> tokens)
    {
        if (!BindErrorReport.IsSimplyParameterizable(tokens))
            return null;
        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '!' }:
                case Operator { Character: '<' } when i + 1 < tokens.Count && tokens[i + 1] is Operator { Character: '>' }:
                case ReservedKeyword { Keyword: Keyword.Not } when i == 0 || tokens[i - 1] is not ReservedKeyword { Keyword: Keyword.Is }:
                    return null;
            }
        }
        // A catalog view isn't parameterized (probed 2026-09-29).
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] is Name { Value: var schema } && tokens[i + 1] is Operator { Character: '.' }
                && (schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }
        var renderer = new Renderer(tokens);
        return renderer.Statement() ? (renderer.Result(whereFirst: true), renderer.Result(whereFirst: false)) : null;
    }

    /// <summary>Where a literal sits, which decides the type its parameter declares.</summary>
    private enum LiteralSite
    {
        /// <summary>Directly compared: typed by its value.</summary>
        Compared,

        /// <summary>In arithmetic, an assignment or a <c>VALUES</c> list: an integer is <c>int</c>.</summary>
        Widened,

        /// <summary>In the select list: rendered, not parameterized.</summary>
        Kept,
    }

    private sealed class Renderer(List<Token> tokens)
    {
        private readonly StringBuilder body = new();
        private readonly List<(int Number, string Type, bool DeclaredFirst)> parameters = [];
        private int position;
        private bool inWhere;
        private bool declareWhereFirst;

        private Token? Current => this.position < tokens.Count ? tokens[this.position] : null;

        private Token? Peek(int ahead) => this.position + ahead < tokens.Count ? tokens[this.position + ahead] : null;

        /// <summary>
        /// The rendered statement under its declaration: in number order, or
        /// — <paramref name="whereFirst"/>, Query Store's form — an
        /// <c>UPDATE</c>'s <c>WHERE</c> clause's parameters ahead of its
        /// <c>SET</c> clause's, each group in number order.
        /// </summary>
        public string Result(bool whereFirst)
        {
            var whereGroupFirst = whereFirst && this.declareWhereFirst;
            var prefix = new StringBuilder("(");
            var first = true;
            for (var pass = whereGroupFirst ? 0 : 1; pass < 2; pass++)
            {
                foreach (var (number, type, inWhere) in this.parameters)
                {
                    if (whereGroupFirst && inWhere != (pass == 0))
                        continue;
                    if (!first)
                        _ = prefix.Append(',');
                    first = false;
                    _ = prefix.Append('@').Append(number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(type);
                }
            }
            return prefix.Append(')').Append(this.body).ToString();
        }

        public bool Statement() => this.Current switch
        {
            ReservedKeyword { Keyword: Keyword.Select } => this.Select(),
            ReservedKeyword { Keyword: Keyword.Update } => this.Update(),
            ReservedKeyword { Keyword: Keyword.Delete } => this.Delete(),
            ReservedKeyword { Keyword: Keyword.Insert } => this.Insert(),
            _ => false,
        };

        private bool At(Keyword keyword) => this.Current is ReservedKeyword k && k.Keyword == keyword;

        private bool AtOperator(char character) => this.Current is Operator o && o.Character == character;

        private bool Select()
        {
            this.position++;
            _ = this.body.Append("SELECT ");
            if (!this.SelectList())
                return false;
            if (!this.At(Keyword.From))
                return false;
            this.position++;
            _ = this.body.Append(" FROM ");
            if (!this.TableReference(allowAlias: true))
                return false;
            if (this.At(Keyword.With))
            {
                if (!this.TableHints())
                    return false;
                _ = this.body.Append(' ');
            }
            if (this.At(Keyword.Where))
            {
                this.position++;
                _ = this.body.Append(" WHERE ");
                if (!this.Predicate())
                    return false;
            }
            if (this.At(Keyword.Order))
            {
                if (this.Peek(1) is not ReservedKeyword { Keyword: Keyword.By })
                    return false;
                this.position += 2;
                _ = this.body.Append(" ORDER BY ");
                if (!this.OrderList())
                    return false;
            }
            return this.Current is null;
        }

        private bool Update()
        {
            this.position++;
            _ = this.body.Append("UPDATE ");
            if (!this.TableReference(allowAlias: false) || !this.At(Keyword.Set))
                return false;
            this.position++;
            _ = this.body.Append(" set ");
            this.declareWhereFirst = true;
            while (true)
            {
                if (!this.ColumnName())
                    return false;
                if (!this.AtOperator('='))
                    return false;
                this.position++;
                _ = this.body.Append(" = ");
                if (!this.Expression(LiteralSite.Widened, parenthesizeArithmetic: false))
                    return false;
                if (!this.AtOperator(','))
                    break;
                this.position++;
                _ = this.body.Append(',');
            }
            if (this.At(Keyword.Where))
            {
                this.position++;
                _ = this.body.Append("  WHERE ");
                if (!this.Predicate())
                    return false;
            }
            return this.Current is null;
        }

        private bool Delete()
        {
            this.position++;
            if (this.At(Keyword.From))
                this.position++;
            _ = this.body.Append("DELETE ");
            if (!this.TableReference(allowAlias: false) || !this.At(Keyword.Where))
                return false;
            this.position++;
            _ = this.body.Append("  WHERE ");
            return this.Predicate() && this.Current is null;
        }

        private bool Insert()
        {
            this.position++;
            if (this.At(Keyword.Into))
                this.position++;
            _ = this.body.Append("INSERT INTO ");
            if (!this.TableReference(allowAlias: false))
                return false;
            if (this.AtOperator('('))
            {
                this.position++;
                _ = this.body.Append('(');
                while (true)
                {
                    if (!this.ColumnName())
                        return false;
                    if (this.AtOperator(')'))
                        break;
                    if (!this.AtOperator(','))
                        return false;
                    this.position++;
                    _ = this.body.Append(',');
                }
                this.position++;
                _ = this.body.Append(')');
            }
            if (this.At(Keyword.Values))
            {
                this.position++;
                _ = this.body.Append(" values");
                while (true)
                {
                    if (!this.AtOperator('('))
                        return false;
                    this.position++;
                    _ = this.body.Append('(');
                    if (!this.ValueList(')'))
                        return false;
                    this.position++;
                    _ = this.body.Append(')');
                    if (!this.AtOperator(','))
                        break;
                    this.position++;
                    _ = this.body.Append(',');
                }
                return this.Current is null;
            }
            if (!this.At(Keyword.Select))
                return false;
            this.position++;
            _ = this.body.Append(" SELECT ");
            return this.ValueList(null) && this.Current is null;
        }

        // A VALUES row or a FROM-less SELECT's list; stops ahead of the closer.
        private bool ValueList(char? closer)
        {
            while (true)
            {
                if (!this.Expression(LiteralSite.Widened, parenthesizeArithmetic: false))
                    return false;
                if (closer is { } close && this.AtOperator(close))
                    return true;
                if (closer is null && this.Current is null)
                    return true;
                if (!this.AtOperator(','))
                    return false;
                this.position++;
                _ = this.body.Append(',');
            }
        }

        private bool SelectList()
        {
            while (true)
            {
                if (this.AtOperator('*'))
                {
                    this.position++;
                    _ = this.body.Append('*');
                }
                else
                {
                    if (!this.Expression(LiteralSite.Kept, parenthesizeArithmetic: false))
                        return false;
                    if (this.At(Keyword.As))
                        this.position++;
                    if (this.Current is Name alias)
                    {
                        this.position++;
                        _ = this.body.Append(' ');
                        AppendBracketed(this.body, alias);
                    }
                }
                if (!this.AtOperator(','))
                    return true;
                this.position++;
                _ = this.body.Append(',');
            }
        }

        private bool TableReference(bool allowAlias)
        {
            if (!this.DottedName())
                return false;
            if (!allowAlias)
                return true;
            if (this.At(Keyword.As))
            {
                this.position++;
                if (this.Current is not Name)
                    return false;
            }
            if (this.Current is Name alias)
            {
                this.position++;
                _ = this.body.Append(' ');
                AppendBracketed(this.body, alias);
            }
            return true;
        }

        // WITH ( hint [, hint …] ) — the hints as written, without spaces.
        private bool TableHints()
        {
            this.position++;
            if (!this.AtOperator('('))
                return false;
            this.position++;
            _ = this.body.Append(" WITH(");
            var depth = 1;
            while (this.Current is { } token)
            {
                if (token is Operator { Character: '(' })
                    depth++;
                if (token is Operator { Character: ')' } && --depth == 0)
                {
                    this.position++;
                    _ = this.body.Append(')');
                    return true;
                }
                _ = this.body.Append(token.Source);
                this.position++;
            }
            return false;
        }

        private bool OrderList()
        {
            while (true)
            {
                if (!this.Expression(LiteralSite.Kept, parenthesizeArithmetic: false))
                    return false;
                if (this.At(Keyword.Desc))
                {
                    this.position++;
                    _ = this.body.Append(" DESC");
                }
                else
                {
                    if (this.At(Keyword.Asc))
                        this.position++;
                    _ = this.body.Append(" ASC");
                }
                if (!this.AtOperator(','))
                    return true;
                this.position++;
                _ = this.body.Append(',');
            }
        }

        private bool Predicate()
        {
            this.inWhere = true;
            while (true)
            {
                if (!this.Conjunct())
                    return false;
                if (!this.At(Keyword.And))
                    return true;
                this.position++;
                _ = this.body.Append(" AND ");
            }
        }

        private bool Conjunct()
        {
            // A parenthesized predicate group loses its parentheses; an
            // operand that merely opens with one (arithmetic) is read as such
            // when the group reading fails.
            if (this.AtOperator('('))
            {
                var start = this.position;
                var length = this.body.Length;
                var parameterCount = this.parameters.Count;
                this.position++;
                if (this.Predicate() && this.AtOperator(')'))
                {
                    this.position++;
                    return true;
                }
                this.position = start;
                this.body.Length = length;
                this.parameters.RemoveRange(parameterCount, this.parameters.Count - parameterCount);
            }

            var operandStart = this.position;
            var operandBodyStart = this.body.Length;
            if (!this.Expression(LiteralSite.Compared, parenthesizeArithmetic: true))
                return false;
            if (this.At(Keyword.Is))
            {
                this.position++;
                var negated = this.At(Keyword.Not);
                if (negated)
                    this.position++;
                if (!this.At(Keyword.Null))
                    return false;
                this.position++;
                _ = this.body.Append(negated ? " IS NOT NULL" : " IS NULL");
                return true;
            }
            if (this.At(Keyword.Between))
            {
                // x BETWEEN a AND b renders as x>=a AND x<=b, x written twice.
                var operandText = this.body.ToString(operandBodyStart, this.body.Length - operandBodyStart);
                if (ContainsLiteral(tokens, operandStart, this.position))
                    return false;
                this.position++;
                _ = this.body.Append(">=");
                if (!this.Expression(LiteralSite.Compared, parenthesizeArithmetic: true) || !this.At(Keyword.And))
                    return false;
                this.position++;
                _ = this.body.Append(" AND ").Append(operandText).Append("<=");
                return this.Expression(LiteralSite.Compared, parenthesizeArithmetic: true);
            }
            if (this.ComparisonOperator() is not { } comparison)
                return false;
            _ = this.body.Append(comparison);
            return this.Expression(LiteralSite.Compared, parenthesizeArithmetic: true);
        }

        private static bool ContainsLiteral(List<Token> tokens, int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (tokens[i] is Literal or Numeric)
                    return true;
            }
            return false;
        }

        private string? ComparisonOperator()
        {
            switch (this.Current)
            {
                case Operator { Character: '=' }:
                    this.position++;
                    return "=";
                case Operator { Character: '<' or '>' } first:
                    this.position++;
                    if (this.AtOperator('='))
                    {
                        this.position++;
                        return first.Character == '<' ? "<=" : ">=";
                    }
                    return first.Character == '<' ? "<" : ">";
                default:
                    return null;
            }
        }

        /// <summary>
        /// An operand: terms joined by arithmetic operators, rendered without
        /// spaces; parenthesized as a whole in a comparison when it is
        /// arithmetic, where its literals widen to <c>int</c>.
        /// </summary>
        private bool Expression(LiteralSite site, bool parenthesizeArithmetic)
        {
            var start = this.position;
            var arithmetic = false;
            var scan = this.position;
            var depth = 0;
            for (; scan < tokens.Count; scan++)
            {
                var token = tokens[scan];
                var stop = false;
                switch (token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        break;
                    case Operator { Character: ')' }:
                        stop = depth == 0;
                        depth--;
                        break;
                    case Operator { Character: '+' or '-' or '*' or '/' or '%' } when depth == 0 && scan > start
                        && tokens[scan - 1] is not Operator { Character: '(' or ',' or '+' or '-' or '*' or '/' or '%' or '=' or '<' or '>' }:
                        arithmetic = true;
                        break;
                    case Name or Literal or Numeric or Operator { Character: '.' } or ReservedKeyword { Keyword: Keyword.Null or Keyword.Default }:
                        break;
                    default:
                        stop = depth == 0;
                        break;
                }
                if (stop)
                    break;
            }
            var termSite = arithmetic && site == LiteralSite.Compared ? LiteralSite.Widened : site;
            var wrap = arithmetic && parenthesizeArithmetic;
            if (wrap)
                _ = this.body.Append('(');
            while (true)
            {
                if (!this.Term(termSite))
                    return false;
                if (this.Current is Operator { Character: '+' or '-' or '*' or '/' or '%' } op)
                {
                    this.position++;
                    _ = this.body.Append(op.Character);
                    continue;
                }
                break;
            }
            if (wrap)
                _ = this.body.Append(')');
            return true;
        }

        private bool Term(LiteralSite site)
        {
            switch (this.Current)
            {
                case Operator { Character: '(' }:
                    this.position++;
                    _ = this.body.Append('(');
                    if (!this.Expression(site, parenthesizeArithmetic: false) || !this.AtOperator(')'))
                        return false;
                    this.position++;
                    _ = this.body.Append(')');
                    return true;
                case Operator { Character: '-' } when this.Peek(1) is Numeric or Literal:
                    this.position++;
                    return this.LiteralTerm(site, negative: true);
                case Numeric or Literal:
                    return this.LiteralTerm(site, negative: false);
                case ReservedKeyword { Keyword: Keyword.Null }:
                    this.position++;
                    _ = this.body.Append("NULL");
                    return true;
                case ReservedKeyword { Keyword: Keyword.Default }:
                    this.position++;
                    _ = this.body.Append("DEFAULT");
                    return true;
                case ReservedKeyword { Keyword: Keyword.Convert } when this.Peek(1) is Operator { Character: '(' }:
                    return this.Conversion(site, cast: false);
                case Name { Value: var cast } when this.Peek(1) is Operator { Character: '(' } && cast.Equals("CAST", StringComparison.OrdinalIgnoreCase):
                    return this.Conversion(site, cast: true);
                case ReservedKeyword { Keyword: Keyword.Coalesce } coalesce when this.Peek(1) is Operator { Character: '(' }:
                    return this.Call(site, coalesce.Source.ToString(), upperCased: false);
                case Name name when this.Peek(1) is Operator { Character: '(' }:
                    // A function call: its name as written — an aggregate's
                    // upper case — and its arguments rendered; real declines a
                    // statement calling some (probed 2026-10-19 against SQL
                    // Server 2025).
                    return FunctionCase(name.Value) is { } upperCased && this.Call(site, upperCased ? name.Value.ToUpperInvariant() : name.Source.ToString(), upperCased);
                case Name:
                    return this.DottedName();
                default:
                    return false;
            }
        }

        /// <summary>A function call named <paramref name="written"/>, the cursor on its name: an aggregate's <c>*</c> argument, else its arguments rendered.</summary>
        private bool Call(LiteralSite site, string written, bool upperCased)
        {
            this.position += 2;
            _ = this.body.Append(written).Append('(');
            if (upperCased && this.AtOperator('*'))
            {
                this.position++;
                _ = this.body.Append('*');
            }
            else if (!this.AtOperator(')'))
            {
                while (true)
                {
                    if (!this.Expression(site == LiteralSite.Kept ? site : LiteralSite.Widened, parenthesizeArithmetic: false))
                        return false;
                    if (!this.AtOperator(','))
                        break;
                    this.position++;
                    _ = this.body.Append(',');
                }
            }
            if (!this.AtOperator(')'))
                return false;
            this.position++;
            _ = this.body.Append(')');
            return true;
        }

        /// <summary>
        /// Whether real renders a call of <paramref name="name"/> in upper case
        /// — the aggregates — or as written, or null for a function whose call
        /// real doesn't parameterize: <c>MIN</c>, <c>MAX</c>, <c>LEN</c>,
        /// <c>UPPER</c> and <c>ISNULL</c> (probed 2026-10-19 against SQL Server
        /// 2025).
        /// </summary>
        private static bool? FunctionCase(string name)
        {
            Span<char> upper = stackalloc char[name.Length];
            return upper[..name.AsSpan().ToUpperInvariant(upper)] switch
            {
                "AVG" or "CHECKSUM_AGG" or "COUNT" or "COUNT_BIG" or "STDEV" or "STRING_AGG" or "SUM" => true,
                "ISNULL" or "LEN" or "MAX" or "MIN" or "UPPER" => null,
                _ => false,
            };
        }

        /// <summary>
        /// A <c>CAST</c> or two-argument <c>CONVERT</c>, which real renders as
        /// <c>CONVERT([type](length),expression)</c> (probed 2026-10-19 against
        /// SQL Server 2025).
        /// </summary>
        private bool Conversion(LiteralSite site, bool cast)
        {
            this.position += 2;
            var expressionAt = this.body.Length;
            string? type;
            if (cast)
            {
                if (!this.Expression(site == LiteralSite.Kept ? site : LiteralSite.Widened, parenthesizeArithmetic: false)
                    || this.Current is not ReservedKeyword { Keyword: Keyword.As })
                {
                    return false;
                }
                this.position++;
                type = this.TypeName();
            }
            else
            {
                type = this.TypeName();
                if (type is null || !this.AtOperator(','))
                    return false;
                this.position++;
                if (!this.Expression(site == LiteralSite.Kept ? site : LiteralSite.Widened, parenthesizeArithmetic: false))
                    return false;
            }
            if (type is null || !this.AtOperator(')'))
                return false;
            this.position++;
            var expression = this.body.ToString(expressionAt, this.body.Length - expressionAt);
            _ = this.body.Remove(expressionAt, this.body.Length - expressionAt).Append("CONVERT(").Append(type).Append(',').Append(expression).Append(')');
            return true;
        }

        /// <summary>A conversion's target type as real renders it — <c>[varchar](10)</c> — or null for one past this renderer.</summary>
        private string? TypeName()
        {
            if (this.Current is not Name type)
                return null;
            this.position++;
            var rendered = "[" + type.Value + "]";
            if (!this.AtOperator('('))
                return rendered;
            this.position++;
            if (this.Current is not Numeric length)
                return null;
            this.position++;
            rendered += "(" + length.Source.ToString();
            if (this.AtOperator(','))
            {
                this.position++;
                if (this.Current is not Numeric scale)
                    return null;
                this.position++;
                rendered += "," + scale.Source.ToString();
            }
            if (!this.AtOperator(')'))
                return null;
            this.position++;
            return rendered + ")";
        }

        private bool LiteralTerm(LiteralSite site, bool negative)
        {
            var token = this.Current!;
            this.position++;
            if (site == LiteralSite.Kept)
            {
                if (token is Numeric)
                    _ = this.body.Append('(').Append(negative ? "-" : "").Append(token.Source).Append(')');
                else
                    _ = this.body.Append(negative ? "-" : "").Append(token.Source);
                return true;
            }
            var value = token switch
            {
                Numeric numeric => numeric.Value,
                Literal literal => literal.Value,
                _ => default,
            };
            if (value.IsNull)
                return false;
            if (ParameterType(value, negative, site) is not { } type)
                return false;
            var number = this.parameters.Count + 1;
            this.parameters.Add((number, type, this.inWhere));
            _ = this.body.Append('@').Append(number.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private static string? ParameterType(SqlValue value, bool negative, LiteralSite site) => value.Type switch
        {
            Int32SqlType when site == LiteralSite.Widened => "int",
            Int32SqlType => (negative ? -(long)value.AsInt32 : value.AsInt32) switch
            {
                >= 0 and <= 255 => "tinyint",
                >= short.MinValue and <= short.MaxValue => "smallint",
                _ => "int",
            },
            DecimalSqlType d => string.Create(CultureInfo.InvariantCulture, $"numeric({d.precision},{d.scale})"),
            FloatSqlType => "float",
            MoneySqlType => "money",
            VarcharSqlType or CharSqlType => "varchar(8000)",
            NVarcharSqlType or NCharSqlType => "nvarchar(4000)",
            VarbinarySqlType or BinarySqlType => "varbinary(8000)",
            _ => null,
        };

        private bool ColumnName()
        {
            if (this.Current is not Name)
                return false;
            return this.DottedName();
        }

        // name [. name …], each part bracketed.
        private bool DottedName()
        {
            if (this.Current is not Name first)
                return false;
            this.position++;
            AppendBracketed(this.body, first);
            while (this.AtOperator('.'))
            {
                this.position++;
                _ = this.body.Append('.');
                if (this.AtOperator('*'))
                {
                    this.position++;
                    _ = this.body.Append('*');
                    return true;
                }
                if (this.Current is not Name part)
                    return false;
                this.position++;
                AppendBracketed(this.body, part);
            }
            return true;
        }

        private static void AppendBracketed(StringBuilder builder, Name name) =>
            _ = builder.Append('[').Append(name.Value.Replace("]", "]]", StringComparison.Ordinal)).Append(']');
    }
}
