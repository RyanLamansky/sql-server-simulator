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
        // Every error from here on names the view as its Procedure — its
        // syntax, its binding, its shape, even the name collision — as the
        // statement wrote it (probed 2026-09-25 against SQL Server 2025). The
        // statement is its batch's only one, so nothing after it inherits this.
        // An element of a CREATE SCHEMA is no module of its own there, and
        // its errors name none (probed 2026-09-30 against SQL Server 2025).
        if (context.Batch.CreateSchemaElementScope is null)
            context.Batch.ErrorProcedureName = viewName.Leaf;
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
        var options = ParseModuleOptions(context, ModuleOptionHost.View, viewName.Leaf);
        var isSchemaBound = options.SchemaBinding;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Parse the body's SELECT once to derive output schema and locate
        // the body's end. Selection.Parse leaves the cursor at the first
        // un-consumed token after the SELECT — typically the next
        // statement-starting keyword OR the trailing WITH CHECK OPTION.
        context.MoveNextRequired();
        // The body may be parenthesized, to any depth (probed 2026-09-25
        // against SQL Server 2025); the stored body is the query inside. A
        // body opening with a parenthesized set-operation branch,
        // `(SELECT …) UNION ALL (SELECT …)`, keeps those parentheses.
        var bodyParens = Selection.CountWrappingParentheses(context);
        for (var i = 0; i < bodyParens; i++)
            context.MoveNextRequired();
        var commandText = context.Command.CommandText;
        var bodyStart = context.Token?.StartIndex
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        context.BindingViewDefinition = true;
        var bodySelection = ParseBodyQuery(context, rejectsNextValueFor: true, bodyParens > 0 ? QueryPosition.ParenthesizedModuleBody : QueryPosition.Statement);
        context.BindingViewDefinition = false;
        // A CREATE SCHEMA's next element is no part of this body. A refusal
        // leaves the rule set for the recovery that follows it.
        context.SchemaBoundBody = SchemaBoundBody.None;
        var bodyEnd = context.Token?.StartIndex ?? commandText.Length;
        var bodyText = commandText[bodyStart..bodyEnd];
        for (; bodyParens > 0; bodyParens--)
        {
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }

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

        // A view body runs to the end of its batch — anything past it is a
        // syntax error at that token, before the view is created.
        RejectStatementAfterModuleBody(context, viewName.Leaf);

        if (context.Batch.IsSkipping)
            return true;

        // DDL gate: db-scope CREATE VIEW + schema ALTER when the statement
        // creates (Msg 262 state 18 with the view as Procedure attribution, else
        // Msg 2760), object ALTER when it replaces an existing view (Msg 3701
        // state 20).
        CheckModuleDdlPermission(
            context, "CREATE VIEW", viewName, schema, isAlter, createOrAlter,
            schema.Views.GetValueOrDefault(viewName.Leaf));

        // Cross-kind collisions (Msg 2714 on create, Msg 2010 on either ALTER
        // leg) and the ALTER-on-missing Msg 208 all live in the shared helper.
        var replaced = (View?)ResolveModuleAlterTarget(
            context, schema, viewName, isAlter, createOrAlter,
            schema.Views.TryGetValue(viewName.Leaf, out var existingView) ? existingView : null);

        if (isSchemaBound)
            SchemaBinding.EnforceBody(context.CurrentDatabase, "view", $"{schema.Name}.{viewName.Leaf}", bodyText);

        var outputColumns = ComputeViewOutputColumns(context.CurrentDatabase.Collation, bodySelection, renameList, viewName.Leaf);

        var (baseTable, baseColumnOrdinals, rejectionReason, visibilityCheck, checkOptionCheck, isJoinUpdatable, partitionedBase) =
            AnalyzeViewUpdatability(context.CurrentDatabase.Collation, bodySelection, withCheckOption);
        // A stored UNION ALL view whose every branch reads one table plainly
        // is a partitioned view: a write through it routes to its members.
        var isPartitioned = IsPartitionedViewShape(bodySelection);
        if (isPartitioned)
        {
            baseColumnOrdinals = new int[outputColumns.Length];
            for (var i = 0; i < baseColumnOrdinals.Length; i++)
                baseColumnOrdinals[i] = i;
        }

        var (upstreamView, upstreamOrdinals) = baseTable is null ? (null, []) : UpstreamLinkOf(context.CurrentDatabase.Collation, bodySelection);
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
            checkOptionCheck: checkOptionCheck,
            isJoinUpdatable: isJoinUpdatable)
        {
            DefinitionText = options.Encryption ? null : BuildModuleDefinition(commandText, context.Batch.CurrentStatement.StartIndex, isAlter, createOrAlter),
            UsesQuotedIdentifier = context.QuotedIdentifiers,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            DerivedOutputColumns = DerivedOutputColumnsFor(bodySelection, baseTable, rejectionReason, outputColumns.Length),
            UnionOwnerName = UnionOwnerNameOf(bodySelection, rejectionReason),
            UnionLeadsWithJoin = UnionLeadsWithJoinOf(bodySelection, rejectionReason),
            IsRowLimited = IsRowLimitedBody(bodySelection),
            IsWindowed = IsWindowedBody(bodySelection),
            VolatileColumns = bodySelection.VolatileColumns,
            UpstreamView = upstreamView,
            UpstreamColumnOrdinals = upstreamOrdinals,
            PartitionedBase = partitionedBase,
        };
        if (isPartitioned)
            view.PartitionedBase = view;
        var replacedBases = replaced?.ReferencedBaseTables;
        if (replaced is not null)
        {
            view.ModifyDate = context.Batch.CurrentStatement.UtcNow;
            DetachIndexedViewDependencies(replaced);
            ReseatTriggerParents(context.CurrentDatabase, replaced, view);
        }
        schema.Views[viewName.Leaf] = view;
        if (replaced is not null)
            RebindExtendedProperties(context.Batch, replaced, view);
        var database = context.CurrentDatabase;
        RecordDdlUndo(context, () =>
        {
            if (replaced is null)
            {
                _ = schema.Views.TryRemove(viewName.Leaf, out _);
                return;
            }
            schema.Views[viewName.Leaf] = replaced;
            ReattachIndexedViewDependencies(replaced, replacedBases!);
            ReseatTriggerParents(database, view, replaced);
        });
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
    /// Reverses <see cref="DetachIndexedViewDependencies"/> when a rollback
    /// restores <paramref name="view"/>, rewiring it to the base tables it
    /// maintained.
    /// </summary>
    private static void ReattachIndexedViewDependencies(View view, HeapTable[] bases)
    {
        view.ReferencedBaseTables = bases;
        foreach (var table in bases)
            table.DependentIndexedViews.Add(view);
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
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, trigger) in schema.Triggers)
            {
                if (ReferenceEquals(trigger.Parent, replaced))
                    trigger.Parent = replacement;
            }
        }
    }

    /// <summary>
    /// Walks the body's projected schema to derive <see cref="View.OutputColumns"/>:
    /// applies the explicit column-rename list when one was supplied (Msg
    /// 8158 / 8159 on count mismatch); otherwise validates that every
    /// projection has a name (Msg 4511) and that names don't duplicate
    /// (Msg 4506). Per-column nullability comes from the body's own
    /// projection inference (<see cref="Selection.ColumnNullability"/>) — the
    /// same rules the TDS COLMETADATA fNullable flag reports, so a view's
    /// <c>sys.columns</c> row and a direct read of its body agree. That
    /// inference declines the joined and multi-source shapes (an outer join
    /// NULL-fills the inner side, which base-column nullability alone would
    /// miss), and those keep the conservative True.
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
        var nullability = bodySelection.ColumnNullability;
        var output = new HeapColumn[projectionCount];
        for (var i = 0; i < projectionCount; i++)
        {
            var name = columnNames[i];
            if (string.IsNullOrEmpty(name))
                throw SimulatedSqlException.CreateViewMissingColumnName(i + 1);
            if (!seen.Add(name))
                throw SimulatedSqlException.DuplicateColumnInViewOrFunction(name, viewName);
            var nullable = nullability is null || i >= nullability.Length || nullability[i];
            // A character column reports the collation its expression carries,
            // as a SELECT INTO's does — OPENJSON's key column its
            // Latin1_General_BIN2 (probed 2026-10-02 against SQL Server 2025).
            var type = bodySelection.Schema[i];
            output[i] = new HeapColumn(name, type, maxLength: null, nullable: nullable, collation: type is VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType ? type.Collation?.Name : null, spelledNumeric: bodySelection.ColumnReportsNumeric is { } numeric && numeric[i])
            {
                AliasType = bodySelection.ColumnAliasTypes?[i],
                IdentitySource = bodySelection.ColumnIdentitySources?[i],
            };
        }
        return output;
    }
}
