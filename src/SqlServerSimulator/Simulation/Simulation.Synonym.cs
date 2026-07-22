using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE SYNONYM [schema.]name FOR base_object</c>. Cursor on
    /// entry: the <c>SYNONYM</c> word (matched by the CREATE dispatcher). The
    /// synonym is stored as a name indirection to <c>base_object</c>; FROM-
    /// source resolution redirects a reference to the synonym onto its base
    /// table / view (see <see cref="BatchContext.TryResolveTable"/>). A name
    /// already taken in the schema's object namespace raises Msg 2714.
    /// </summary>
    private static bool TryParseCreateSynonym(ParserContext context)
    {
        context.MoveNextRequired();
        var synonymName = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var baseObject = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveSchema(synonymName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(synonymName.Count >= 2 ? synonymName.ImmediateQualifier! : Database.DefaultSchemaName);

        var leaf = synonymName.Leaf;
        if (schema.HasNameInSharedNamespace(leaf) || schema.Synonyms.ContainsKey(leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(leaf);

        _ = schema.Synonyms.TryAdd(leaf, new Synonym(leaf, baseObject));
        return true;
    }

    /// <summary>
    /// Parses <c>DROP SYNONYM [IF EXISTS] [schema.]name</c>. Cursor on entry:
    /// the <c>SYNONYM</c> word (matched by the DROP dispatcher). A missing
    /// synonym without <c>IF EXISTS</c> raises Msg 3701 (State 5).
    /// </summary>
    private static bool TryParseDropSynonym(ParserContext context)
    {
        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                return false;
            ifExists = true;
            context.MoveNextRequired();
        }
        var synonymName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        var leaf = synonymName.Leaf;
        var removed = context.Batch.TryResolveSchema(synonymName, out var schema) && schema.Synonyms.TryRemove(leaf, out _);
        return removed || ifExists
            ? true
            : throw SimulatedSqlException.CannotDropSynonymDoesNotExist(leaf);
    }
}
