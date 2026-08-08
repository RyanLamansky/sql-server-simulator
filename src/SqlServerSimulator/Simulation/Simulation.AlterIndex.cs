using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private const string IgnoreDupKeyOption = "IGNORE_DUP_KEY";

    /// <summary>
    /// <c>ALTER INDEX … SET</c> options taking <c>ON</c> / <c>OFF</c>. Only
    /// <c>IGNORE_DUP_KEY</c> is acted on; the others are recognized so their
    /// names don't fall to Msg 155, then discarded.
    /// </summary>
    private static readonly FrozenSet<string> OnOffIndexOptions = new[]
    {
        "ALLOW_PAGE_LOCKS",
        "ALLOW_ROW_LOCKS",
        IgnoreDupKeyOption,
        "OPTIMIZE_FOR_SEQUENTIAL_KEY",
        "STATISTICS_NORECOMPUTE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>ALTER INDEX … SET</c> options taking a numeric value. Recognized and
    /// discarded — there is no B-tree for either to describe.
    /// </summary>
    private static readonly FrozenSet<string> NumericIndexOptions = new[]
    {
        "COMPRESSION_DELAY",
        "FILLFACTOR",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>ALTER INDEX { index_name | ALL } ON &lt;table&gt; SET ( option
    /// [, …] )</c>. Of the SET options only <c>IGNORE_DUP_KEY</c> carries a
    /// semantic (see <c>docs/claude/constraints.md</c>); the rest —
    /// <c>ALLOW_ROW_LOCKS</c> / <c>ALLOW_PAGE_LOCKS</c> /
    /// <c>STATISTICS_NORECOMPUTE</c> / <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c> /
    /// <c>COMPRESSION_DELAY</c> — are validated by name and discarded, since a
    /// heap-only store has nothing for them to change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The option list is validated strictly here, unlike CREATE INDEX's
    /// <c>WITH (…)</c> clause, which skips unrecognized options so that scripted
    /// DDL keeps flowing. That asymmetry follows real: an unknown name raises
    /// <b>Msg 155</b> at ALTER INDEX, and its <c>= value</c> must be
    /// <c>ON</c> / <c>OFF</c> (a numeric for <c>COMPRESSION_DELAY</c> /
    /// <c>FILLFACTOR</c>) or the statement is a syntax error.
    /// </para>
    /// <para>
    /// Setting <c>IGNORE_DUP_KEY</c> is narrower than declaring it, and each
    /// rejection was probe-confirmed against SQL Server 2025:
    /// a non-unique index raises <b>Msg 1915</b> (the CREATE path's equivalent is
    /// a different number, 1916, with different wording); a filtered index
    /// raises <b>Msg 10618</b> with the verb <c>alter</c> where CREATE says
    /// <c>create</c>; and the index backing a PRIMARY KEY / UNIQUE constraint
    /// raises <b>Msg 1979</b> — real accepts the option in such a constraint's
    /// own declaration but refuses to change it afterwards.
    /// <c>ALTER INDEX ALL</c> fans out over every index on the table and aborts
    /// on the first that refuses, so a table carrying a constraint-backed index
    /// can't have the option set table-wide.
    /// </para>
    /// <para>
    /// <c>REORGANIZE</c> has nothing to compact in a flat page list, so it
    /// validates and succeeds. Its own <c>WITH (…)</c> block takes
    /// <c>LOB_COMPACTION</c> and <c>COMPRESS_ALL_ROW_GROUPS</c> — real accepts
    /// the columnstore option on a rowstore index — and refuses anything else
    /// with a REORGANIZE-flavoured <b>Msg 155</b>, a non-<c>ON</c>/<c>OFF</c>
    /// value with <b>Msg 153</b>. A disabled index refuses REORGANIZE with
    /// <b>Msg 1973</b> where <c>ALL</c> skips over it, matching real.
    /// </para>
    /// <para>
    /// <c>RESUME</c> / <c>PAUSE</c> / <c>ABORT</c> address a paused resumable
    /// index build. The simulator never starts one — every index is built in
    /// place — so the whole model is real's own refusal: <b>Msg 10638</b> for a
    /// named index (State 1 for RESUME, 2 for PAUSE and ABORT) and
    /// <b>Msg 10680</b> at Level 11 for <c>ALL</c>, both raised after the table
    /// and index have resolved, and neither caring whether the index is
    /// disabled.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterIndex(ParserContext context)
    {
        // ALL is a reserved keyword; a named index is an ordinary identifier.
        var alterAll = context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All };
        string? indexName = null;
        if (!alterAll)
        {
            if (context.Token is not Name named)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            indexName = named.Value;
        }

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var tableName = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();

        var form = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Set } => AlterIndexForm.Set,
            UnquotedString { ContextualKeyword: ContextualKeyword.Disable } => AlterIndexForm.Disable,
            UnquotedString { ContextualKeyword: ContextualKeyword.Rebuild } => AlterIndexForm.Rebuild,
            UnquotedString { ContextualKeyword: ContextualKeyword.Reorganize } => AlterIndexForm.Reorganize,
            UnquotedString { ContextualKeyword: ContextualKeyword.Resume } => AlterIndexForm.Resume,
            UnquotedString { ContextualKeyword: ContextualKeyword.Pause } => AlterIndexForm.Pause,
            UnquotedString { ContextualKeyword: ContextualKeyword.Abort } => AlterIndexForm.Abort,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

        bool? ignoreDupKey = null;
        var namedPartition = false;
        switch (form)
        {
            case AlterIndexForm.Set:
                ignoreDupKey = ParseAlterIndexSetOptions(context);
                break;
            case AlterIndexForm.Pause:
            case AlterIndexForm.Abort:
                // Neither takes a PARTITION clause or an option block; real
                // reports the trailing WITH as Msg 319.
                context.MoveNextOptional();
                break;
            case AlterIndexForm.Resume:
                // RESUME's WITH (…) carries the resumption controls
                // (MAX_DURATION / MAXDOP / WAIT_AT_LOW_PRIORITY). There is
                // nothing to resume, so the block is validated by name and
                // discarded.
                context.MoveNextOptional();
                ParseOptionalResumeWithClause(context);
                break;
            case AlterIndexForm.Reorganize:
                context.MoveNextOptional();
                namedPartition = ParseOptionalIndexPartitionClause(context);
                ParseOptionalReorganizeWithClause(context);
                break;
            default:
                // REBUILD takes an optional PARTITION = ALL and its own WITH (…)
                // option block; neither describes anything a heap has.
                context.MoveNextOptional();
                namedPartition = ParseOptionalIndexPartitionClause(context);
                _ = ParseOptionalIndexWithClause(context);
                break;
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterIndex(tableName.ToString());
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        // ALTER INDEX is gated on ALTER of the parent table — the same Msg 1088
        // state 9 a missing table earns (probe-confirmed).
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForAlterIndex(tableName.ToString());

        // A named index has to resolve against the table's own indexes or its
        // key constraints — a constraint name is a legal ALTER INDEX target,
        // which is how Msg 1979 becomes reachable.
        var collation = context.Batch.CurrentDatabase.Collation;
        if (!alterAll)
        {
            foreach (var constraint in table.KeyConstraints)
            {
                if (collation.Equals(constraint.Name, indexName))
                {
                    RejectNamedIndexTarget(form, namedPartition, constraint.Name, table.Name);
                    ApplyToConstraint(table, constraint, form, ignoreDupKey, context.Batch);
                    RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), indexName!, "INDEX", table.Name, "TABLE");
                    return true;
                }
            }

            foreach (var index in table.Indexes)
            {
                if (collation.Equals(index.Name, indexName))
                {
                    RejectNamedIndexTarget(form, namedPartition, index.Name, table.Name);
                    ApplyToIndex(context, table, index, form, ignoreDupKey);
                    RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), indexName!, "INDEX", table.Name, "TABLE");
                    return true;
                }
            }

            throw SimulatedSqlException.CannotFindIndex(indexName!);
        }

        // ALL: the resumable forms never look at an individual index — real
        // raises its own ALL-flavoured refusal even for a table carrying no
        // index at all (probe-confirmed on a bare heap).
        if (form is AlterIndexForm.Resume or AlterIndexForm.Pause or AlterIndexForm.Abort)
            throw SimulatedSqlException.NoPendingResumableIndexOperationForAll(FormName(form), table.Name);
        if (namedPartition)
        {
            // Real names the first index the statement would have touched —
            // index_id order, so a constraint's clustered index first — and
            // falls back to the table when there is none.
            var firstTarget = table.KeyConstraints.Count > 0 ? table.KeyConstraints[0].Name
                : table.Indexes.Count > 0 ? table.Indexes[0].Name
                : null;
            throw SimulatedSqlException.RebuildPartitionOnUnpartitioned(alterIndex: true, firstTarget, table.Name);
        }

        // ALL: constraints first, matching real's abort-on-first-refusal — a
        // constraint-backed index is present in every table that has a key, so
        // an IGNORE_DUP_KEY set over ALL raises Msg 1979 before touching
        // anything. Nothing is mutated before every target has been accepted.
        foreach (var constraint in table.KeyConstraints)
        {
            // REORGANIZE over ALL steps past a disabled index where naming it
            // would be Msg 1973 (probe-confirmed).
            if (form == AlterIndexForm.Reorganize && constraint.IsDisabled)
                continue;
            ApplyToConstraint(table, constraint, form, ignoreDupKey, context.Batch);
        }
        foreach (var index in table.Indexes)
        {
            if (form == AlterIndexForm.Reorganize && index.IsDisabled)
                continue;
            ApplyToIndex(context, table, index, form, ignoreDupKey);
        }
        RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), table.Name, "INDEX", table.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// Raises the refusals a named <c>ALTER INDEX</c> target earns once it has
    /// resolved: the partition clause on an unpartitioned index (Msg 7729) and
    /// the resumable forms' Msg 10638.
    /// </summary>
    private static void RejectNamedIndexTarget(AlterIndexForm form, bool namedPartition, string indexName, string tableName)
    {
        if (namedPartition)
            throw SimulatedSqlException.PartitionNumberOnUnpartitionedIndex(indexName);
        if (form is AlterIndexForm.Resume or AlterIndexForm.Pause or AlterIndexForm.Abort)
            throw SimulatedSqlException.NoPendingResumableIndexOperation(FormName(form), indexName, tableName);
    }

    private static string FormName(AlterIndexForm form) => form switch
    {
        AlterIndexForm.Abort => "ABORT",
        AlterIndexForm.Pause => "PAUSE",
        _ => "RESUME",
    };

    private enum AlterIndexForm
    {
        Set,
        Disable,
        Rebuild,
        Reorganize,
        Resume,
        Pause,
        Abort,
    }

    /// <summary>
    /// Applies the form to a PRIMARY KEY / UNIQUE constraint's backing index.
    /// DISABLE and REBUILD are allowed here — real permits taking a constraint's
    /// index out of service, and while it's out the constraint isn't enforced at
    /// all (probe-confirmed) — but changing <c>IGNORE_DUP_KEY</c> is not
    /// (Msg 1979). A SET that doesn't mention that option is a no-op rather than
    /// an error, so <c>ALTER INDEX ALL … SET (ALLOW_ROW_LOCKS = ON)</c> still
    /// succeeds on a table carrying a PRIMARY KEY.
    /// </summary>
    private static void ApplyToConstraint(
        HeapTable table, KeyConstraint constraint, AlterIndexForm form, bool? ignoreDupKey, BatchContext batch)
    {
        switch (form)
        {
            case AlterIndexForm.Disable:
                constraint.IsDisabled = true;
                break;
            case AlterIndexForm.Rebuild:
                if (constraint.IsDisabled)
                    ValidateExistingRowsForKeyConstraint(table, constraint, batch);
                constraint.IsDisabled = false;
                break;
            case AlterIndexForm.Reorganize:
                // Nothing to compact in a flat page list, but a disabled index
                // still refuses the operation.
                if (constraint.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(constraint.Name, table.Name);
                break;
            default:
                if (constraint.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(constraint.Name, table.Name);
                if (ignoreDupKey is not null)
                    throw SimulatedSqlException.IgnoreDupKeyOnConstraintIndex(constraint.Name);
                break;
        }
    }

    private static void ApplyToIndex(
        ParserContext context, HeapTable table, Storage.Index index, AlterIndexForm form, bool? ignoreDupKey)
    {
        switch (form)
        {
            case AlterIndexForm.Disable:
                index.IsDisabled = true;
                break;
            case AlterIndexForm.Rebuild:
                // Rows that accumulated while the index was out of service are
                // re-validated on the way back in, exactly as a fresh CREATE
                // UNIQUE INDEX would be: Msg 1505 on a duplicate. A REBUILD of an
                // index that was never disabled is a no-op success.
                if (index.IsDisabled && index.IsUnique)
                {
                    ValidateExistingRowsForUniqueIndex(
                        table, index, context.Batch, $"{Database.DefaultSchemaName}.{table.Name}");
                }
                index.IsDisabled = false;
                break;
            case AlterIndexForm.Reorganize:
                if (index.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(index.Name, table.Name);
                break;
            default:
                if (index.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(index.Name, table.Name);
                if (ignoreDupKey is not bool value)
                    return;
                if (!index.IsUnique)
                    throw SimulatedSqlException.IgnoreDupKeyOnNonUniqueIndexAlter(index.Name);
                if (index.Filter is not null)
                    throw SimulatedSqlException.IgnoreDupKeyOnFilteredIndex("alter", index.Name, SchemaQualifyTableName(table, context.CurrentDatabase));
                index.IgnoreDupKey = value;
                break;
        }
    }

    /// <summary>
    /// Parses the <c>SET ( option = value [, …] )</c> list, returning the
    /// <c>IGNORE_DUP_KEY</c> setting when the list carried one and
    /// <see langword="null"/> when it didn't — the distinction matters, because
    /// only a list that mentions the option can raise the constraint / non-unique
    /// / filtered rejections. Every other recognized option is discarded.
    /// Cursor on entry: the <c>SET</c> keyword. On exit: first token past the
    /// closing <c>)</c>.
    /// </summary>
    private static bool? ParseAlterIndexSetOptions(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        bool? ignoreDupKey = null;
        while (true)
        {
            // An empty list is a syntax error on real, so a name is required.
            // Read it off the raw source rather than as an identifier: FILLFACTOR
            // is a reserved keyword, so it arrives as a ReservedKeyword while
            // every other option name is an ordinary unquoted identifier.
            if (context.GetNextRequired() is not (StringToken or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var optionName = context.Token!.Source.ToString();
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var value = context.GetNextRequired();
            if (OnOffIndexOptions.Contains(optionName))
            {
                var on = ReadOnOffOptionValue(context, value);
                if (IgnoreDupKeyOption.Equals(optionName, StringComparison.OrdinalIgnoreCase))
                    ignoreDupKey = on;
            }
            else if (NumericIndexOptions.Contains(optionName))
            {
                if (value is not Numeric)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            else
            {
                throw SimulatedSqlException.UnrecognizedAlterIndexOption(optionName);
            }

            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return ignoreDupKey;
    }

    /// <summary>
    /// <c>ALTER INDEX … REORGANIZE WITH (…)</c> options. Both take
    /// <c>ON</c> / <c>OFF</c>; real accepts the columnstore-shaped
    /// <c>COMPRESS_ALL_ROW_GROUPS</c> on a rowstore index without complaint
    /// (probe-confirmed), so neither name is gated on the index kind.
    /// </summary>
    private static readonly FrozenSet<string> ReorganizeOptions = new[]
    {
        "COMPRESS_ALL_ROW_GROUPS",
        "LOB_COMPACTION",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>ALTER INDEX … RESUME WITH (…)</c> options. Recognized by name and
    /// discarded — there is never an operation to resume, so nothing reads
    /// them.
    /// </summary>
    private static readonly FrozenSet<string> ResumeOptions = new[]
    {
        "MAXDOP",
        "MAX_DURATION",
        "WAIT_AT_LOW_PRIORITY",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the optional <c>PARTITION = { ALL | &lt;number&gt; }</c> clause
    /// shared by <c>REBUILD</c> and <c>REORGANIZE</c>, returning
    /// <see langword="true"/> when a partition <i>number</i> was named — which
    /// nothing here is partitioned enough to satisfy, so the caller raises
    /// real's refusal once it knows what to name. Cursor on entry: the first
    /// token past the form keyword. On exit: the first token past the clause.
    /// </summary>
    private static bool ParseOptionalIndexPartitionClause(ParserContext context)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
            return false;
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var named = context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.All };
        if (named && context.Token is not Numeric)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return named;
    }

    /// <summary>
    /// Parses <c>REORGANIZE</c>'s own <c>WITH ( option = ON | OFF [, …] )</c>
    /// block. Unlike <c>SET</c>'s, an unrecognized name here reports the
    /// REORGANIZE-flavoured Msg 155 and a non-<c>ON</c>/<c>OFF</c> value reports
    /// Msg 153 rather than a syntax error.
    /// </summary>
    private static void ParseOptionalReorganizeWithClause(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        while (true)
        {
            if (context.GetNextRequired() is not (StringToken or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var optionName = context.Token.Source.ToString();
            if (!ReorganizeOptions.Contains(optionName))
                throw SimulatedSqlException.UnrecognizedAlterIndexReorganizeOption(optionName);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off })
                throw SimulatedSqlException.InvalidUsageOfIndexOption(optionName);

            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
    }

    /// <summary>
    /// Parses <c>RESUME</c>'s <c>WITH ( … )</c> block. Each option's value
    /// grammar differs (<c>MAX_DURATION = &lt;n&gt; [MINUTES]</c>,
    /// <c>MAXDOP = &lt;n&gt;</c>, <c>WAIT_AT_LOW_PRIORITY ( … )</c>), so the
    /// names are validated and the values skipped to the matching close paren.
    /// </summary>
    private static void ParseOptionalResumeWithClause(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var depth = 1;
        var expectingName = true;
        while (depth > 0)
        {
            var token = context.GetNextRequired();
            if (expectingName)
            {
                if (token is not (StringToken or ReservedKeyword))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (!ResumeOptions.Contains(token.Source.ToString()))
                    throw SimulatedSqlException.UnrecognizedAlterIndexOption(token.Source.ToString());
                expectingName = false;
                continue;
            }

            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ',' } when depth == 1:
                    expectingName = true;
                    break;
            }
        }

        context.MoveNextOptional();
    }

    private static bool ReadOnOffOptionValue(ParserContext context, Token value) =>
        value is ReservedKeyword { Keyword: var keyword } && keyword is Keyword.On or Keyword.Off
            ? keyword == Keyword.On
            : throw SimulatedSqlException.SyntaxErrorNear(context);
}
