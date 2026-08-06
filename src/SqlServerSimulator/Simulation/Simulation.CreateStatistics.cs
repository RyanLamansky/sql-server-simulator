using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE STATISTICS name ON table (column [, …]) [WITH option [, …]]</c>.
    /// Entered with the cursor on the <c>STATISTICS</c> keyword.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The statistic is recorded on the table and surfaces through
    /// <c>sys.stats</c> / <c>sys.stats_columns</c> with
    /// <c>user_created = 1</c>; nothing about query execution reads it — see
    /// <see cref="UserStatistic"/> for why the declaration alone is the
    /// modeled part.
    /// </para>
    /// <para>
    /// Diagnostics follow real's, probe-confirmed against SQL Server 2025:
    /// <strong>Msg 1088</strong> for a missing table (shared with CREATE
    /// INDEX, at its own state 12), <strong>Msg 1911</strong> for a missing
    /// column, and <strong>Msg 1927</strong> for a name the table already
    /// carries — which includes an <em>index</em>'s name, since statistics and
    /// indexes share one per-table name space.
    /// </para>
    /// <para>
    /// Of the WITH options only <c>NORECOMPUTE</c> has an observable effect
    /// (<c>sys.stats.no_recompute</c>); the sampling family (<c>FULLSCAN</c>,
    /// <c>SAMPLE n {PERCENT | ROWS}</c>, <c>PERSIST_SAMPLE_PERCENT</c>,
    /// <c>INCREMENTAL</c>, <c>MAXDOP</c>, <c>AUTO_DROP</c>) describes how real
    /// would scan the data to build a histogram there isn't one of here, so
    /// those parse and discard.
    /// </para>
    /// </remarks>
    internal static bool TryParseCreateStatistics(ParserContext context)
    {
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var statisticsName = nameToken.Value;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var targetTableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var columnNames = new List<string>();
        do
        {
            if (context.GetNextRequired() is not Name column)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            columnNames.Add(column.Value);
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var noRecompute = ParseStatisticsOptions(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(targetTableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        table.OwningDatabase?.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        var collation = context.Batch.CurrentDatabase.Collation;
        foreach (var existing in table.UserStatistics)
        {
            if (collation.Equals(existing.Name, statisticsName))
                throw SimulatedSqlException.StatisticsAlreadyExist(table.Name, statisticsName);
        }
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Name is { } indexName && collation.Equals(indexName, statisticsName))
                throw SimulatedSqlException.StatisticsAlreadyExist(table.Name, statisticsName);
        }

        var ordinals = new int[columnNames.Count];
        for (var i = 0; i < columnNames.Count; i++)
        {
            var ordinal = -1;
            for (var c = 0; c < table.Columns.Length; c++)
            {
                if (collation.Equals(table.Columns[c].Name, columnNames[i]))
                {
                    ordinal = c;
                    break;
                }
            }
            if (ordinal < 0)
                throw SimulatedSqlException.IndexColumnMissing(columnNames[i]);
            ordinals[i] = ordinal;
        }

        table.UserStatistics.Add(new UserStatistic(
            statisticsName,
            NextStatisticsId(table),
            ordinals,
            noRecompute,
            context.Batch.CurrentStatement.UtcNow));
        RecordDdlEvent(context, "CREATE_STATISTICS", EventSchemaName(targetTableName), statisticsName, "STATISTICS", table.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// Parses <c>DROP STATISTICS table.name [, …]</c>. Entered with the cursor
    /// on the <c>STATISTICS</c> keyword. Each entry is a dotted name whose leaf
    /// is the statistic and whose remaining segments address the table, so the
    /// form is 2-part (<c>t.s</c>), 3-part (<c>dbo.t.s</c>) or 4-part with the
    /// database. <strong>Msg 3701</strong> names the whole written form when
    /// nothing matches.
    /// </summary>
    internal static bool TryParseDropStatistics(ParserContext context)
    {
        var pending = new List<MultiPartName>();
        do
        {
            context.MoveNextRequired();
            pending.Add(BatchContext.ParseObjectName(context));
            context.MoveNextOptional();
        } while (context.Token is Operator { Character: ',' });

        if (context.Batch.IsSkipping)
            return true;

        foreach (var written in pending)
        {
            if (written.Count < 2)
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());
            var tableName = QualifierOf(written);
            if (!context.Batch.TryResolveTable(tableName, out var table))
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());

            table.OwningDatabase?.RejectWriteWhenReadOnly();
            if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());

            var collation = context.Batch.CurrentDatabase.Collation;
            var index = table.UserStatistics.FindIndex(s => collation.Equals(s.Name, written.Leaf));
            if (index < 0)
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());
            table.UserStatistics.RemoveAt(index);
            RecordDdlEvent(context, "DROP_STATISTICS", EventSchemaName(tableName), written.Leaf, "STATISTICS", table.Name, "TABLE");
        }
        return true;
    }

    /// <summary>
    /// The written name minus its leaf — for <c>DROP STATISTICS</c>, where the
    /// leaf is the statistic and everything before it addresses the table.
    /// </summary>
    private static MultiPartName QualifierOf(MultiPartName written)
    {
        var qualifier = new MultiPartName(written[0]);
        for (var i = 1; i < written.Count - 1; i++)
            qualifier = qualifier.WithAddedPart(written[i]);
        return qualifier;
    }

    /// <summary>
    /// The next free per-table stats id: one past the highest index id and the
    /// highest statistic already recorded. Index-backed statistics share their
    /// index's id, so the two draw from one sequence.
    /// </summary>
    private static int NextStatisticsId(HeapTable table)
    {
        var highest = 0;
        foreach (var identity in table.IndexIdentities())
            highest = Math.Max(highest, identity.IndexId);
        foreach (var statistic in table.UserStatistics)
            highest = Math.Max(highest, statistic.StatsId);
        return highest + 1;
    }

    /// <summary>
    /// Consumes the optional <c>WITH</c> option list, returning whether
    /// <c>NORECOMPUTE</c> appeared. Everything else is accepted and discarded.
    /// </summary>
    private static bool ParseStatisticsOptions(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return false;

        var noRecompute = false;
        var depth = 0;
        context.MoveNextRequired();
        while (context.Token is { } token)
        {
            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                // A comma at the top of the option list separates options; one
                // inside a parenthesized option value belongs to that value.
                case Operator { Character: ',' } when depth == 0:
                    break;
                case Name option when option.Value.Equals("NORECOMPUTE", StringComparison.OrdinalIgnoreCase):
                    noRecompute = true;
                    break;
                case Operator or Name or Numeric or Literal:
                    break;
                default:
                    // A reserved keyword ends the option list — the statement
                    // is over and the next one begins.
                    return noRecompute;
            }
            context.MoveNextOptional();
            if (context.Token is null)
                break;
        }
        return noRecompute;
    }
}
