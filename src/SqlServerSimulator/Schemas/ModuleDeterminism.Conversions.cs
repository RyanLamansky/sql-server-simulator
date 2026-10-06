using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Frozen;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The <c>CAST</c> / <c>CONVERT</c> half of <c>IsDeterministic</c>: real SQL
/// Server classifies a conversion <em>between a date/time type and a character
/// string</em> as nondeterministic unless an explicit style from the
/// deterministic set is supplied, in either direction, and leaves every other
/// conversion alone (<c>CONVERT(varchar(20), &lt;int&gt;)</c> and
/// <c>CONVERT(datetime, &lt;int&gt;)</c> are both deterministic — probed).
/// </summary>
/// <remarks>
/// <para>
/// The named type — <c>CAST(x AS T)</c>'s <c>T</c>, <c>CONVERT(T, x)</c>'s —
/// and the style literal read straight off the token stream. The other side is
/// the expression, whose type the token scan has to infer: the walk classifies
/// the source extent by the evidence it carries, resolving a bare or
/// alias-qualified column name against the columns of the tables and views the
/// body references, a <c>@name</c> against the module's declared parameters and
/// the body's own <c>DECLARE</c>s, a character literal as a string, and a
/// nested <c>CAST</c> / <c>CONVERT</c> as its own named type. A call to a
/// built-in whose result family doesn't follow its arguments
/// (<see cref="FixedResultFamilies"/>) contributes that family and hides its
/// arguments, which is what keeps <c>CONVERT(varchar(20), YEAR(&lt;date&gt;))</c>
/// deterministic; every other call propagates, so <c>ISNULL</c> / <c>CASE</c> /
/// an aggregate over a date column still reads as a date.
/// </para>
/// <para>
/// A style written as a constant expression (<c>121 + 0</c>) is folded as
/// real folds it, and a qualified call to a scalar user function contributes
/// the function's declared return family. What stays undecidable, erring toward
/// <em>deterministic</em> — the answer the module already had, so the
/// inference only ever moves a cell toward real: a column name the body's
/// tables don't carry, which is the output of a CTE or derived table that no
/// aliased conversion typed (<c>CAST(a AS varchar(20)) s</c> types <c>s</c>).
/// </para>
/// </remarks>
internal static partial class ModuleDeterminism
{
    /// <summary>
    /// The side of the date/time ↔ character-string pair a type or expression
    /// falls on. <see cref="Other"/> covers everything the rule ignores.
    /// </summary>
    private enum ConversionFamily
    {
        Other,
        DateTimeValue,
        CharacterString,
    }

    /// <summary>
    /// Styles that make a date/time ↔ character-string conversion
    /// deterministic. Probed one style per function across 0-25, 100-114,
    /// 120-131: everything outside this set — including no style at all —
    /// leaves the conversion nondeterministic.
    /// </summary>
    private static readonly FrozenSet<int> DeterministicStyles =
        new[] { 20, 21, 101, 102, 103, 104, 105, 108, 110, 111, 112, 114, 120, 121, 126, 127, 130, 131 }.ToFrozenSet();

    /// <summary>
    /// Built-ins whose result family is fixed rather than taken from their
    /// arguments, so the source-extent walk reads the function itself and skips
    /// what it wraps. <see cref="ConversionFamily.Other"/> entries are the
    /// point of the table: <c>YEAR</c> / <c>DATEDIFF</c> / <c>LEN</c> take a
    /// date or a string and return a number, and real calls the conversion that
    /// wraps them deterministic.
    /// </summary>
    private static readonly FrozenDictionary<string, ConversionFamily> FixedResultFamilies =
        new Dictionary<string, ConversionFamily>
        {
            ["ASCII"] = ConversionFamily.Other,
            ["CHARINDEX"] = ConversionFamily.Other,
            ["DATALENGTH"] = ConversionFamily.Other,
            ["DATEDIFF"] = ConversionFamily.Other,
            ["DATEDIFF_BIG"] = ConversionFamily.Other,
            ["DATEPART"] = ConversionFamily.Other,
            ["DAY"] = ConversionFamily.Other,
            ["DIFFERENCE"] = ConversionFamily.Other,
            ["ISDATE"] = ConversionFamily.Other,
            ["ISJSON"] = ConversionFamily.Other,
            ["ISNUMERIC"] = ConversionFamily.Other,
            ["LEN"] = ConversionFamily.Other,
            ["MONTH"] = ConversionFamily.Other,
            ["PATINDEX"] = ConversionFamily.Other,
            ["UNICODE"] = ConversionFamily.Other,
            ["YEAR"] = ConversionFamily.Other,
            ["DATEADD"] = ConversionFamily.DateTimeValue,
            ["DATEFROMPARTS"] = ConversionFamily.DateTimeValue,
            ["DATETIME2FROMPARTS"] = ConversionFamily.DateTimeValue,
            ["DATETIMEFROMPARTS"] = ConversionFamily.DateTimeValue,
            ["DATETIMEOFFSETFROMPARTS"] = ConversionFamily.DateTimeValue,
            ["DATETRUNC"] = ConversionFamily.DateTimeValue,
            ["DATE_BUCKET"] = ConversionFamily.DateTimeValue,
            ["EOMONTH"] = ConversionFamily.DateTimeValue,
            ["GETDATE"] = ConversionFamily.DateTimeValue,
            ["GETUTCDATE"] = ConversionFamily.DateTimeValue,
            ["SMALLDATETIMEFROMPARTS"] = ConversionFamily.DateTimeValue,
            ["SWITCHOFFSET"] = ConversionFamily.DateTimeValue,
            ["SYSDATETIME"] = ConversionFamily.DateTimeValue,
            ["SYSDATETIMEOFFSET"] = ConversionFamily.DateTimeValue,
            ["SYSUTCDATETIME"] = ConversionFamily.DateTimeValue,
            ["TIMEFROMPARTS"] = ConversionFamily.DateTimeValue,
            ["TODATETIMEOFFSET"] = ConversionFamily.DateTimeValue,
            ["CONCAT"] = ConversionFamily.CharacterString,
            ["CONCAT_WS"] = ConversionFamily.CharacterString,
            ["DATENAME"] = ConversionFamily.CharacterString,
            ["FORMAT"] = ConversionFamily.CharacterString,
            ["LEFT"] = ConversionFamily.CharacterString,
            ["LOWER"] = ConversionFamily.CharacterString,
            ["LTRIM"] = ConversionFamily.CharacterString,
            ["QUOTENAME"] = ConversionFamily.CharacterString,
            ["REPLACE"] = ConversionFamily.CharacterString,
            ["REPLICATE"] = ConversionFamily.CharacterString,
            ["REVERSE"] = ConversionFamily.CharacterString,
            ["RIGHT"] = ConversionFamily.CharacterString,
            ["RTRIM"] = ConversionFamily.CharacterString,
            ["SOUNDEX"] = ConversionFamily.CharacterString,
            ["SPACE"] = ConversionFamily.CharacterString,
            ["STR"] = ConversionFamily.CharacterString,
            ["STUFF"] = ConversionFamily.CharacterString,
            ["SUBSTRING"] = ConversionFamily.CharacterString,
            ["TRANSLATE"] = ConversionFamily.CharacterString,
            ["TRIM"] = ConversionFamily.CharacterString,
            ["UPPER"] = ConversionFamily.CharacterString,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The family a declared type name falls on. Anything unrecognized —
    /// a numeric or binary type, an alias type, an ANSI synonym's first word —
    /// is <see cref="ConversionFamily.Other"/>, which takes the conversion out
    /// of the rule.
    /// </summary>
    private static ConversionFamily FamilyOfTypeName(ReadOnlySpan<char> name)
    {
        Span<char> lowered = stackalloc char[name.Length];
        _ = name.ToLowerInvariant(lowered);
        return lowered switch
        {
            // `character` and `national` open the ANSI spellings (`character
            // varying`, `national char`), each a string type on real.
            "char" or "character" or "national" or "nchar" or "ntext" or "nvarchar" or "sysname" or "text" or "varchar" => ConversionFamily.CharacterString,
            "date" or "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime" or "time" => ConversionFamily.DateTimeValue,
            _ => ConversionFamily.Other,
        };
    }

    // A type name as written or as a stored definition brackets it.
    private static ReadOnlySpan<char> TypeNameText(Token token) => token is Name name ? name.Span : token.Source;

    /// <summary>The family a stored column / parameter type falls on.</summary>
    private static ConversionFamily FamilyOfSqlType(SqlType type) =>
        type.Category switch
        {
            SqlTypeCategory.String => ConversionFamily.CharacterString,
            SqlTypeCategory.DateTime => ConversionFamily.DateTimeValue,
            _ => ConversionFamily.Other,
        };

    /// <summary>
    /// Whether every date/time ↔ character-string conversion in
    /// <paramref name="tokens"/> carries a deterministic style.
    /// <paramref name="nameFamilies"/> maps the column and <c>@variable</c>
    /// names the body can reach to their families.
    /// </summary>
    private static bool ConversionsAreDeterministic(List<Token> tokens, Dictionary<string, ConversionFamily> nameFamilies)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!TryReadConversion(tokens, i, out var conversion))
                continue;

            var wanted = conversion.Target switch
            {
                ConversionFamily.CharacterString => ConversionFamily.DateTimeValue,
                ConversionFamily.DateTimeValue => ConversionFamily.CharacterString,
                _ => ConversionFamily.Other,
            };
            if (wanted == ConversionFamily.Other || DeterministicStyles.Contains(conversion.Style))
                continue;

            if (ExtentReaches(tokens, conversion.SourceFrom, conversion.SourceTo, wanted, nameFamilies))
                return false;
        }
        return true;
    }

    /// <summary>
    /// One <c>CAST</c> / <c>CONVERT</c> site: its named target family, the
    /// half-open token range holding the converted expression, and the style
    /// argument (<c>-1</c> when there is none, or when it isn't a plain
    /// integer literal — real folds a constant expression there, the scan
    /// doesn't).
    /// </summary>
    private readonly struct ConversionSite(ConversionFamily target, int sourceFrom, int sourceTo, int style, int close)
    {
        internal readonly ConversionFamily Target = target;
        internal readonly int SourceFrom = sourceFrom;
        internal readonly int SourceTo = sourceTo;
        internal readonly int Style = style;

        /// <summary>The index of the call's closing parenthesis.</summary>
        internal readonly int Close = close;
    }

    /// <summary>
    /// Reads a conversion whose opening name sits at <paramref name="index"/>,
    /// in either the <c>CAST(x AS T)</c> or the <c>CONVERT(T, x[, style])</c>
    /// shape (both with their <c>TRY_</c> spellings, which real classifies
    /// identically).
    /// </summary>
    private static bool TryReadConversion(List<Token> tokens, int index, out ConversionSite conversion)
    {
        conversion = default;
        if (index + 2 >= tokens.Count || tokens[index + 1] is not Operator { Character: '(' })
            return false;

        var name = tokens[index].Source;
        var isCast = name.Equals("CAST", StringComparison.OrdinalIgnoreCase) || name.Equals("TRY_CAST", StringComparison.OrdinalIgnoreCase);
        if (!isCast && !name.Equals("CONVERT", StringComparison.OrdinalIgnoreCase) && !name.Equals("TRY_CONVERT", StringComparison.OrdinalIgnoreCase))
            return false;

        // Argument boundaries at the call's own nesting depth: the AS that
        // splits CAST, and the commas that split CONVERT.
        var depth = 0;
        var separators = new List<int>();
        var close = -1;
        for (var i = index + 1; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    if (--depth == 0)
                        close = i;
                    break;
                case Operator { Character: ',' } when depth == 1 && !isCast:
                case ReservedKeyword { Keyword: Keyword.As } when depth == 1 && isCast:
                    separators.Add(i);
                    break;
            }
            if (close >= 0)
                break;
        }
        if (close < 0 || separators.Count == 0)
            return false;

        if (isCast)
        {
            // CAST(x AS T): the last top-level AS is the type's, since a
            // nested CAST's own AS sits deeper.
            var asIndex = separators[^1];
            if (asIndex + 1 >= close)
                return false;
            conversion = new(FamilyOfTypeName(TypeNameText(tokens[asIndex + 1])), index + 2, asIndex, -1, close);
            return true;
        }

        var style = -1;
        var sourceTo = separators.Count > 1 ? separators[1] : close;
        if (separators.Count > 1)
        {
            // A stored definition writes the style parenthesized, `(112)`.
            var styleFrom = separators[1] + 1;
            var styleTo = close;
            while (styleTo - styleFrom > 2 && tokens[styleFrom] is Operator { Character: '(' } && tokens[styleTo - 1] is Operator { Character: ')' })
            {
                styleFrom++;
                styleTo--;
            }
            if (styleTo == styleFrom + 1 && tokens[styleFrom] is Numeric { Value.Type: Int32SqlType } styleLiteral)
                style = styleLiteral.Value.AsInt32;
            else if (TryFoldStyle(tokens, styleFrom, styleTo, out var folded))
                style = folded;
        }
        conversion = new(FamilyOfTypeName(TypeNameText(tokens[index + 2])), separators[0] + 1, sourceTo, style, close);
        return true;
    }

    /// <summary>
    /// Folds a style written as integer arithmetic — <c>121 + 0</c>,
    /// <c>60 * 2</c>, <c>-(-121)</c> — over literals, <c>+ - * / %</c> and
    /// parentheses, which real folds to the constant it is (probed 2026-09-29
    /// against SQL Server 2025). False for anything else.
    /// </summary>
    private static bool TryFoldStyle(List<Token> tokens, int from, int to, out int value)
    {
        var position = from;
        value = 0;
        if (!TryFoldSum(tokens, ref position, to, out var sum) || position != to)
            return false;
        value = sum;
        return true;
    }

    private static bool TryFoldSum(List<Token> tokens, ref int position, int to, out int value)
    {
        if (!TryFoldProduct(tokens, ref position, to, out value))
            return false;
        while (position < to && tokens[position] is Operator { Character: '+' or '-' } op)
        {
            position++;
            if (!TryFoldProduct(tokens, ref position, to, out var right))
                return false;
            value = op.Character == '+' ? value + right : value - right;
        }
        return true;
    }

    private static bool TryFoldProduct(List<Token> tokens, ref int position, int to, out int value)
    {
        if (!TryFoldUnary(tokens, ref position, to, out value))
            return false;
        while (position < to && tokens[position] is Operator { Character: '*' or '/' or '%' } op)
        {
            position++;
            if (!TryFoldUnary(tokens, ref position, to, out var right))
                return false;
            if (op.Character != '*' && right == 0)
                return false;
            value = op.Character switch
            {
                '*' => value * right,
                '/' => value / right,
                _ => value % right,
            };
        }
        return true;
    }

    private static bool TryFoldUnary(List<Token> tokens, ref int position, int to, out int value)
    {
        value = 0;
        if (position >= to)
            return false;
        switch (tokens[position])
        {
            case Operator { Character: '-' or '+' } sign:
                position++;
                if (!TryFoldUnary(tokens, ref position, to, out value))
                    return false;
                if (sign.Character == '-')
                    value = -value;
                return true;
            case Operator { Character: '(' }:
                position++;
                if (!TryFoldSum(tokens, ref position, to, out value) || position >= to || tokens[position] is not Operator { Character: ')' })
                    return false;
                position++;
                return true;
            case Numeric { Value.Type: Int32SqlType } number:
                value = number.Value.AsInt32;
                position++;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The <see cref="NameFamilies"/> key a scalar user function's return family is filed under.</summary>
    private static string FunctionKey(string schema, string name) => $"fn:{schema}.{name}";

    /// <summary>
    /// Whether the expression spanning <c>[from, to)</c> carries evidence of
    /// <paramref name="wanted"/> — the family the conversion's other side would
    /// have to be for real's rule to bite.
    /// </summary>
    private static bool ExtentReaches(
        List<Token> tokens,
        int from,
        int to,
        ConversionFamily wanted,
        Dictionary<string, ConversionFamily> nameFamilies)
    {
        for (var i = from; i < to; i++)
        {
            // A nested conversion states its own result type and hides what it
            // wraps; the outer loop checks the nested site's own style.
            if (TryReadConversion(tokens, i, out var nested))
            {
                if (nested.Target == wanted)
                    return true;
                i = SkipCall(tokens, i + 1, to);
                continue;
            }

            switch (tokens[i])
            {
                case Literal { Value.Type.Category: SqlTypeCategory.String }:
                    if (wanted == ConversionFamily.CharacterString)
                        return true;
                    break;
                case AtPrefixedString variable:
                    if (nameFamilies.TryGetValue(variable.Value, out var variableFamily) && variableFamily == wanted)
                        return true;
                    break;
                case Name:
                    {
                        // Walk a dotted chain to its leaf: only the leaf can be
                        // the column, the qualifiers being alias / schema.
                        var leaf = i;
                        while (leaf + 2 < to && tokens[leaf + 1] is Operator { Character: '.' } && tokens[leaf + 2] is Name)
                            leaf += 2;
                        if (leaf + 1 < to && tokens[leaf + 1] is Operator { Character: '(' })
                        {
                            // A qualified call to a scalar user function has the
                            // function's declared type, whatever its arguments.
                            if (leaf - 2 >= from && tokens[leaf - 1] is Operator { Character: '.' } && tokens[leaf - 2] is Name qualifier
                                && nameFamilies.TryGetValue(FunctionKey(qualifier.Value, ((Name)tokens[leaf]).Value), out var functionFamily))
                            {
                                if (functionFamily == wanted)
                                    return true;
                                i = SkipCall(tokens, leaf + 1, to);
                                continue;
                            }
                            if (FixedResultFamilies.TryGetValue(((Name)tokens[leaf]).Value, out var resultFamily))
                            {
                                if (resultFamily == wanted)
                                    return true;
                                i = SkipCall(tokens, leaf + 1, to);
                                continue;
                            }
                            // Anything else propagates: keep walking into it.
                            i = leaf;
                            break;
                        }
                        if (nameFamilies.TryGetValue(((Name)tokens[leaf]).Value, out var columnFamily) && columnFamily == wanted)
                            return true;
                        i = leaf;
                        break;
                    }
            }
        }
        return false;
    }

    /// <summary>
    /// Index of a call's closing parenthesis given the index of its opening
    /// one, or <paramref name="to"/> - 1 when the extent ends first.
    /// </summary>
    private static int SkipCall(List<Token> tokens, int open, int to)
    {
        var depth = 0;
        for (var i = open; i < to; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' } when --depth == 0:
                    return i;
            }
        }
        return to - 1;
    }

    /// <summary>
    /// The name → family map the source-extent walk resolves against: every
    /// column of every table and view the body names, the module's own declared
    /// parameters, and the <c>DECLARE @v &lt;type&gt;</c> locals in the body.
    /// Name-based like the expression-dependency surfaces, so a name two
    /// referenced tables carry with different types resolves to the first.
    /// </summary>
    private static Dictionary<string, ConversionFamily> NameFamilies(
        Database database,
        SchemaObject? module,
        List<Token> tokens,
        List<(string Qualifier, string Leaf)> referencedNames)
    {
        var families = new Dictionary<string, ConversionFamily>(StringComparer.OrdinalIgnoreCase);
        foreach (var (qualifier, leaf) in referencedNames)
        {
            if (!database.Schemas.TryGetValue(qualifier, out var schema))
                continue;
            if (schema.HeapTables.TryGetValue(leaf, out var table))
                AddColumns(families, table.Columns);
            if (schema.Views.TryGetValue(leaf, out var view))
                AddColumns(families, view.OutputColumns);
            if (schema.Functions.TryGetValue(leaf, out var scalarFunction) && scalarFunction is ScalarFunction scalar)
                families[FunctionKey(qualifier, leaf)] = FamilyOfSqlType(scalar.ReturnType);
        }

        if (module is UserDefinedFunction function)
        {
            foreach (var parameter in function.Parameters)
                families[parameter.Name] = FamilyOfSqlType(parameter.Type);
        }

        // `DECLARE @v <type>` — the type name follows the variable, and a
        // multi-variable DECLARE repeats the pair after each comma.
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i] is ReservedKeyword { Keyword: Keyword.Declare }
                && tokens[i + 1] is AtPrefixedString local
                && tokens[i + 2] is Name localType)
            {
                families[local.Value] = FamilyOfTypeName(localType.Source);
            }
        }

        // A column a derived table or CTE names by aliasing a conversion —
        // `(SELECT CAST(a AS varchar(20)) s FROM t) q` — carries the
        // conversion's named type wherever the body reads it back.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!TryReadConversion(tokens, i, out var site) || site.Target == ConversionFamily.Other)
                continue;
            var aliasAt = site.Close + 1 < tokens.Count && tokens[site.Close + 1] is ReservedKeyword { Keyword: Keyword.As } ? site.Close + 2 : site.Close + 1;
            if (aliasAt < tokens.Count && tokens[aliasAt] is Name alias && !families.ContainsKey(alias.Value))
                families[alias.Value] = site.Target;
        }

        // Any other select item a derived table or CTE names — `dtcol AS dt`,
        // `dt = DATEADD(…)`, a CTE's declared column list taking its items in
        // order — carries the family its expression reaches, so reading it back
        // converts as the expression would (probed 2026-10-06 against SQL
        // Server 2025: `CONVERT(varchar(20), d.dt)` over `(SELECT dtcol AS dt
        // FROM t) d` is nondeterministic, over `YEAR(dtcol) AS dt` it is not).
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is not ReservedKeyword { Keyword: Keyword.Select })
                continue;
            foreach (var (from, to) in SelectItems(tokens, i))
            {
                if (ItemAlias(tokens, from, to, out var expressionFrom, out var expressionTo) is { } alias && !families.ContainsKey(alias))
                    NoteFamily(families, alias, tokens, expressionFrom, expressionTo);
            }
        }
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] is not Name || tokens[i + 1] is not Operator { Character: '(' } || (i > 0 && tokens[i - 1] is Operator { Character: '.' }))
                continue;
            var declared = new List<string>();
            var at = i + 2;
            while (at + 1 < tokens.Count && tokens[at] is Name column && tokens[at + 1] is Operator { Character: ',' or ')' } separator)
            {
                declared.Add(column.Value);
                at += 2;
                if (separator.Character == ')')
                    break;
            }
            if (declared.Count == 0 || tokens[at - 1] is not Operator { Character: ')' }
                || at + 2 >= tokens.Count || tokens[at] is not ReservedKeyword { Keyword: Keyword.As }
                || tokens[at + 1] is not Operator { Character: '(' } || tokens[at + 2] is not ReservedKeyword { Keyword: Keyword.Select })
            {
                continue;
            }
            var items = SelectItems(tokens, at + 2);
            for (var k = 0; k < declared.Count && k < items.Count; k++)
            {
                var (from, to) = items[k];
                if (ItemAlias(tokens, from, to, out var expressionFrom, out var expressionTo) is not null)
                    (from, to) = (expressionFrom, expressionTo);
                if (!families.ContainsKey(declared[k]))
                    NoteFamily(families, declared[k], tokens, from, to);
            }
        }
        return families;
    }

    /// <summary>Files <paramref name="name"/> under the family the expression spanning <c>[from, to)</c> reaches, if any.</summary>
    private static void NoteFamily(Dictionary<string, ConversionFamily> families, string name, List<Token> tokens, int from, int to)
    {
        if (ExtentReaches(tokens, from, to, ConversionFamily.DateTimeValue, families))
            families[name] = ConversionFamily.DateTimeValue;
        else if (ExtentReaches(tokens, from, to, ConversionFamily.CharacterString, families))
            families[name] = ConversionFamily.CharacterString;
    }

    /// <summary>
    /// The extents of the select items of the <c>SELECT</c> at
    /// <paramref name="selectIndex"/>, each <c>[from, to)</c>, past any
    /// <c>DISTINCT</c> / <c>ALL</c> / <c>TOP</c> prefix and up to the clause or
    /// closing parenthesis that ends the list.
    /// </summary>
    private static List<(int From, int To)> SelectItems(List<Token> tokens, int selectIndex)
    {
        var items = new List<(int, int)>();
        var i = selectIndex + 1;
        while (i < tokens.Count && tokens[i] is ReservedKeyword { Keyword: Keyword.Distinct or Keyword.All })
            i++;
        if (i < tokens.Count && tokens[i] is ReservedKeyword { Keyword: Keyword.Top })
        {
            i++;
            i = i < tokens.Count && tokens[i] is Operator { Character: '(' } ? SkipCall(tokens, i, tokens.Count) + 1 : i + 1;
            if (i < tokens.Count && tokens[i] is ReservedKeyword { Keyword: Keyword.Percent })
                i++;
        }
        var depth = 0;
        var start = i;
        for (; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    continue;
                case Operator { Character: ')' } when depth == 0:
                case Operator { Character: ';' }:
                case ReservedKeyword { Keyword: var keyword } when depth == 0 && EndsSelectList(keyword):
                    if (i > start)
                        items.Add((start, i));
                    return items;
                case Operator { Character: ')' }:
                    depth--;
                    continue;
                case Operator { Character: ',' } when depth == 0:
                    if (i > start)
                        items.Add((start, i));
                    start = i + 1;
                    continue;
            }
        }
        if (i > start)
            items.Add((start, i));
        return items;
    }

    private static bool EndsSelectList(Keyword keyword) => keyword is Keyword.From or Keyword.Where or Keyword.Group or Keyword.Order
        or Keyword.Having or Keyword.Union or Keyword.Except or Keyword.Intersect or Keyword.Into or Keyword.Option or Keyword.For;

    /// <summary>
    /// The alias a select item spanning <c>[from, to)</c> names its column by —
    /// <c>expr AS alias</c>, <c>expr alias</c> or <c>alias = expr</c> — with the
    /// aliased expression's own extent, or null for an item without one.
    /// </summary>
    private static string? ItemAlias(List<Token> tokens, int from, int to, out int expressionFrom, out int expressionTo)
    {
        (expressionFrom, expressionTo) = (from, to);
        if (to - from >= 3 && tokens[from] is Name assigned && tokens[from + 1] is Operator { Character: '=' })
        {
            expressionFrom = from + 2;
            return assigned.Value;
        }
        if (to - from >= 3 && tokens[to - 1] is Name asAlias && tokens[to - 2] is ReservedKeyword { Keyword: Keyword.As })
        {
            expressionTo = to - 2;
            return asAlias.Value;
        }
        if (to - from >= 2 && tokens[to - 1] is Name bareAlias && tokens[to - 2] is not Operator { Character: '.' }
            && tokens[to - 2] is Name or Operator { Character: ')' } or Literal or Numeric)
        {
            expressionTo = to - 1;
            return bareAlias.Value;
        }
        return null;
    }

    private static void AddColumns(Dictionary<string, ConversionFamily> families, HeapColumn[] columns)
    {
        foreach (var column in columns)
        {
            if (FamilyOfSqlType(column.Type) is var family && family != ConversionFamily.Other && !families.ContainsKey(column.Name))
                families[column.Name] = family;
        }
    }
}
