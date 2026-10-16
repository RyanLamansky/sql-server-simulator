using System.Globalization;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The template real's forced parameterization makes of a statement, which
/// <c>sp_get_query_template</c> returns and Query Store stores, behind its
/// declaration, for a database whose <c>PARAMETERIZATION</c> is
/// <c>FORCED</c>: <c>select * from t where a = @0</c> with <c>@0 int</c> for
/// <c>SELECT * FROM t WHERE a = 1</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rendering rules were read off SQL Server 2025 (probed 2026-10-15):
/// the statement's tokens joined by single spaces, comments dropped, every
/// reserved keyword in lower case and every other token as written — each
/// operator character its own token, so <c>&gt;=</c> renders <c>&gt; =</c> —
/// and a trailing <c>;</c> dropped but a <c>MERGE</c>'s. A sign written
/// before a number joins it, parentheses and all (<c>-(5)</c> is one
/// literal), unless the number is an operand of arithmetic.
/// </para>
/// <para>
/// Literals number <c>@0</c>, <c>@1</c> … in text order. Those in a select
/// list (a subquery's in it included), <c>TOP</c>, <c>GROUP BY</c>,
/// <c>HAVING</c>, <c>ORDER BY</c>, <c>OFFSET</c> / <c>FETCH</c>,
/// <c>TABLESAMPLE</c>, <c>OUTPUT</c>, <c>OPTION</c> or <c>FOR</c> clause, or
/// a <c>LIKE</c> pattern or escape take no number; an operand of arithmetic,
/// a <c>CONVERT</c> / <c>TRY_CONVERT</c> style and an ODBC escape's take one
/// but stay as written; every other literal becomes its parameter. A
/// parameter types by its literal: <c>int</c> for an integer <c>int</c>
/// holds (its sign included), <c>numeric(38,s)</c> for a wider integer or a
/// decimal, <c>float</c>, <c>money</c>, <c>varchar(8000)</c> /
/// <c>nvarchar(4000)</c> / <c>varbinary(8000)</c> or their <c>max</c> past
/// that.
/// </para>
/// <para>
/// Only a <c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or
/// <c>MERGE</c> is parameterized, and not one that reads a variable, asks
/// for <c>RECOMPILE</c>, writes through <c>EXEC</c> or names a cursor's
/// current row.
/// </para>
/// </remarks>
internal static class ForcedParameterization
{
    /// <summary>A statement's template and the parameters its literals became, in number order.</summary>
    internal sealed class Template(string text, List<(int Number, string Type, bool AfterWhere)> parameters, bool isUpdate)
    {
        public readonly string Text = text;
        public readonly List<(int Number, string Type, bool AfterWhere)> Parameters = parameters;
        private readonly bool isUpdate = isUpdate;

        /// <summary>The parameters as <c>sp_get_query_template</c> declares them, in number order.</summary>
        public string Declaration => Declare(this.Parameters.Select(static parameter => parameter));

        /// <summary>
        /// The parameters as Query Store's stored text declares them: an
        /// <c>UPDATE</c>'s <c>WHERE</c> clause's ahead of the rest, as
        /// <c>(@1 int,@0 int)update t set a = @0 where c = @1</c>.
        /// </summary>
        public string StoredDeclaration => this.isUpdate
            ? Declare(this.Parameters.Where(static parameter => parameter.AfterWhere).Concat(this.Parameters.Where(static parameter => !parameter.AfterWhere)))
            : this.Declaration;

        private static string Declare(IEnumerable<(int Number, string Type, bool AfterWhere)> parameters) =>
            string.Join(',', parameters.Select(static parameter => string.Create(CultureInfo.InvariantCulture, $"@{parameter.Number} {parameter.Type}")));
    }

    /// <summary>Where a token sits, which decides what becomes of a literal there.</summary>
    private enum Clause : byte
    {
        /// <summary>A literal becomes a parameter.</summary>
        Parameterized,

        /// <summary>A literal is kept as written and takes no number.</summary>
        Kept,
    }

    /// <summary>One parenthesized level of the statement.</summary>
    private sealed class Frame(bool kept, bool convertStyle, bool odbc)
    {
        public Clause Clause;
        public readonly bool Kept = kept;
        public readonly bool IsConvert = convertStyle;
        public readonly bool IsOdbc = odbc;
        public int Commas;
        public bool InLikePattern;
    }

    /// <summary>
    /// The template of the statement <paramref name="tokens"/> spell — no
    /// whitespace or comment among them — or null with the state of real's Msg
    /// 10524: 1 when no literal took a number (or the statement is no kind
    /// real parameterizes), 2 when every one that did stays as written.
    /// </summary>
    public static Template? Parameterize(List<Token> tokens, out byte refusalState)
    {
        refusalState = 1;
        var start = 0;
        var end = tokens.Count;
        while (start < end && tokens[start] is Operator { Character: ';' })
            start++;
        var lead = start < end ? tokens[start] : null;
        if (lead is not (ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge or Keyword.With } or Operator { Character: '(' }))
            return null;
        var isMerge = lead is ReservedKeyword { Keyword: Keyword.Merge };
        while (end > start && tokens[end - 1] is Operator { Character: ';' } && !(isMerge && end - 1 > start && tokens[end - 2] is not Operator { Character: ';' }))
            end--;

        for (var i = start; i < end; i++)
        {
            switch (tokens[i])
            {
                case AtPrefixedString:
                case ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute }:
                case ReservedKeyword { Keyword: Keyword.Current } when i + 1 < end && tokens[i + 1] is ReservedKeyword { Keyword: Keyword.Of }:
                case UnquotedString word when word.Span.Equals("RECOMPILE", StringComparison.OrdinalIgnoreCase):
                    return null;
            }
        }

        var parts = new List<string>(end - start);
        var parameters = new List<(int Number, string Type, bool AfterWhere)>();
        var frames = new Stack<Frame>();
        frames.Push(new Frame(kept: false, convertStyle: false, odbc: false));
        var numbered = 0;
        var afterTopLevelWhere = false;
        for (var i = start; i < end; i++)
        {
            var token = tokens[i];
            var frame = frames.Peek();
            switch (token)
            {
                case Operator { Character: '(' or '{' } open:
                    var previous = i > start ? tokens[i - 1] : null;
                    var convert = open.Character == '(' && previous is ReservedKeyword { Keyword: Keyword.Convert or Keyword.Try_Convert };
                    // A type's length or precision — CAST(… AS varchar(10)),
                    // CONVERT(varchar(10), …) — belongs to the type, and a
                    // table hint list to the table: neither holds a literal.
                    var typeArguments = open.Character == '(' && previous is Name && i - 2 >= start
                        && (tokens[i - 2] is ReservedKeyword { Keyword: Keyword.As } || (frame.IsConvert && frame.Commas == 0));
                    var tableHints = open.Character == '(' && previous is ReservedKeyword { Keyword: Keyword.With };
                    frames.Push(new Frame(typeArguments || tableHints || frame.Kept || frame.Clause == Clause.Kept || frame.InLikePattern, convert, open.Character == '{' || frame.IsOdbc));
                    parts.Add(Text(open));
                    continue;
                case Operator { Character: ')' or '}' } close:
                    if (frames.Count > 1)
                        _ = frames.Pop();
                    parts.Add(Text(close));
                    continue;
                case Operator { Character: ',' } comma:
                    frame.Commas++;
                    parts.Add(Text(comma));
                    continue;
                case ReservedKeyword keyword:
                    ApplyClause(frame, keyword.Keyword);
                    if (frames.Count == 1 && keyword.Keyword == Keyword.Where)
                        afterTopLevelWhere = true;
#pragma warning disable CA1308 // real renders a reserved keyword in lower case; this is output text, never a comparison key
                    parts.Add(Text(keyword).ToLowerInvariant());
#pragma warning restore CA1308
                    continue;
                case UnquotedString word when word.Span.Equals("OFFSET", StringComparison.OrdinalIgnoreCase) || word.Span.Equals("OUTPUT", StringComparison.OrdinalIgnoreCase):
                    frame.Clause = Clause.Kept;
                    frame.InLikePattern = false;
                    parts.Add(Text(word));
                    continue;
            }

            // A literal, a sign that joins the literal after it, or anything else as written.
            var literalStart = i;
            var literalIndex = i;
            var signed = false;
            if (token is Operator { Character: '-' or '+' } && IsUnarySign(tokens, i, start) && SignedLiteralEnd(tokens, i + 1, end) is { } signedEnd
                && !IsArithmeticOperand(tokens, i, signedEnd, start, end))
            {
                signed = true;
                literalIndex = i + 1;
                while (tokens[literalIndex] is Operator { Character: '(' or '-' or '+' })
                    literalIndex++;
                i = signedEnd - 1;
            }
            else if (token is not (Literal or Numeric))
            {
                parts.Add(Text(token));
                continue;
            }

            var kept = frame.Kept || frame.Clause == Clause.Kept || frame.InLikePattern;
            if (kept)
            {
                AppendAsWritten(parts, tokens, literalStart, i + 1);
                continue;
            }
            var number = numbered++;
            var staysWritten = frame.IsOdbc
                || (frame.IsConvert && frame.Commas == 2)
                || (!signed && IsArithmeticOperand(tokens, literalStart, i + 1, start, end));
            var literal = tokens[literalIndex];
            var negative = false;
            for (var sign = literalStart; sign < literalIndex; sign++)
                negative ^= tokens[sign] is Operator { Character: '-' };
            if (staysWritten || ParameterType(literal, negative) is not { } type)
            {
                AppendAsWritten(parts, tokens, literalStart, i + 1);
                continue;
            }
            parameters.Add((number, type, afterTopLevelWhere));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"@{number}"));
        }

        if (numbered == 0)
            return null;
        if (parameters.Count == 0)
        {
            refusalState = 2;
            return null;
        }
        return new Template(string.Join(' ', parts), parameters, lead is ReservedKeyword { Keyword: Keyword.Update });
    }

    /// <summary>Moves <paramref name="frame"/> into the clause <paramref name="keyword"/> opens, if it opens one.</summary>
    private static void ApplyClause(Frame frame, Keyword keyword)
    {
        switch (keyword)
        {
            case Keyword.Like:
            case Keyword.Escape:
                frame.InLikePattern = true;
                return;
            case Keyword.And or Keyword.Or or Keyword.Then or Keyword.When or Keyword.Else or Keyword.End or Keyword.Not:
                frame.InLikePattern = false;
                return;
            case Keyword.Select or Keyword.Top or Keyword.Group or Keyword.Having or Keyword.Order or Keyword.Option or Keyword.For or Keyword.TableSample or Keyword.Fetch:
                frame.Clause = Clause.Kept;
                frame.InLikePattern = false;
                return;
            case Keyword.From or Keyword.Where or Keyword.On or Keyword.Set or Keyword.Values or Keyword.Into or Keyword.Update or Keyword.Delete
                or Keyword.Insert or Keyword.Merge or Keyword.Union or Keyword.Except or Keyword.Intersect or Keyword.Join:
                frame.Clause = Clause.Parameterized;
                frame.InLikePattern = false;
                return;
        }
    }

    /// <summary>
    /// Whether the <c>+</c> or <c>-</c> at <paramref name="index"/> signs what
    /// follows rather than adding or subtracting: nothing ahead of it is an
    /// operand.
    /// </summary>
    private static bool IsUnarySign(List<Token> tokens, int index, int start) =>
        tokens[index] is Operator { Character: '-' or '+' } && (index == start || !EndsOperand(tokens[index - 1]));

    /// <summary>
    /// The end of a number at <paramref name="index"/>, bare, signed again or
    /// parenthesized (<c>5</c>, <c>- 5</c>, <c>(5)</c>, <c>((5))</c>), or null
    /// for anything else.
    /// </summary>
    private static int? SignedLiteralEnd(List<Token> tokens, int index, int end)
    {
        while (index < end && tokens[index] is Operator { Character: '-' or '+' })
            index++;
        var depth = 0;
        while (index < end && tokens[index] is Operator { Character: '(' })
        {
            depth++;
            index++;
        }
        if (index >= end || tokens[index] is not (Numeric or Literal { Value.Type: MoneySqlType }))
            return null;
        index++;
        for (var i = 0; i < depth; i++, index++)
        {
            if (index >= end || tokens[index] is not Operator { Character: ')' })
                return null;
        }
        return index;
    }

    /// <summary>
    /// Whether the literal spanning <paramref name="from"/> to
    /// <paramref name="to"/> is an operand of arithmetic on either side, a
    /// sign ahead of it read past: <c>b - -5</c>'s 5 is one.
    /// </summary>
    private static bool IsArithmeticOperand(List<Token> tokens, int from, int to, int start, int end)
    {
        if (to < end && tokens[to] is Operator { Character: '+' or '-' or '*' or '/' or '%' })
            return true;
        var before = from - 1;
        while (before > start && IsUnarySign(tokens, before, start))
            before--;
        return before > start && tokens[before] is Operator { Character: '+' or '-' or '*' or '/' or '%' } && EndsOperand(tokens[before - 1]);
    }

    /// <summary>Whether <paramref name="token"/> can end an operand, so an operator after it is binary.</summary>
    private static bool EndsOperand(Token token) => token switch
    {
        Name or Literal or Numeric or AtPrefixedString or DoubleAtPrefixedString or Operator { Character: ')' } => true,
        ReservedKeyword { Keyword: Keyword.Null or Keyword.End or Keyword.User or Keyword.Current_Date or Keyword.Current_Time or Keyword.Current_Timestamp or Keyword.Current_User or Keyword.Session_User or Keyword.System_user } => true,
        _ => false,
    };

    private static void AppendAsWritten(List<string> parts, List<Token> tokens, int from, int to)
    {
        for (var i = from; i < to; i++)
            parts.Add(Text(tokens[i]));
    }

    /// <summary>A token as written; a delimited identifier, whose token stops short of its closing delimiter, closed again.</summary>
    private static string Text(Token token) => token switch
    {
        DelimitedIdentifier { Source: ['[', ..] } bracketed => $"[{bracketed.Value.Replace("]", "]]", StringComparison.Ordinal)}]",
        DelimitedIdentifier { Source: ['"', ..] } quoted => $"\"{quoted.Value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
        _ => token.Source.ToString(),
    };

    /// <summary>The type a literal's parameter declares, or null for one real wouldn't parameterize.</summary>
    private static string? ParameterType(Token literal, bool negative)
    {
        var value = literal switch
        {
            Numeric numeric => numeric.Value,
            Literal text => text.Value,
            _ => default,
        };
        if (value.IsNull)
            return null;
        return value.Type switch
        {
            Int32SqlType => "int",
            DecimalSqlType { scale: 0 } when negative && literal.Source is "2147483648" => "int",
            DecimalSqlType d => string.Create(CultureInfo.InvariantCulture, $"numeric(38,{d.scale})"),
            FloatSqlType => "float",
            MoneySqlType => "money",
            NVarcharSqlType or NCharSqlType => value.AsString.Length > 4000 ? "nvarchar(max)" : "nvarchar(4000)",
            VarcharSqlType or CharSqlType => value.AsString.Length > 8000 ? "varchar(max)" : "varchar(8000)",
            VarbinarySqlType or BinarySqlType => value.AsBytes.Length > 8000 ? "varbinary(max)" : "varbinary(8000)",
            _ => null,
        };
    }
}
