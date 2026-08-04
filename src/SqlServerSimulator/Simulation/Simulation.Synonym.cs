using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE SYNONYM [schema.]name FOR base_object</c>. Cursor on
    /// entry: the <c>SYNONYM</c> word (matched by the CREATE dispatcher). The
    /// synonym is stored as a name indirection to <c>base_object</c>;
    /// resolution redirects a reference to the synonym onto its base object
    /// (see <see cref="BatchContext.TryResolveTable"/>). A name already taken
    /// in the schema's object namespace raises Msg 2714. The base object is
    /// not resolved here — real binds it lazily, so a synonym over a missing
    /// (or cross-database) base creates successfully and raises Msg 5313 at
    /// first use.
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

        schema.Database.RejectWriteWhenReadOnly();

        // Dual DDL gate, in real's probed order: the database-scope CREATE
        // SYNONYM permission (Msg 262 state 1), then ALTER on the target schema
        // (Msg 2760).
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, schema.Database, Permission.CreateSynonym))
            throw SimulatedSqlException.DatabasePermissionDenied("CREATE SYNONYM", schema.Database.Name);
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);

        var leaf = synonymName.Leaf;
        var synonym = new Synonym(schema, leaf, context.CurrentDatabase.AllocateObjectId(), context.Batch.CurrentStatement.UtcNow, baseObject);
        if (schema.HasNameInSharedNamespace(leaf) || !schema.Synonyms.TryAdd(leaf, synonym))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(leaf);
        // Real reports the base object as TargetObjectName with no type.
        RecordDdlEvent(context, "CREATE_SYNONYM", schema.Name, leaf, "SYNONYM", baseObject.Leaf);
        return true;
    }

    /// <summary>
    /// Parses <c>DROP SYNONYM [IF EXISTS] [schema.]name</c>. Cursor on entry:
    /// the <c>SYNONYM</c> word (matched by the DROP dispatcher). A name that
    /// belongs to an object of another kind raises Msg 3705 naming that kind
    /// (probe-confirmed, and <c>IF EXISTS</c> doesn't suppress it — the object
    /// does exist); a missing synonym without <c>IF EXISTS</c> raises Msg 3701
    /// (State 5).
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
        if (!context.Batch.TryResolveSchema(synonymName, out var schema))
            return ifExists ? true : throw SimulatedSqlException.CannotDropSynonymDoesNotExist(leaf);
        RejectDropOfOtherKind(schema, synonymName, "SYNONYM");
        if (schema.Synonyms.TryGetValue(leaf, out var target)
            && !PermissionEnforcement.HasDropAuthority(context.Batch, schema, target.ObjectId))
        {
            throw SimulatedSqlException.DropObjectPermissionDenied("synonym", leaf);
        }
        if (!schema.Synonyms.TryRemove(leaf, out var dropped))
            return ifExists ? true : throw SimulatedSqlException.CannotDropSynonymDoesNotExist(leaf);
        RecordDdlEvent(context, "DROP_SYNONYM", schema.Name, leaf, "SYNONYM", dropped.BaseObject.Leaf);
        return true;
    }

    /// <summary>
    /// Raises Msg 3705 when <paramref name="name"/> names an object of a kind
    /// other than <paramref name="attemptedDropKind"/> — the cross-kind
    /// rejection real applies between <c>DROP TABLE</c> and <c>DROP SYNONYM</c>
    /// in both directions. No-op when the name is free or already the right
    /// kind, so the caller's own missing-object path (Msg 3701) still runs.
    /// </summary>
    private static void RejectDropOfOtherKind(Schema schema, MultiPartName name, string attemptedDropKind)
    {
        var collation = schema.Database.Collation;
        foreach (var candidate in schema.SchemaObjects())
        {
            if (!collation.Equals(candidate.Name, name.Leaf))
                continue;
            var (noun, dropKind) = DropWordsFor(candidate);
            if (!dropKind.Equals(attemptedDropKind, StringComparison.Ordinal))
                throw SimulatedSqlException.CannotUseDropWithObjectKind(attemptedDropKind, name.ToString(), noun, dropKind);
            return;
        }
    }

    /// <summary>
    /// The noun real SQL Server uses for an object kind in the Msg 3705 body,
    /// paired with the <c>DROP</c> form that would succeed. Probe-confirmed per
    /// kind; note a table-valued function reads "table valued function" but is
    /// still dropped with <c>DROP FUNCTION</c>.
    /// </summary>
    private static (string Noun, string DropKind) DropWordsFor(SchemaObject obj) => obj switch
    {
        HeapTable => ("table", "TABLE"),
        View => ("view", "VIEW"),
        Procedure => ("procedure", "PROCEDURE"),
        InlineTableValuedFunction or MultiStatementTableValuedFunction => ("table valued function", "FUNCTION"),
        UserDefinedFunction => ("function", "FUNCTION"),
        Sequence => ("sequence", "SEQUENCE"),
        Trigger => ("trigger", "TRIGGER"),
        Synonym => ("synonym", "SYNONYM"),
        _ => throw new InvalidOperationException($"No DROP wording is mapped for schema-object kind {obj.GetType().Name}."),
    };
}
