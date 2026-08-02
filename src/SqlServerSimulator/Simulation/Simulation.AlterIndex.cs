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
    /// The <c>REBUILD</c> / <c>REORGANIZE</c> / <c>DISABLE</c> / <c>RESUME</c> /
    /// <c>PAUSE</c> / <c>ABORT</c> forms raise <see cref="NotSupportedException"/>
    /// naming the form: they're real features the simulator hasn't built, not
    /// rejections. The heap has no B-tree to rebuild and no disabled-index state.
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
            _ => throw new NotSupportedException(
                $"ALTER INDEX '{(alterAll ? "ALL" : indexName)}' supports the SET (…) / DISABLE / REBUILD forms; "
                + "REORGANIZE / RESUME / PAUSE / ABORT aren't modeled."),
        };

        bool? ignoreDupKey = null;
        if (form == AlterIndexForm.Set)
        {
            ignoreDupKey = ParseAlterIndexSetOptions(context);
        }
        else
        {
            // REBUILD takes an optional PARTITION = ALL and its own WITH (…)
            // option block; neither describes anything a heap has.
            context.MoveNextOptional();
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
            {
                context.MoveNextRequired();
                if (context.Token is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                context.MoveNextOptional();
            }
            _ = ParseOptionalIndexWithClause(context);
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterIndex(tableName.ToString());
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
                    ApplyToConstraint(table, constraint, form, ignoreDupKey);
                    RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), indexName!, "INDEX", table.Name, "TABLE");
                    return true;
                }
            }

            foreach (var index in table.Indexes)
            {
                if (collation.Equals(index.Name, indexName))
                {
                    ApplyToIndex(context, table, index, form, ignoreDupKey);
                    RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), indexName!, "INDEX", table.Name, "TABLE");
                    return true;
                }
            }

            throw SimulatedSqlException.CannotFindIndex(indexName!);
        }

        // ALL: constraints first, matching real's abort-on-first-refusal — a
        // constraint-backed index is present in every table that has a key, so
        // an IGNORE_DUP_KEY set over ALL raises Msg 1979 before touching
        // anything. Nothing is mutated before every target has been accepted.
        foreach (var constraint in table.KeyConstraints)
            ApplyToConstraint(table, constraint, form, ignoreDupKey);
        foreach (var index in table.Indexes)
            ApplyToIndex(context, table, index, form, ignoreDupKey);
        RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), table.Name, "INDEX", table.Name, "TABLE");
        return true;
    }

    private enum AlterIndexForm
    {
        Set,
        Disable,
        Rebuild,
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
        HeapTable table, KeyConstraint constraint, AlterIndexForm form, bool? ignoreDupKey)
    {
        switch (form)
        {
            case AlterIndexForm.Disable:
                constraint.IsDisabled = true;
                break;
            case AlterIndexForm.Rebuild:
                if (constraint.IsDisabled)
                    ValidateExistingRowsForKeyConstraint(table, constraint);
                constraint.IsDisabled = false;
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
            default:
                if (index.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(index.Name, table.Name);
                if (ignoreDupKey is not bool value)
                    return;
                if (!index.IsUnique)
                    throw SimulatedSqlException.IgnoreDupKeyOnNonUniqueIndexAlter(index.Name);
                if (index.Filter is not null)
                    throw SimulatedSqlException.IgnoreDupKeyOnFilteredIndex("alter", index.Name, table.Name);
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

    private static bool ReadOnOffOptionValue(ParserContext context, Token value) =>
        value is ReservedKeyword { Keyword: var keyword } && keyword is Keyword.On or Keyword.Off
            ? keyword == Keyword.On
            : throw SimulatedSqlException.SyntaxErrorNear(context);
}
