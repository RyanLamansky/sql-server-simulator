using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Folds the multi-word ANSI type synonyms SQL Server accepts wherever a type
/// name parses (CAST / CONVERT / DECLARE / column and parameter declarations)
/// into the single canonical base-type name the simulator's
/// <see cref="Storage.SqlType.GetByName"/> resolves. The single-word synonyms
/// (<c>integer</c> → int, <c>dec</c> → decimal, <c>character</c> → char,
/// <c>rowversion</c> → timestamp) map inside <c>GetByName</c> itself; only the
/// multi-token forms need the parser to consume the trailing word(s):
/// <list type="bullet">
/// <item><c>double precision</c> → <c>float</c></item>
/// <item><c>character varying</c> / <c>char varying</c> → <c>varchar</c></item>
/// <item><c>national character</c> / <c>national char</c> → <c>nchar</c></item>
/// <item><c>national character varying</c> / <c>national char varying</c> → <c>nvarchar</c></item>
/// <item><c>binary varying</c> → <c>varbinary</c></item>
/// <item><c>national text</c> → <c>ntext</c></item>
/// </list>
/// The leading word may be a reserved keyword (<c>double</c>, <c>national</c>)
/// or an identifier (<c>character</c>, <c>char</c>, <c>binary</c>), so the fold
/// runs ahead of a site's "type name must be an identifier" guard.
/// </summary>
internal static class TypeNameSynonyms
{
    /// <summary>
    /// Reads the leading type-name token(s) at the current cursor into a
    /// resolvable name pair. Multi-word synonyms fold to a
    /// <see cref="SynonymTypeName"/> leaf (1-part); anything else routes
    /// through <see cref="BatchContext.ParseObjectName"/> for the ordinary
    /// 1–2-part (schema-qualified alias type) form. On entry
    /// <see cref="ParserContext.Token"/> is the first type token; on return it
    /// is the leaf token (the last consumed word), matching the single-token
    /// contract the callers already advance past.
    /// </summary>
    /// <exception cref="SimulatedSqlException">The token isn't a type-name start (Msg 102).</exception>
    internal static (MultiPartName Qualified, Name Leaf) ReadTypeName(ParserContext context)
    {
        if (TryFoldMultiWordType(context) is { } synonym)
            return (new MultiPartName(synonym.Value), synonym);
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var qualified = BatchContext.ParseObjectName(context);
        return (qualified, (Name)context.Token);
    }

    /// <summary>
    /// When the cursor is on the first token of a multi-word type synonym,
    /// consumes its constituent word tokens and returns the folded canonical
    /// leaf (cursor left on the last consumed word). Otherwise leaves the
    /// cursor untouched and returns <see langword="null"/>.
    /// </summary>
    internal static SynonymTypeName? TryFoldMultiWordType(ParserContext context)
    {
        var firstToken = context.Token;
        if (firstToken is null)
            return null;

        var checkpoint = context.SaveCheckpoint();
        switch (firstToken)
        {
            case ReservedKeyword { Keyword: Keyword.Double }:
                if (context.MoveNext() && IsWord(context.Token, "precision"))
                    return Fold("float", firstToken, context.Token);
                break;

            case ReservedKeyword { Keyword: Keyword.National }:
                if (context.MoveNext())
                {
                    var word = context.Token;
                    if (IsWord(word, "character") || IsWord(word, "char"))
                    {
                        var afterChar = context.SaveCheckpoint();
                        if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.Varying })
                            return Fold("nvarchar", firstToken, context.Token);
                        context.RestoreCheckpoint(afterChar);
                        return Fold("nchar", firstToken, word);
                    }
                    if (IsWord(word, "text"))
                        return Fold("ntext", firstToken, word);
                }
                break;

            case Name when IsWord(firstToken, "character") || IsWord(firstToken, "char"):
                if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.Varying })
                    return Fold("varchar", firstToken, context.Token);
                break;

            case Name when IsWord(firstToken, "binary"):
                if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.Varying })
                    return Fold("varbinary", firstToken, context.Token);
                break;
        }

        context.RestoreCheckpoint(checkpoint);
        return null;
    }

    private static SynonymTypeName Fold(string canonical, Token first, Token last) =>
        new(canonical, first, last.EndIndex);

    private static bool IsWord(Token? token, string word) =>
        token is Name name && name.Span.Equals(word, StringComparison.OrdinalIgnoreCase);
}
