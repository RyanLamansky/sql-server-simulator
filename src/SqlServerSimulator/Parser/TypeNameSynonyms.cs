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
    /// Reads the first argument of a type spec's parenthesized list, with the
    /// cursor on it: a length or precision, or <c>MAX</c>
    /// (<see cref="Storage.SqlType.MaxLengthSentinel"/>). See
    /// <see cref="ReadIntegerTypeArgument"/> for the numbers it refuses.
    /// </summary>
    internal static int ReadFirstTypeArgument(ParserContext context, bool columnDefinition) =>
        context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
            ? Storage.SqlType.MaxLengthSentinel
            : ReadIntegerTypeArgument(context, columnDefinition);

    /// <summary>
    /// A type argument that has to be a number, with the cursor on it: an
    /// <c>int</c> literal. A decimal one — a fraction, or a whole number past
    /// <c>int</c> — is Msg 1080 echoing it in a column definition (a table's,
    /// a table type's or variable's, a table-valued function's result, an
    /// <c>OPENJSON</c> / <c>OPENXML</c> <c>WITH</c> clause, <c>WITH RESULT
    /// SETS</c>) and Msg 102 near it in any other type spec, and a float or
    /// anything else is Msg 102 near it everywhere (probed 2026-10-25 against
    /// SQL Server 2025).
    /// </summary>
    internal static int ReadIntegerTypeArgument(ParserContext context, bool columnDefinition) => context.Token switch
    {
        Numeric { Value: { IsNull: false } number } when number.Type == Storage.SqlType.Int32 => number.AsInt32,
        Numeric { Value: { IsNull: false } number } when columnDefinition && number.Type.Category == Storage.SqlTypeCategory.Decimal =>
            throw SimulatedSqlException.IntegerValueOutOfRange(context.Token.Source.ToString()),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// Reads the second argument of a type spec's parenthesized pair, with
    /// the cursor on it: an integer (a <c>decimal</c>'s scale, refused as
    /// <see cref="ReadIntegerTypeArgument"/> refuses), or for
    /// <c>vector</c> an unquoted base-type name, which reads as
    /// <see cref="Storage.VectorSqlType.Float32BaseType"/> when it is
    /// <c>float32</c>, as <see cref="Storage.VectorSqlType.Float16BaseType"/>
    /// when it is <c>float16</c> and the database's <c>PREVIEW_FEATURES</c>
    /// is on, and raises real's Msg 195 when it names anything else.
    /// A name after any other type is real's Msg 102 near the type's name.
    /// </summary>
    internal static int ReadSecondTypeArgument(ParserContext context, Name typeName, bool columnDefinition = false) => context.Token switch
    {
        Numeric => ReadIntegerTypeArgument(context, columnDefinition),
        UnquotedString when !string.Equals(typeName.Value, "vector", StringComparison.OrdinalIgnoreCase) => throw SimulatedSqlException.SyntaxErrorNear(typeName),
        UnquotedString baseType when string.Equals(baseType.Value, "float32", StringComparison.OrdinalIgnoreCase) => Storage.VectorSqlType.Float32BaseType,
        UnquotedString baseType when string.Equals(baseType.Value, "float16", StringComparison.OrdinalIgnoreCase)
            && context.Batch.CurrentDatabase.ScopedConfiguration.PreviewFeatures => Storage.VectorSqlType.Float16BaseType,
        UnquotedString baseType => throw SimulatedSqlException.VectorBaseTypeNotRecognized(baseType.Value),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

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
