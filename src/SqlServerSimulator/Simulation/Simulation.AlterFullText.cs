using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>ALTER FULLTEXT {INDEX ON table | CATALOG name} …</c>. Cursor
    /// enters on <c>FULLTEXT</c>; caller has matched <c>ALTER</c>.
    /// </summary>
    private static bool TryParseAlterFullText(ParserContext context)
    {
        context.MoveNextRequired();
        return context.Token switch
        {
            UnquotedString { Value: var w } when w.Equals("CATALOG", StringComparison.OrdinalIgnoreCase)
                => ParseAlterFullTextCatalog(context),
            ReservedKeyword { Keyword: Keyword.Index }
                => ParseAlterFullTextIndex(context),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    private enum AlterFullTextIndexAction
    {
        Enable,
        Disable,
        SetChangeTracking,
        SetStoplist,
        SetSearchPropertyList,
        Add,
        Drop,
        AlterColumn,
        StartPopulation,
        StartUpdatePopulation,
        StopPopulation,
        PausePopulation,
        ResumePopulation,
    }

    /// <summary>
    /// Parses <c>ALTER FULLTEXT INDEX ON table</c> and one action: <c>ENABLE</c>
    /// / <c>DISABLE</c>; <c>SET CHANGE_TRACKING</c>, <c>SET STOPLIST</c> or
    /// <c>SET SEARCH PROPERTY LIST</c>; <c>ADD (columns)</c>, <c>DROP
    /// (columns)</c> or <c>ALTER COLUMN c {ADD | DROP} STATISTICAL_SEMANTICS</c>;
    /// <c>START {FULL | INCREMENTAL | UPDATE} POPULATION</c> or <c>{STOP |
    /// PAUSE | RESUME} POPULATION</c>. Searches read the live rows, so the
    /// population verbs change nothing here; each answers with the warning or
    /// refusal real gives an index whose population has completed, which
    /// depends on the change-tracking mode (probed 2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    private static bool ParseAlterFullTextIndex(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var tableName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();

        AlterFullTextIndexAction action;
        var changeTracking = FullTextChangeTracking.Auto;
        var stoplistOff = false;
        string? namedList = null;
        List<FullTextColumnSpec> addSpecs = [];
        List<string> dropNames = [];
        var addSemantics = false;
        var noPopulation = false;
        switch (context.Token)
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Enable }:
                action = AlterFullTextIndexAction.Enable;
                context.MoveNextOptional();
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Disable }:
                action = AlterFullTextIndexAction.Disable;
                context.MoveNextOptional();
                break;
            case ReservedKeyword { Keyword: Keyword.Set }:
                context.MoveNextRequired();
                if (IsFullTextWord(context.Token, "CHANGE_TRACKING"))
                {
                    action = AlterFullTextIndexAction.SetChangeTracking;
                    changeTracking = ParseChangeTrackingOption(context, allowNoPopulation: false);
                }
                else if (IsFullTextWord(context.Token, "STOPLIST"))
                {
                    action = AlterFullTextIndexAction.SetStoplist;
                    (stoplistOff, namedList) = ParseStoplistValue(context);
                    noPopulation = ParseWithNoPopulation(context);
                }
                else if (IsFullTextWord(context.Token, "SEARCH"))
                {
                    action = AlterFullTextIndexAction.SetSearchPropertyList;
                    namedList = ParseSearchPropertyListValue(context);
                    noPopulation = ParseWithNoPopulation(context);
                }
                else
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                break;
            case ReservedKeyword { Keyword: Keyword.Add }:
                action = AlterFullTextIndexAction.Add;
                context.MoveNextRequired();
                addSpecs = ParseFullTextColumnList(context);
                noPopulation = ParseWithNoPopulation(context);
                break;
            case ReservedKeyword { Keyword: Keyword.Drop }:
                action = AlterFullTextIndexAction.Drop;
                if (context.GetNextRequired() is not Operator { Character: '(' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                do
                {
                    if (context.GetNextRequired() is not Name dropToken)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    dropNames.Add(dropToken.Value);
                }
                while (context.GetNextRequired() is Operator { Character: ',' });
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
                noPopulation = ParseWithNoPopulation(context);
                break;
            case ReservedKeyword { Keyword: Keyword.Alter }:
                action = AlterFullTextIndexAction.AlterColumn;
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Column }
                    || context.GetNextRequired() is not Name alteredToken)
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                dropNames.Add(alteredToken.Value);
                addSemantics = context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.Add } => true,
                    ReservedKeyword { Keyword: Keyword.Drop } => false,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                if (!IsFullTextWord(context.GetNextRequired(), "STATISTICAL_SEMANTICS"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
                noPopulation = ParseWithNoPopulation(context);
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Start }:
                action = context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.Full } => AlterFullTextIndexAction.StartPopulation,
                    ReservedKeyword { Keyword: Keyword.Update } => AlterFullTextIndexAction.StartUpdatePopulation,
                    Name incremental when IsFullTextWord(incremental, "INCREMENTAL") => AlterFullTextIndexAction.StartPopulation,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                ExpectPopulationWord(context);
                break;
            case Name verb when IsFullTextWord(verb, "STOP"):
                action = AlterFullTextIndexAction.StopPopulation;
                ExpectPopulationWord(context);
                break;
            case Name verb when IsFullTextWord(verb, "PAUSE"):
                action = AlterFullTextIndexAction.PausePopulation;
                ExpectPopulationWord(context);
                break;
            case Name verb when IsFullTextWord(verb, "RESUME"):
                action = AlterFullTextIndexAction.ResumePopulation;
                ExpectPopulationWord(context);
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (context.Batch.IsSkipping)
            return true;

        RejectFullTextDdlInTransaction(context, "ALTER FULLTEXT INDEX");
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableName, state: 52);
        if (table.FullTextIndex is not { } index)
            throw SimulatedSqlException.FullTextIndexMissing(tableName.Leaf, state: 2);

        var batch = context.Batch;
        var messages = context.Connection.PendingMessages;
        var tracking = index.ChangeTracking;
        switch (action)
        {
            case AlterFullTextIndexAction.Enable:
                if (index.Columns.Count == 0)
                    throw SimulatedSqlException.FullTextIndexHasNoColumns(table.Name);
                index.IsEnabled = true;
                break;
            case AlterFullTextIndexAction.Disable:
                index.IsEnabled = false;
                break;
            case AlterFullTextIndexAction.SetChangeTracking:
                if (changeTracking == FullTextChangeTracking.Off)
                {
                    messages.Enqueue(tracking == FullTextChangeTracking.Off
                        ? SimulatedSqlException.FullTextChangeTrackingAlreadyOffMessage(batch, table.Name)
                        : SimulatedSqlException.FullTextChangesDeletedMessage(batch, table.Name));
                }
                else if (changeTracking == tracking)
                {
                    messages.Enqueue(tracking == FullTextChangeTracking.Auto
                        ? SimulatedSqlException.FullTextAutoPropagationAlreadyOnMessage(batch, table.Name)
                        : SimulatedSqlException.FullTextChangeTrackingAlreadyOnMessage(batch, table.Name));
                }
                index.ChangeTracking = changeTracking;
                break;
            case AlterFullTextIndexAction.SetStoplist:
                if (namedList is not null)
                    throw SimulatedSqlException.FullTextStoplistNotFound(namedList, state: 3);
                if (noPopulation && stoplistOff != index.StoplistOff)
                    messages.Enqueue(SimulatedSqlException.FullTextStoplistNoPopulationMessage(batch));
                index.StoplistOff = stoplistOff;
                break;
            case AlterFullTextIndexAction.SetSearchPropertyList:
                if (namedList is not null)
                    throw SimulatedSqlException.SearchPropertyListNotFound(namedList, state: 3);
                break;
            case AlterFullTextIndexAction.Add:
                if (noPopulation && tracking != FullTextChangeTracking.Off)
                    throw SimulatedSqlException.FullTextNoPopulationWithChangeTracking();
                var added = ResolveFullTextColumns(context, table, addSpecs, index.Columns, missingState: 4);
                if (index.Columns.Count == 0)
                    index.IsEnabled = true;
                index.Columns.AddRange(added);
                break;
            case AlterFullTextIndexAction.Drop:
                if (noPopulation && tracking != FullTextChangeTracking.Off)
                    throw SimulatedSqlException.FullTextNoPopulationWithChangeTracking();
                var dropped = IndexedColumnIds(context, table, index, dropNames);
                _ = index.Columns.RemoveAll(c => dropped.Contains(c.ColumnId));
                if (index.Columns.Count == 0)
                    index.IsEnabled = false;
                break;
            case AlterFullTextIndexAction.AlterColumn:
                _ = IndexedColumnIds(context, table, index, dropNames);
                if (addSemantics)
                    throw SimulatedSqlException.SemanticDatabaseNotRegistered();
                break;
            case AlterFullTextIndexAction.StartPopulation:
                if (tracking == FullTextChangeTracking.Auto)
                    messages.Enqueue(SimulatedSqlException.FullTextPopulationActiveMessage(batch, table.Name));
                break;
            case AlterFullTextIndexAction.StartUpdatePopulation:
                if (tracking == FullTextChangeTracking.Off)
                    throw SimulatedSqlException.FullTextChangeTrackingNotStarted(table.Name);
                break;
            case AlterFullTextIndexAction.StopPopulation:
                if (tracking == FullTextChangeTracking.Auto)
                    messages.Enqueue(SimulatedSqlException.FullTextStopIgnoredMessage(batch));
                break;
            case AlterFullTextIndexAction.PausePopulation:
                messages.Enqueue(SimulatedSqlException.FullTextPauseIgnoredMessage(batch, tracking == FullTextChangeTracking.Auto ? (byte)1 : (byte)2));
                break;
            case AlterFullTextIndexAction.ResumePopulation:
                if (tracking != FullTextChangeTracking.Auto)
                    messages.Enqueue(SimulatedSqlException.FullTextResumeIgnoredMessage(batch));
                break;
        }
        return true;
    }

    /// <summary>
    /// The column ids <paramref name="names"/> resolve to, each of which must
    /// be one the index covers: a missing column is Msg 1911 at state 5, an
    /// unindexed one Msg 7677.
    /// </summary>
    private static HashSet<int> IndexedColumnIds(ParserContext context, HeapTable table, FullTextIndex index, List<string> names)
    {
        var ids = new HashSet<int>();
        foreach (var name in names)
        {
            var ordinal = ResolveColumnOrdinalForFullText(context.Batch.CurrentDatabase.Collation, table, name, missingState: 5);
            if (!index.Columns.Exists(c => c.ColumnId == ordinal))
                throw SimulatedSqlException.FullTextDdlColumnNotIndexed(table.Columns[ordinal - 1].Name);
            _ = ids.Add(ordinal);
        }
        return ids;
    }

    /// <summary>Consumes an optional trailing <c>WITH NO POPULATION</c>, answering whether it was there.</summary>
    private static bool ParseWithNoPopulation(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return false;
        if (!IsFullTextWord(context.GetNextRequired(), "NO") || !IsFullTextWord(context.GetNextRequired(), "POPULATION"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return true;
    }

    private static void ExpectPopulationWord(ParserContext context)
    {
        if (!IsFullTextWord(context.GetNextRequired(), "POPULATION"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
    }

    /// <summary>
    /// Parses <c>ALTER FULLTEXT CATALOG name {REBUILD [WITH ACCENT_SENSITIVITY
    /// = {ON | OFF}] | REORGANIZE | AS DEFAULT}</c>. <c>REORGANIZE</c> and a
    /// plain <c>REBUILD</c> have nothing to merge or re-crawl here; the
    /// accent option and <c>AS DEFAULT</c> change the catalog.
    /// </summary>
    private static bool ParseAlterFullTextCatalog(ParserContext context)
    {
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;

        bool? accentSensitive = null;
        var asDefault = false;
        switch (context.GetNextRequired())
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Rebuild }:
                context.MoveNextOptional();
                if (context.Token is ReservedKeyword { Keyword: Keyword.With })
                {
                    if (!IsFullTextWord(context.GetNextRequired(), "ACCENT_SENSITIVITY")
                        || context.GetNextRequired() is not Operator { Character: '=' })
                    {
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    accentSensitive = context.GetNextRequired() switch
                    {
                        ReservedKeyword { Keyword: Keyword.On } => true,
                        ReservedKeyword { Keyword: Keyword.Off } => false,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    context.MoveNextOptional();
                }
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Reorganize }:
                context.MoveNextOptional();
                break;
            case ReservedKeyword { Keyword: Keyword.As }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Default })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                asDefault = true;
                context.MoveNextOptional();
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (context.Batch.IsSkipping)
            return true;

        RejectFullTextDdlInTransaction(context, "ALTER FULLTEXT CATALOG");
        var database = context.CurrentDatabase;
        if (!database.FullTextCatalogs.TryGetValue(name, out var catalog)
            || !PermissionEnforcement.HasDatabasePermission(context.Batch, database, Permission.AlterAnyFullTextCatalog))
        {
            throw SimulatedSqlException.FullTextCatalogNotFoundOrDenied(name, database.Name, state: 2);
        }

        if (asDefault)
        {
            foreach (var other in database.FullTextCatalogs.Values)
                other.IsDefault = false;
            catalog.IsDefault = true;
        }
        if (accentSensitive is { } sensitive && sensitive != catalog.IsAccentSensitive)
        {
            // A search binds the accent fold when it compiles; a cached plan
            // must see the new one.
            catalog.IsAccentSensitive = sensitive;
            context.Connection.Simulation.BumpSchemaVersion();
        }
        return true;
    }
}
