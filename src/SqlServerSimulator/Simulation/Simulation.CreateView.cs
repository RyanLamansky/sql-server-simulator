using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE [OR ALTER] VIEW schema.name [(col_list)] [WITH
    /// SCHEMABINDING | ENCRYPTION | VIEW_METADATA] AS &lt;SELECT&gt; [WITH
    /// CHECK OPTION]</c> — and, via <paramref name="isAlter"/>, the
    /// identically-shaped <c>ALTER VIEW</c> — storing a <see cref="View"/> in
    /// the target
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
    /// <item><c>ALTER VIEW</c> / <c>CREATE OR ALTER VIEW</c> over a name held
    /// by another object kind → <strong>Msg 2010</strong>; bare <c>ALTER
    /// VIEW</c> on a name nothing holds → <strong>Msg 208</strong>.</item>
    /// </list>
    /// <para>
    /// <strong>What the replacement preserves</strong> (probe-confirmed):
    /// the <see cref="SchemaObject.ObjectId"/> and
    /// <see cref="SchemaObject.CreateDate"/>, every permission granted on the
    /// view (the permission store keys object-scope rows by object_id), and
    /// the view's <c>INSTEAD OF</c> triggers, which reseat onto the new
    /// instance. <see cref="SchemaObject.ModifyDate"/> advances. Indexes are
    /// <em>not</em> preserved: an ALTER of an indexed view drops its indexes
    /// along with the schema-binding that allowed them.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateView(ParserContext context, bool isAlter, bool createOrAlter)
    {
        // CREATE OR ALTER reports under the plain CREATE label (probe-confirmed
        // — real names the statement by the verb it started with).
        if (context.Batch.BlockDepth > 0 || context.Batch.HasDispatchedStatement)
            throw SimulatedSqlException.MustBeFirstStatementInBatch(isAlter ? "ALTER VIEW" : "CREATE VIEW");

        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var viewName = BatchContext.ParseObjectName(context);
        RejectQualifiedModuleName(viewName, "VIEW");
        var schema = ResolveModuleSchema(context, viewName, isAlter);

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

        // Optional WITH-clause: SCHEMABINDING / ENCRYPTION / VIEW_METADATA.
        // SCHEMABINDING is captured (it gates CREATE INDEX on the view and
        // surfaces through sys.sql_modules.is_schema_bound / OBJECTPROPERTY);
        // the other two parse-and-ignore.
        var isSchemaBound = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            isSchemaBound = ParseViewOptions(context);
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
        var bodySelection = ParseBodyQuery(context);
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

        // DDL gate on a plain create: db-scope CREATE VIEW + ALTER on the
        // target schema (Msg 262 state 18 with the view as Procedure
        // attribution, else Msg 2760). Replacing an existing view isn't gated
        // here (outside the probed scope), matching the procedure parser.
        if (!isAlter && !createOrAlter)
            PermissionEnforcement.CheckCreateModule(context.Batch, "CREATE VIEW", viewName.Leaf, schema);

        // Cross-kind collisions (Msg 2714 on create, Msg 2010 on either ALTER
        // leg) and the ALTER-on-missing Msg 208 all live in the shared helper.
        var replaced = (View?)ResolveModuleAlterTarget(
            context, schema, viewName, isAlter, createOrAlter,
            schema.Views.TryGetValue(viewName.Leaf, out var existingView) ? existingView : null);

        if (isSchemaBound)
            SchemaBinding.EnforceBody(context.CurrentDatabase, "view", $"{schema.Name}.{viewName.Leaf}", bodyText);

        var outputColumns = ComputeViewOutputColumns(context.CurrentDatabase.Collation, bodySelection, renameList, viewName.Leaf);

        var (baseTable, baseColumnOrdinals, rejectionReason, visibilityCheck, checkOptionCheck) =
            AnalyzeViewUpdatability(context.CurrentDatabase.Collation, bodySelection, withCheckOption);

        var view = new View(
            schema,
            viewName.Leaf,
            replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId(),
            outputColumns,
            bodyText,
            withCheckOption,
            isSchemaBound,
            createDate: replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow,
            baseTable: baseTable,
            baseColumnOrdinals: baseColumnOrdinals,
            rejectionReason: rejectionReason,
            visibilityCheck: visibilityCheck,
            checkOptionCheck: checkOptionCheck)
        {
            DefinitionText = BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, bodyEnd, isAlter, createOrAlter),
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
        };
        if (replaced is not null)
        {
            view.ModifyDate = context.Batch.CurrentStatement.UtcNow;
            DetachIndexedViewDependencies(replaced);
            ReseatTriggerParents(context.CurrentDatabase, replaced, view);
        }
        schema.Views[viewName.Leaf] = view;
        RecordDdlEvent(context, replaced is null ? "CREATE_VIEW" : "ALTER_VIEW", schema.Name, viewName.Leaf, "VIEW");
        return true;
    }

    /// <summary>
    /// Unwires a replaced (or dropped) view from every base table's
    /// <see cref="HeapTable.DependentIndexedViews"/> list. Without this the
    /// stale instance keeps driving unique-index re-validation on base-table
    /// DML — enforcing indexes that no longer exist, over a body that no
    /// longer describes the view. No-op for an ordinary unindexed view, which
    /// never registered a dependency.
    /// </summary>
    private static void DetachIndexedViewDependencies(View view)
    {
        foreach (var table in view.ReferencedBaseTables)
            _ = table.DependentIndexedViews.Remove(view);
        view.ReferencedBaseTables = [];
    }

    /// <summary>
    /// Points every trigger attached to <paramref name="replaced"/> at
    /// <paramref name="replacement"/>. A view's <c>INSTEAD OF</c> triggers
    /// survive <c>ALTER VIEW</c> on real SQL Server (probe-confirmed), and the
    /// trigger-firing paths match a trigger to its parent by reference, so the
    /// swap has to carry them across.
    /// </summary>
    private static void ReseatTriggerParents(Database database, SchemaObject replaced, SchemaObject replacement)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (ReferenceEquals(trigger.Parent, replaced))
                    trigger.Parent = replacement;
            }
        }
    }

    /// <summary>
    /// Consumes a <c>WITH option [, option ...]</c> clause on a view's
    /// header. Cursor on entry: the <c>WITH</c> keyword. Cursor on exit:
    /// the first token after the option list (expected to be <c>AS</c>).
    /// Returns true when <c>SCHEMABINDING</c> was among the options.
    /// </summary>
    private static bool ParseViewOptions(ParserContext context)
    {
        var isSchemaBound = false;
        context.MoveNextRequired();
        while (true)
        {
            if (context.Token is not UnquotedString opt
                || opt.ContextualKeyword is not (ContextualKeyword.SchemaBinding or ContextualKeyword.Encryption or ContextualKeyword.View_Metadata))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (opt.ContextualKeyword is ContextualKeyword.SchemaBinding)
                isSchemaBound = true;
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return isSchemaBound;
    }

    /// <summary>
    /// Walks the body's projected schema to derive <see cref="View.OutputColumns"/>:
    /// applies the explicit column-rename list when one was supplied (Msg
    /// 8158 / 8159 on count mismatch); otherwise validates that every
    /// projection has a name (Msg 4511) and that names don't duplicate
    /// (Msg 4506). Nullability is conservatively True — same fidelity gap
    /// as inline TVFs.
    /// </summary>
    private static HeapColumn[] ComputeViewOutputColumns(Collation collation, Selection bodySelection, List<string>? renameList, string viewName)
    {
        var projectionCount = bodySelection.Schema.Length;
        string[] columnNames;
        if (renameList is { } renames)
        {
            // Msg 8158 / 8159 — the shared column-alias-list mismatch factory
            // (probe-confirmed identical text across CTE / view / VALUES).
            if (renames.Count < projectionCount)
                throw SimulatedSqlException.HasMoreColumnsThanColumnList(viewName);
            if (renames.Count > projectionCount)
                throw SimulatedSqlException.HasFewerColumnsThanColumnList(viewName);
            columnNames = [.. renames];
        }
        else
        {
            columnNames = bodySelection.ColumnNames;
        }

        var seen = new HashSet<string>(collation);
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
