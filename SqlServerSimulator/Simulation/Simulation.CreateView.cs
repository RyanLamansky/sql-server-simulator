using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE VIEW schema.name [(col_list)] [WITH SCHEMABINDING |
    /// ENCRYPTION | VIEW_METADATA] AS &lt;SELECT&gt; [WITH CHECK OPTION]</c>
    /// and stores a <see cref="View"/> in the target
    /// <see cref="Schema.Views"/> dict. The body source is captured by
    /// running <see cref="Selection.Parse"/> once at CREATE time to derive
    /// the output column schema and to measure the body span — the cursor
    /// is past the last body token when Parse returns, so the span is
    /// <c>[bodyStartIndex, cursorIndex)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>WITH-clause options</strong>: <c>SCHEMABINDING</c>,
    /// <c>ENCRYPTION</c>, and <c>VIEW_METADATA</c> parse-and-ignore. No
    /// dependency tracking for SCHEMABINDING (so DROP TABLE on a referenced
    /// table succeeds and the view later fails at call time);
    /// <c>VIEW_DEFINITION</c> in INFORMATION_SCHEMA.VIEWS still surfaces
    /// the body text for ENCRYPTION views (real SQL Server returns NULL —
    /// minor fidelity gap).
    /// </para>
    /// <para>
    /// <strong>Trailing <c>WITH CHECK OPTION</c></strong>: parsed and
    /// recorded on <see cref="View.WithCheckOption"/>; not enforced because
    /// v1 doesn't support DML through views.
    /// </para>
    /// <para>
    /// <strong>Probe-confirmed errors</strong> (SQL Server 2025, 2026-05-12):
    /// </para>
    /// <list type="bullet">
    /// <item>Unnamed projection → <strong>Msg 4511</strong> (distinct from
    /// inline TVF's Msg 4514 and SELECT INTO's Msg 1038).</item>
    /// <item>Duplicate column name → <strong>Msg 4506</strong> (shared with
    /// inline TVFs).</item>
    /// <item>Column-list count mismatch — too few listed → <strong>Msg
    /// 8158</strong>; too many listed → <strong>Msg 8159</strong>.</item>
    /// <item>Self-recursion → Msg 208 from the body's parse against the
    /// not-yet-registered view name. Matches real SQL Server's rejection
    /// (different error path; same end state).</item>
    /// </list>
    /// </remarks>
    private static bool TryParseCreateView(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var viewName = BatchContext.ParseObjectName(context);
        if (!context.Batch.TryResolveSchema(viewName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(viewName.ImmediateQualifier ?? Database.DefaultSchemaName);

        context.MoveNextRequired();

        // Optional column rename list: `(a, b, c)`.
        List<string>? renameList = null;
        if (context.Token is Operator { Character: '(' })
        {
            renameList = [];
            context.MoveNextRequired();
            while (true)
            {
                if (context.Token is not Name columnName)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                renameList.Add(columnName.Value);
                context.MoveNextRequired();
                if (context.Token is Operator { Character: ')' })
                {
                    context.MoveNextRequired();
                    break;
                }
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
        }

        // Optional WITH-clause: SCHEMABINDING / ENCRYPTION / VIEW_METADATA
        // (parse-and-ignore). Other options raise NotSupportedException.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            ParseViewOptions(context);
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Parse the body's SELECT once to derive output schema and locate
        // the body's end. Selection.Parse leaves the cursor at the first
        // un-consumed token after the SELECT — typically the next
        // statement-starting keyword OR the trailing WITH CHECK OPTION.
        context.MoveNextRequired();
        var commandText = context.Command.CommandText;
        var bodyStart = context.Token?.StartIndex
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        var bodySelection = Selection.Parse(context, depth: 0);
        var bodyEnd = context.Token?.StartIndex ?? commandText.Length;
        var bodyText = commandText[bodyStart..bodyEnd];

        // Msg 1033: a view body's ORDER BY requires a companion TOP / OFFSET /
        // FETCH. Same wording the existing CTE-body check raises (probe-
        // confirmed identical text — the message lists "views, inline
        // functions, derived tables, subqueries, and common table
        // expressions" as the universe).
        if (bodySelection.HasOrderBy && !bodySelection.HasTopOrOffsetOrFetch)
            throw SimulatedSqlException.OrderByInvalidInCte();

        // Optional trailing WITH CHECK OPTION. Cursor on entry: the post-
        // body token, possibly `WITH`. Cursor on exit: post-OPTION or
        // unchanged (when the WITH isn't followed by CHECK OPTION — e.g.
        // a follow-up CTE-prefixed statement).
        var withCheckOption = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextOptional();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Check }
                && context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Option })
            {
                withCheckOption = true;
                context.MoveNextOptional();
            }
            else
            {
                context.RestoreCheckpoint(checkpoint);
            }
        }

        if (context.Batch.IsSkipping)
            return true;

        // Reject collisions across the view / table / function name namespace.
        if (schema.Views.ContainsKey(viewName.Leaf)
            || schema.Functions.ContainsKey(viewName.Leaf)
            || schema.HeapTables.ContainsKey(viewName.Leaf))
        {
            throw SimulatedSqlException.ThereIsAlreadyAnObject(viewName.Leaf);
        }

        var outputColumns = ComputeViewOutputColumns(bodySelection, renameList, viewName.Leaf);

        var (baseTable, baseColumnOrdinals, rejectionReason, visibilityCheck, checkOptionCheck) =
            AnalyzeViewUpdatability(bodySelection, withCheckOption);

        var objectId = context.CurrentDatabase.AllocateObjectId();
        var view = new View(
            schema,
            viewName.Leaf,
            objectId,
            outputColumns,
            bodyText,
            withCheckOption,
            createDate: context.Batch.CurrentStatement.UtcNow,
            baseTable: baseTable,
            baseColumnOrdinals: baseColumnOrdinals,
            rejectionReason: rejectionReason,
            visibilityCheck: visibilityCheck,
            checkOptionCheck: checkOptionCheck);
        schema.Views[viewName.Leaf] = view;
        return true;
    }

    /// <summary>
    /// Consumes a <c>WITH option [, option ...]</c> clause on a view's
    /// header. Cursor on entry: the <c>WITH</c> keyword. Cursor on exit:
    /// the first token after the option list (expected to be <c>AS</c>).
    /// </summary>
    private static void ParseViewOptions(ParserContext context)
    {
        context.MoveNextRequired();
        while (true)
        {
            if (context.Token is not UnquotedString opt
                || opt.ContextualKeyword is not (ContextualKeyword.Schemabinding or ContextualKeyword.Encryption or ContextualKeyword.View_Metadata))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Walks the body's projected schema to derive <see cref="View.OutputColumns"/>:
    /// applies the explicit column-rename list when one was supplied (Msg
    /// 8158 / 8159 on count mismatch); otherwise validates that every
    /// projection has a name (Msg 4511) and that names don't duplicate
    /// (Msg 4506). Nullability is conservatively True — same fidelity gap
    /// as inline TVFs.
    /// </summary>
    private static HeapColumn[] ComputeViewOutputColumns(Selection bodySelection, List<string>? renameList, string viewName)
    {
        var projectionCount = bodySelection.Schema.Length;
        string[] columnNames;
        if (renameList is { } renames)
        {
            // Msg 8158 / 8159 — same wording the existing CTE rename-list
            // mismatch factory uses (probe-confirmed identical text).
            if (renames.Count < projectionCount)
                throw SimulatedSqlException.CteHasMoreColumnsThanList(viewName);
            if (renames.Count > projectionCount)
                throw SimulatedSqlException.CteHasFewerColumnsThanList(viewName);
            columnNames = [.. renames];
        }
        else
        {
            columnNames = bodySelection.ColumnNames;
        }

        var seen = new HashSet<string>(Collation.Default);
        var output = new HeapColumn[projectionCount];
        for (var i = 0; i < projectionCount; i++)
        {
            var name = columnNames[i];
            if (string.IsNullOrEmpty(name))
                throw SimulatedSqlException.CreateViewMissingColumnName(i + 1);
            if (!seen.Add(name))
                throw SimulatedSqlException.DuplicateColumnInViewOrFunction(name, viewName);
            output[i] = new HeapColumn(name, bodySelection.Schema[i], maxLength: null, nullable: true);
        }
        return output;
    }
}
