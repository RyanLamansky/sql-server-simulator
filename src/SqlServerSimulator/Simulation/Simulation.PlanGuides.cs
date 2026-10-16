using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_create_plan_guide, sp_control_plan_guide, sys.fn_validate_plan_guide and
// sp_get_query_template: the plan-guide surface, each check and its line real's
// procedures' own (probed 2026-10-15 against SQL Server 2025). A guide is
// stored, listed, validated and dropped; with no optimizer to steer, it changes
// no plan.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] CreatePlanGuideParameters =
    [
        new("name", SqlType.NVarchar, 128),
        new("stmt", SqlType.NVarchar, int.MaxValue, SqlValue.Null(SqlType.NVarchar)),
        new("type", SqlType.NVarchar, 60),
        new("module_or_batch", SqlType.NVarchar, int.MaxValue, SqlValue.Null(SqlType.NVarchar)),
        new("params", SqlType.NVarchar, int.MaxValue, SqlValue.Null(SqlType.NVarchar)),
        new("hints", SqlType.NVarchar, int.MaxValue, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] ControlPlanGuideParameters =
    [
        new("operation", SqlType.NVarchar, 60),
        new("name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly string[] QueryTemplateParameterNames = ["querytext", "templatetext", "parameters"];

    /// <summary>
    /// <c>sp_create_plan_guide @name, @stmt, @type [, @module_or_batch [,
    /// @params [, @hints]]]</c>: stores a plan guide over one statement of a
    /// batch (<c>SQL</c>), of a procedure, function or trigger
    /// (<c>OBJECT</c>), or over a parameterized form (<c>TEMPLATE</c>). The
    /// procedure runs in a transaction of its own, so the refusals — every one
    /// ending the batch and rolling back as under <c>XACT_ABORT</c> — leave a
    /// <c>CATCH</c> in a doomed transaction one level deeper. The statement
    /// and its scope compile with <c>@params</c> declared (Msg 10516), the
    /// hints compile as the statement's <c>OPTION</c> clause (Msg 10504), and
    /// a query reading no table takes no guide (Msg 10519); a missing object
    /// defers, as a batch's compile does, to <c>sys.fn_validate_plan_guide</c>.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpCreatePlanGuide(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindPlanGuideArguments("sp_create_plan_guide", calledAs, arguments, CreatePlanGuideParameters);
        BeginProcedureTransaction(batch, calledAs, 9);
        if (values[0].IsNull)
            throw NullArgument(batch, AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureExpectsParameterOfType("@name", "nvarchar(128)", state: 100), 20));
        if (values[2].IsNull)
            throw NullArgument(batch, AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureExpectsParameterOfType("@type", "nvarchar(60)", state: 102), 20));

        var name = values[0].AsString;
        var type = values[2].AsString;
        var stmt = values[1].IsNull ? null : values[1].AsString;
        var moduleOrBatch = values[3].IsNull ? null : values[3].AsString;
        var parameters = values[4].IsNull ? null : values[4].AsString;
        var hints = values[5].IsNull ? null : values[5].AsString;
        PlanGuideScope? scope = DeviceNamesEqual(type, "OBJECT") ? PlanGuideScope.Object
            : DeviceNamesEqual(type, "SQL") ? PlanGuideScope.Sql
            : DeviceNamesEqual(type, "TEMPLATE") ? PlanGuideScope.Template
            : null;
        var line = scope == PlanGuideScope.Object ? 15 : 20;
        var database = batch.CurrentDatabase;

        if (stmt is null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideParameterRequired(name, "@stmt", "nvarchar(max)", state: 4), line);
        if (name.Length == 0)
            throw AbortingAtLine(batch, AtProcedureLine(SimulatedSqlException.PlanGuideNameEmpty(), calledAs, 20));
        if (name.StartsWith('#'))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideNameInvalid(name), line);
        if (scope is not { } kind)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideTypeNotAllowed(name, type), line);
        // A name in use is refused here unless its guide covers this statement,
        // which real reports later as the statement match (Msg 10502), whatever
        // the hints (probed 2026-10-16 against SQL Server 2025).
        if (database.PlanGuides.TryGetValue(name, out var named)
            && !(named.Scope == kind && string.Equals(named.Parameters, parameters, StringComparison.Ordinal) && SameStatement(named.QueryText, stmt, database)))
        {
            // Refused as the guide is stored, past its compile, so it spends
            // every id a stored guide would (probed 2026-10-16 against SQL
            // Server 2025).
            database.LastPlanGuideId += kind == PlanGuideScope.Object ? 1 : 2;
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideNameExists(name), line);
        }
        var typeWord = kind == PlanGuideScope.Object ? "object" : kind == PlanGuideScope.Sql ? "sql" : "template";
        switch (kind)
        {
            case PlanGuideScope.Object when moduleOrBatch is null:
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideParameterRequired(name, typeWord, "@module_or_batch", state: 1), line);
            case PlanGuideScope.Object when parameters is not null:
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideParameterMustBeNull(name, typeWord, "@params", state: 3), line);
            case PlanGuideScope.Template when moduleOrBatch is not null:
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideParameterMustBeNull(name, typeWord, "@module_or_batch", state: 5), line);
            case PlanGuideScope.Template when parameters is null:
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideParameterRequired(name, typeWord, "@params", state: 3), line);
        }

        // The module an OBJECT guide names, and the batch the others compile.
        SchemaObject? module = null;
        string? moduleAttribution = null;
        if (kind == PlanGuideScope.Object)
        {
            module = PlanGuideModule(batch, name, moduleOrBatch!, calledAs, line, out moduleAttribution);
        }
        else
        {
            if (!PermissionEnforcement.HasDatabasePermission(batch, database, Permission.Alter))
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideCreatePermissionDenied(name), line);
            if (kind == PlanGuideScope.Template && FindPlanGuide(database, guide => guide.Scope == PlanGuideScope.Template && database.Collation.Equals(guide.QueryText, stmt)) is { } template)
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideTemplateExists(name, template.Name), line);
            if (kind == PlanGuideScope.Sql)
                RefuseMatchingGuide(database, kind, null, moduleOrBatch ?? stmt, parameters, stmt, name, null);
            // The compile spends an id of its own, which a refusal from here on
            // leaves spent: a SQL or TEMPLATE guide steps the id by two, and
            // one refused past this point by one (probed 2026-10-16 against
            // SQL Server 2025).
            database.LastPlanGuideId++;
            var scopeText = kind == PlanGuideScope.Sql ? moduleOrBatch ?? stmt : stmt;
            if (this.CompileWithDeclarations(batch, scopeText, parameters, out _) is { } batchError)
                throw PlanGuideReport(batch, batchError, AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideBatchDoesNotCompile(name), line));
        }

        // The statement: one, of a kind a guide takes, and among the scope's.
        // An empty one is taken as it stands (probed 2026-10-15 against SQL
        // Server 2025).
        var empty = string.IsNullOrWhiteSpace(stmt);
        if (!empty)
        {
            if (this.CompileWithDeclarations(batch, stmt, kind == PlanGuideScope.Object ? null : parameters, out var statementCount) is null && statementCount > 1)
                throw AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideStatementNotSingle(name), moduleAttribution);
            var lead = FirstToken(stmt, database);
            if (lead is not ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge or Keyword.With } and not Operator { Character: '(' })
                throw AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideStatementIneligible(name), moduleAttribution);
            var scopeText = module is not null ? ModuleBodyText(module) : moduleOrBatch;
            if (scopeText is not null && !HoldsStatement(scopeText, stmt, database))
                throw AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideStatementNotInScope(name), moduleAttribution);
        }

        // The hints: a TEMPLATE guide's choose its parameterization, the others' compile as an OPTION clause.
        if (kind == PlanGuideScope.Template)
        {
            if (hints is null || !IsParameterizationHint(hints, database))
                throw AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideTemplateHintsIllegal(name), null);
        }
        else if (hints is not null && !empty)
        {
            var hintTokens = StatementTokensOf(hints, database, quotedIdentifiers: true);
            if (hintTokens is { Count: 0 })
                throw AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideHintsIncorrect(name), null);
            // The hints parse as an OPTION clause of their own, so anything
            // else they open with, or a separator in them, is a syntax error.
            var hintError = hintTokens is [not ReservedKeyword { Keyword: Keyword.Option } and var opening, ..]
                ? AtPlanGuideStatementLine(SimulatedSqlException.SyntaxErrorNear(opening), null)
                : hintTokens?.Find(static token => token is Operator { Character: ';' }) is { } separator
                ? AtPlanGuideStatementLine(SimulatedSqlException.SyntaxErrorNear(separator), null)
                : this.CompileStatementWithHints(batch, stmt, hints, kind == PlanGuideScope.Object ? null : parameters, ignoreDeferred: true);
            if (hintError is not null)
            {
                throw IsHintClauseError(hintError)
                    ? PlanGuideReport(batch, hintError, AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideHintsIncorrect(name), null))
                    : PlanGuideReport(batch, hintError, null);
            }
        }

        if (kind != PlanGuideScope.Template && !empty && !ReadsTable(stmt, database))
            throw AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideHintsNotApplicable(name), moduleAttribution);

        var scopeBatch = kind switch
        {
            PlanGuideScope.Sql => moduleOrBatch ?? stmt,
            PlanGuideScope.Template => stmt,
            _ => null,
        };
        if (kind == PlanGuideScope.Object)
            RefuseMatchingGuide(database, kind, module?.ObjectId, scopeBatch, parameters, stmt, name, moduleAttribution);

        var id = ++database.LastPlanGuideId;
        var created = new PlanGuide(id, name, kind, stmt, module?.ObjectId, scopeBatch, parameters, hints, batch.CurrentStatement.UtcNow);
        database.PlanGuides[name] = created;
        RecordDdlUndo(batch, () => _ = database.PlanGuides.TryRemove(name, out _));
        RecordDdlEvent(batch.Parser, "CREATE_PLAN_GUIDE", schemaName: null, name, "PLAN GUIDE",
            trailingElements: RenderEventParameters(name, stmt, type, moduleOrBatch, parameters, hints));
        CommitProcedureTransaction(batch);
    }

    /// <summary>
    /// <c>sp_control_plan_guide @operation [, @name]</c>: <c>DROP</c>,
    /// <c>DISABLE</c> or <c>ENABLE</c> one guide by name, or every guide in
    /// the database with the <c>ALL</c> forms, which take no name. Like
    /// <c>sp_create_plan_guide</c> it runs in a transaction of its own, and its
    /// refusals — an unknown operation (Msg 10503), a guide the database
    /// doesn't hold (Msg 10508, its state naming the operation) or one the
    /// caller may not control (Msg 10518) — come from
    /// <c>sys.sp_control_plan_guide_int</c> and end the batch as under
    /// <c>XACT_ABORT</c>; a NULL operation, or a NULL name for a single
    /// guide, is Msg 214, which doesn't.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpControlPlanGuide(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindPlanGuideArguments("sp_control_plan_guide", calledAs, arguments, ControlPlanGuideParameters);
        const string Internal = "sys.sp_control_plan_guide_int";
        BeginProcedureTransaction(batch, calledAs, 5);
        if (values[0].IsNull)
            throw NullArgument(batch, AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameterOfType("@operation", "nvarchar(60)"), Internal, 1));
        var operation = values[0].AsString;
        var (verb, all) = DeviceNamesEqual(operation, "DROP") ? ("drop", false)
            : DeviceNamesEqual(operation, "DISABLE") ? ("disable", false)
            : DeviceNamesEqual(operation, "ENABLE") ? ("enable", false)
            : DeviceNamesEqual(operation, "DROP ALL") ? ("drop", true)
            : DeviceNamesEqual(operation, "DISABLE ALL") ? ("disable", true)
            : DeviceNamesEqual(operation, "ENABLE ALL") ? ("enable", true)
            : (null, false);
        if (verb is not null && !all && values[1].IsNull)
            throw NullArgument(batch, AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameterOfType("@name", "nvarchar(128)"), Internal, 1));

        if (verb is null)
            throw AtProcedureLine(SimulatedSqlException.PlanGuideOperationNotAllowed(operation), Internal, 1);
        var database = batch.CurrentDatabase;
        List<PlanGuide> targets;
        string? eventName = null;
        if (all)
        {
            targets = [.. database.PlanGuides.Select(static pair => pair.Value).OrderBy(static guide => guide.Id)];
        }
        else
        {
            var written = values[1].AsString;
            eventName = Parser.Expressions.ObjectId.TryParseObjectName(written, out var parsed) && parsed.Count == 1 ? parsed.Leaf : written;
            if (!database.PlanGuides.TryGetValue(eventName, out var guide))
            {
                byte state = verb switch
                {
                    "drop" => 1,
                    "enable" => 2,
                    _ => 3,
                };
                throw AtProcedureLine(SimulatedSqlException.PlanGuideDoesNotExist(verb, written, state), Internal, 1);
            }
            eventName = guide.Name;
            targets = [guide];
        }
        foreach (var guide in targets)
        {
            if (!MayControlPlanGuide(batch, database, guide))
                throw AtProcedureLine(SimulatedSqlException.PlanGuideControlPermissionDenied(guide.Name), Internal, 1);
        }

        var now = batch.CurrentStatement.UtcNow;
        foreach (var guide in targets)
        {
            if (verb == "drop")
            {
                if (database.PlanGuides.TryRemove(guide.Name, out var removed))
                    RecordDdlUndo(batch, () => database.PlanGuides[removed.Name] = removed);
                continue;
            }
            var (wasDisabled, modified) = (guide.IsDisabled, guide.ModifyDate);
            guide.IsDisabled = verb == "disable";
            guide.ModifyDate = now;
            RecordDdlUndo(batch, () => (guide.IsDisabled, guide.ModifyDate) = (wasDisabled, modified));
        }
        RecordDdlEvent(batch.Parser, verb == "drop" ? "DROP_PLAN_GUIDE" : "ALTER_PLAN_GUIDE", schemaName: null, all ? "" : eventName, "PLAN GUIDE",
            trailingElements: RenderEventParameters(operation, values[1].IsNull ? null : values[1].AsString));
        CommitProcedureTransaction(batch);
    }

    /// <summary>
    /// <c>sp_get_query_template @querytext, @templatetext OUTPUT, @parameters
    /// OUTPUT</c>: the template forced parameterization makes of a statement
    /// and the declaration of its parameters
    /// (<see cref="ForcedParameterization"/>). The text compiles first, with
    /// <c>QUOTED_IDENTIFIER</c> off: a parse error is sent and followed by Msg
    /// 10523 state 2, as are no statement at all, while a binder error is sent
    /// and the template made regardless; more than one statement is Msg 10523
    /// state 3, a text an enabled <c>SQL</c> plan guide's batch matches and a
    /// statement nothing parameterizes Msg 10524. Both refusals end the batch
    /// as under <c>XACT_ABORT</c>. The procedure answers return code 1.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpGetQueryTemplate(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        const string Procedure = "sp_get_query_template";
        var positional = 0;
        foreach (var argument in arguments)
        {
            if ((argument.Name is null && ++positional > 3)
                || (argument.Name is { } written && Array.FindIndex(QueryTemplateParameterNames, parameter => BuiltInToken.Equals(written, parameter)) < 0))
            {
                throw AtProcedureLine(SimulatedSqlException.TooManyArgumentsToFunction(Procedure, state: 23).EndingBatch(), calledAs, 1);
            }
        }
        for (var i = 0; i < QueryTemplateParameterNames.Length; i++)
        {
            if (ArgumentFor(arguments, i, QueryTemplateParameterNames[i]) is not { IsDefault: false })
                throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameter(Procedure, QueryTemplateParameterNames[i], state: (byte)(20 + i)), calledAs, 1);
        }
        var query = ArgumentFor(arguments, 0, "querytext")!.Value;
        var templateSlot = ArgumentFor(arguments, 1, "templatetext")!.Value.OutputSlot
            ?? throw AtProcedureLine(SimulatedSqlException.FormalParameterNotOutput(Procedure, "@templatetext", state: 62), calledAs, 1);
        var parametersSlot = ArgumentFor(arguments, 2, "parameters")!.Value.OutputSlot
            ?? throw AtProcedureLine(SimulatedSqlException.FormalParameterNotOutput(Procedure, "@parameters", state: 63), calledAs, 1);
        if (query.Value.IsNull)
            throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameterOfType("@querytext", "nvarchar(max)", state: 21), calledAs, 1);
        if (!IsNVarcharMax(templateSlot))
            throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameterOfType("@templatetext", "nvarchar(max)", state: 22), calledAs, 1);
        if (!IsNVarcharMax(parametersSlot))
            throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameterOfType("@parameters", "nvarchar(max)", state: 23), calledAs, 1);

        var text = query.Value.CoerceTo(SqlType.NVarcharMax).AsString;
        var database = batch.CurrentDatabase;
        var compileError = this.CompileWithDeclarations(batch, text, declaration: null, out var statements, quotedIdentifiers: false);
        if (compileError is not null)
        {
            if (compileError.Errors.Any(static entry => entry.Class == 15))
                throw PlanGuideReport(batch, compileError, AtProcedureLine(SimulatedSqlException.QueryTemplateNotSingleQuery(state: 2), calledAs, 1));
            // A binder error goes out and the template is made regardless.
            yield return new SimulatedErrorOutcome(compileError);
        }
        var tokens = StatementTokensOf(text, database, quotedIdentifiers: false);
        if (tokens is null || tokens.TrueForAll(static token => token is Operator { Character: ';' }))
            throw AtProcedureLine(SimulatedSqlException.QueryTemplateNotSingleQuery(state: 2), calledAs, 1);
        if (statements > 1 || tokens.Exists(static token => token is ReservedKeyword { Keyword: Keyword.If or Keyword.While or Keyword.Begin }))
            throw AtProcedureLine(SimulatedSqlException.QueryTemplateNotSingleQuery(state: 3), calledAs, 1);

        // A statement a SQL guide matches compiles under the guide, unparameterized.
        if (FindPlanGuide(database, guide => guide is { Scope: PlanGuideScope.Sql, IsDisabled: false } && string.Equals(guide.ScopeBatch, text, StringComparison.Ordinal)) is not null)
            throw AtProcedureLine(SimulatedSqlException.QueryTemplateNotParameterizable(state: 1), calledAs, 1);
        if (ForcedParameterization.Parameterize(tokens, out var refusal) is not { } template)
            throw AtProcedureLine(SimulatedSqlException.QueryTemplateNotParameterizable(refusal), calledAs, 1);

        templateSlot.Value = SqlValue.FromNVarchar(template.Text).CoerceTo(templateSlot.DeclaredType);
        parametersSlot.Value = SqlValue.FromNVarchar(template.Declaration).CoerceTo(parametersSlot.DeclaredType);
        SetProcedureReturnCode(batch, returnCodeVar, 1);
    }

    /// <summary>
    /// The first error <c>sys.fn_validate_plan_guide(@plan_guide_id)</c>
    /// reports for the guide <paramref name="id"/> names — its statement and
    /// scope compiled again with its parameters and hints, as when it was
    /// created — or null when the guide is still valid. An id that is no
    /// guide of the current database's, or NULL, is Msg 10538.
    /// </summary>
    internal SimulatedSqlException? ValidatePlanGuide(BatchContext batch, int? id)
    {
        var database = batch.CurrentDatabase;
        if (id is not { } wanted || FindPlanGuide(database, guide => guide.Id == wanted) is not { } guide)
            throw SimulatedSqlException.PlanGuideIdNotFound();
        if (guide.Scope == PlanGuideScope.Object)
        {
            if (guide.ScopeObjectId is not { } objectId || PlanGuideModuleById(database, objectId) is null)
                return SimulatedSqlException.PlanGuideStatementNotInScope(guide.Name);
            return guide.Hints is { } objectHints ? this.CompileStatementWithHints(batch, guide.QueryText, objectHints, declaration: null) : null;
        }
        if (this.CompileWithDeclarations(batch, guide.ScopeBatch ?? guide.QueryText, guide.Parameters, out _) is { } batchError)
            return batchError;
        if (string.IsNullOrWhiteSpace(guide.QueryText))
            return null;
        if (guide.Scope == PlanGuideScope.Sql && guide.Hints is { } hints && this.CompileStatementWithHints(batch, guide.QueryText, hints, guide.Parameters) is { } hintError)
            return hintError;
        return MissingObject(batch, guide.QueryText, database);
    }

    /// <summary>
    /// Binds a plan-guide procedure's arguments as any system procedure's,
    /// except that too many of them (Msg 8144) end the batch, as they do the
    /// template procedure's (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    private static SqlValue[] BindPlanGuideArguments(string procedure, string calledAs, List<ProcArgument> arguments, SystemProcedureParameter[] parameters)
    {
        try
        {
            return BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        }
        catch (SimulatedSqlException refusal) when (refusal.Number == 8144)
        {
            var ending = refusal.EndingBatch();
            ending.PreserveDiagnostics(0, calledAs);
            throw ending;
        }
    }

    /// <summary>
    /// Opens the transaction a plan-guide procedure runs its work in, as
    /// real's opens its own at <paramref name="line"/>: nested in the
    /// caller's, or a new one — refused with Msg 3930 in a doomed one. Its
    /// refusals then end inside it, so uncaught they roll back the caller's
    /// transaction too and a <c>CATCH</c> finds it doomed.
    /// </summary>
    private static void BeginProcedureTransaction(BatchContext batch, string calledAs, int line)
    {
        var connection = batch.Connection;
        try
        {
            RejectWriteInDoomedTransaction(connection);
        }
        catch (SimulatedSqlException doomed)
        {
            throw AtSystemProcedureLine(calledAs, doomed, line);
        }
        if (connection.CurrentTransaction is { } existing)
            existing.TranCount++;
        else
            connection.CurrentTransaction = new SimulatedDbTransaction(connection.Simulation, connection, System.Data.IsolationLevel.Unspecified);
    }

    /// <summary>
    /// A NULL argument's Msg 214, which real's procedure raises inside the
    /// transaction it opened and which ends only its statement: uncaught, the
    /// procedure closes that transaction on its way out, while a <c>CATCH</c>
    /// takes control before it can, so the transaction stays open, committable,
    /// <c>@@TRANCOUNT</c> one higher (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static SimulatedSqlException NullArgument(BatchContext batch, SimulatedSqlException refusal)
    {
        if (batch.Connection.OpenTryFrames == 0)
            CommitProcedureTransaction(batch);
        return refusal;
    }

    /// <summary>
    /// Msg 10502 for a guide over the scope, <c>@params</c> and statement of
    /// one the database holds — for a <c>SQL</c> guide checked ahead of its
    /// compile, since it spends no id (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static void RefuseMatchingGuide(Database database, PlanGuideScope kind, int? moduleId, string? scopeBatch, string? parameters, string stmt, string name, string? moduleAttribution)
    {
        if (FindPlanGuide(database, guide => guide.Scope == kind && guide.ScopeObjectId == moduleId
                && string.Equals(guide.ScopeBatch, scopeBatch, StringComparison.Ordinal) && string.Equals(guide.Parameters, parameters, StringComparison.Ordinal)
                && SameStatement(guide.QueryText, stmt, database)) is { } existing)
        {
            throw AtPlanGuideStatementLine(SimulatedSqlException.PlanGuideMatchesExisting(name, existing.Name), moduleAttribution);
        }
    }

    /// <summary>Commits the transaction <see cref="BeginProcedureTransaction"/> opened.</summary>
    private static void CommitProcedureTransaction(BatchContext batch)
    {
        if (batch.Connection.CurrentTransaction is not { } transaction)
            return;
        if (--transaction.TranCount == 0)
            transaction.EndCommit();
    }

    /// <summary>
    /// The procedure, function or trigger an <c>OBJECT</c> guide names, or
    /// real's refusal: a name past two parts is Msg 10505, a temporary object
    /// Msg 10527, and one that is missing, of another kind, inline, a system
    /// object or one the caller can't alter Msg 10515. The name the module's
    /// own compile reports under comes back with it.
    /// </summary>
    private static SchemaObject PlanGuideModule(BatchContext batch, string guideName, string written, string calledAs, int line, out string attribution)
    {
        if (!Parser.Expressions.ObjectId.TryParseObjectName(written, out var name) || name.Count > 2)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideModuleNameNotTwoPart(guideName, written), line);
        if (name.Leaf.StartsWith('#'))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideOnTemporaryObject(guideName, written), line);
        var database = batch.CurrentDatabase;
        SchemaObject? module = null;
        if (batch.TryResolveCallerSchema(name, out var schema) && schema.SchemaId != Database.SysSchemaId)
        {
            module = schema.Procedures.TryGetValue(name.Leaf, out var procedure) && procedure.ClrEntry is null ? procedure
                : schema.Functions.TryGetValue(name.Leaf, out var function) && function.ObjectTypeCode is "FN" or "TF" ? function
                : schema.Triggers.TryGetValue(name.Leaf, out var trigger) ? trigger
                : null;
        }
        if (module is null || !PermissionEnforcement.HasObjectAlter(batch, database, module.ObjectId, module.SchemaId))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PlanGuideModuleNotFound(guideName, written), line);
        attribution = name.Count == 2 ? $"{name[0]}.{name.Leaf}" : name.Leaf;
        return module;
    }

    /// <summary>The body a module's statements are matched in.</summary>
    private static string ModuleBodyText(SchemaObject module) => module switch
    {
        Schemas.Procedure procedure => procedure.BodyText,
        UserDefinedFunction function => function.BodyText,
        Trigger trigger => trigger.BodyText,
        _ => "",
    };

    /// <summary>
    /// An error raised as the guide's statement compiles: line 1 of the
    /// module an <c>OBJECT</c> guide names, or of no procedure for the others.
    /// </summary>
    private static SimulatedSqlException AtPlanGuideStatementLine(SimulatedSqlException error, string? module)
    {
        error.PreserveDiagnostics(1, module);
        error.RaisedBySystemProcedure = true;
        return error;
    }

    /// <summary>
    /// What a compile raised followed by the procedure's own refusal, the
    /// two sent together and ending the batch as the refusal does; with no
    /// refusal, the compile's errors end it alone.
    /// </summary>
    private static SimulatedSqlException PlanGuideReport(BatchContext batch, SimulatedSqlException compileError, SimulatedSqlException? refusal) =>
        AbortingAtLine(batch, SimulatedSqlException.AbortingAggregate(refusal is null ? [compileError] : [compileError, refusal]));

    /// <summary>
    /// <paramref name="report"/>, its transaction ended as under
    /// <c>XACT_ABORT</c> here when its first entry is a parse-phase error —
    /// rolled back, or doomed under a <c>TRY</c> — which the dispatcher's own
    /// promotion leaves alone at that severity.
    /// </summary>
    private static SimulatedSqlException AbortingAtLine(BatchContext batch, SimulatedSqlException report)
    {
        var connection = batch.Connection;
        if (report.Class == 15 && connection.CurrentTransaction is { } transaction)
        {
            if (connection.OpenTryFrames > 0)
                transaction.Doomed = true;
            else
                transaction.EndRollback();
        }
        return report;
    }

    /// <summary>Whether a compile error belongs to the hint clause's own grammar, which real follows with Msg 10504.</summary>
    private static bool IsHintClauseError(SimulatedSqlException error) =>
        error.Errors.Any(static entry => entry.Class == 15 || entry.Number is 137 or 1042);

    /// <summary>
    /// Compiles <paramref name="text"/> as a batch with the parameters
    /// <paramref name="declaration"/> declares (an <c>sp_executesql</c>-style
    /// list) declared, as real compiles a guide's scope: null when it
    /// compiles, else what the compile raised, a declaration that doesn't
    /// parse included. Counts the batch's top-level statements.
    /// </summary>
    private SimulatedSqlException? CompileWithDeclarations(BatchContext batch, string text, string? declaration, out int statements, bool quotedIdentifiers = true)
    {
        statements = 0;
        var connection = batch.Connection;
        List<SpExecuteSqlParam> parameters;
        try
        {
            parameters = ParseSpExecuteSqlParamDefinitions(declaration ?? "", connection);
        }
        catch (SimulatedSqlException declarationError)
        {
            return declarationError;
        }
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var tableVariables = new Dictionary<string, HeapTable>(BatchContext.VariableNameComparer);
        foreach (var parameter in parameters)
        {
            if (parameter.TableType is { } tableType)
                tableVariables[parameter.Name] = CloneTableValuedArgument(tableType, parameter.Name, batch, supplied: null);
            else
                variables[parameter.Name] = new VariableSlot(parameter.Type, parameter.DeclaredMaxLength, SqlValue.Null(parameter.Type), parameter: null);
        }
        var sessionQuotedIdentifiers = connection.QuotedIdentifiers;
        connection.QuotedIdentifiers = quotedIdentifiers && sessionQuotedIdentifiers;
        try
        {
            using var command = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // the guide's own text, compiled and never run
            command.CommandText = text.Length == 0 ? " " : text;
#pragma warning restore CA2100
            var statementBatch = new BatchContext(command, variables, new ProcFrame("<dynamic-sql>", isDynamicSql: true), tableVariables);
            var compile = CompileContextFor(statementBatch, command);
            var error = this.CompileBatch(compile, key: null, out _);
            statements = compile.TopLevelStatementsDispatched;
            return error;
        }
        finally
        {
            connection.QuotedIdentifiers = sessionQuotedIdentifiers;
        }
    }

    /// <summary>
    /// Compiles a guide's statement with its hints as the statement's
    /// <c>OPTION</c> clause, in place of any the statement writes. With
    /// <paramref name="ignoreDeferred"/>, the errors real leaves to
    /// <c>sys.fn_validate_plan_guide</c> as it creates a guide — an index hint
    /// naming no index (Msg 308), a plan the hints leave impossible (Msg
    /// 8622) — don't count.
    /// </summary>
    private SimulatedSqlException? CompileStatementWithHints(BatchContext batch, string stmt, string hints, string? declaration, bool ignoreDeferred = false)
    {
        var tokens = StatementTokensOf(stmt, batch.CurrentDatabase, quotedIdentifiers: true) ?? [];
        var end = tokens.Count > 0 ? tokens[^1].EndIndex : stmt.Length;
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case ReservedKeyword { Keyword: Keyword.Option } when depth == 0:
                    end = tokens[i].StartIndex;
                    i = tokens.Count;
                    break;
            }
        }
        var trimmed = stmt[..end].TrimEnd().TrimEnd(';');
        var error = this.CompileWithDeclarations(batch, trimmed + " " + hints, declaration, out _);
        return ignoreDeferred && error is not null && error.Errors.All(static entry => entry.Number is 308 or 8622) ? null : error;
    }

    /// <summary>
    /// A table the statement reads that the database doesn't hold, as Msg 208
    /// — what a guide's compile deferred, which validation reports. A
    /// statement opening with a common table expression is left alone, its
    /// names its own.
    /// </summary>
    private static SimulatedSqlException? MissingObject(BatchContext batch, string stmt, Database database)
    {
        var tokens = StatementTokensOf(stmt, database, quotedIdentifiers: true) ?? [];
        if (tokens is [ReservedKeyword { Keyword: Keyword.With }, ..])
            return null;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] is not ReservedKeyword { Keyword: Keyword.From or Keyword.Join or Keyword.Update or Keyword.Into } || tokens[i + 1] is not Name first || first.Value.StartsWith('#'))
                continue;
            var last = i + 1;
            while (last + 2 < tokens.Count && tokens[last + 1] is Operator { Character: '.' } && tokens[last + 2] is Name)
                last += 2;
            if ((last + 1 < tokens.Count && tokens[last + 1] is Operator { Character: '(' })
                || !Parser.Expressions.ObjectId.TryParseObjectName(stmt[first.StartIndex..tokens[last].EndIndex], out var name)
                || name.Count > 2
                || (name.Count == 2 && (BuiltInToken.Equals(name[0], "sys") || BuiltInToken.Equals(name[0], "INFORMATION_SCHEMA"))))
            {
                continue;
            }
            if (!batch.TryResolveCallerSchema(name, out var schema) || !(schema.HeapTables.ContainsKey(name.Leaf) || schema.Views.ContainsKey(name.Leaf) || schema.Synonyms.ContainsKey(name.Leaf)))
                return SimulatedSqlException.InvalidObjectName(name);
        }
        return null;
    }

    /// <summary>The tokens of <paramref name="text"/>, no whitespace or comment among them, or null for a text that doesn't tokenize.</summary>
    private static List<Token>? StatementTokensOf(string text, Database database, bool quotedIdentifiers)
    {
        var tokens = new List<Token>();
        var index = 0;
        try
        {
            while (Tokenizer.NextToken(text, ref index, database.Collation, quotedIdentifiers, database.CompatibilityLevel) is { } token)
            {
                if (token is not (Whitespace or Comment))
                    tokens.Add(token);
            }
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
        return tokens;
    }

    private static Token? FirstToken(string text, Database database) =>
        StatementTokensOf(text, database, quotedIdentifiers: true) is { Count: > 0 } tokens ? tokens[0] : null;

    /// <summary>Whether a statement reads a table anywhere — names a FROM clause, or writes one — which a guide's hints need.</summary>
    private static bool ReadsTable(string stmt, Database database) =>
        StatementTokensOf(stmt, database, quotedIdentifiers: true) is { } tokens
        && tokens.Exists(static token => token is ReservedKeyword { Keyword: Keyword.From or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge });

    /// <summary>Whether <paramref name="hints"/> is exactly one <c>OPTION (PARAMETERIZATION FORCED | SIMPLE)</c>, as a <c>TEMPLATE</c> guide's must be.</summary>
    private static bool IsParameterizationHint(string hints, Database database) =>
        StatementTokensOf(hints, database, quotedIdentifiers: true) is [ReservedKeyword { Keyword: Keyword.Option }, Operator { Character: '(' }, Name parameterization, Name mode, Operator { Character: ')' }]
        && BuiltInToken.Equals(parameterization.Value, "PARAMETERIZATION")
        && (BuiltInToken.Equals(mode.Value, "FORCED") || BuiltInToken.Equals(mode.Value, "SIMPLE"));

    /// <summary>Two guides' statements read alike: their texts compared under the collation once a trailing <c>;</c> is set aside.</summary>
    private static bool SameStatement(string x, string y, Database database) =>
        database.Collation.Equals(x.TrimEnd().TrimEnd(';').TrimEnd(), y.TrimEnd().TrimEnd(';').TrimEnd());

    /// <summary>
    /// Whether <paramref name="stmt"/> is one of the statements of
    /// <paramref name="text"/>: written there as a statement of its own — not
    /// part of a longer one, nor a subquery or an <c>INSERT</c>'s source —
    /// spaced exactly as there and compared under the collation, a trailing
    /// <c>;</c> set aside (probed 2026-10-15 against SQL Server 2025: a
    /// procedure's <c>select</c> matches <c>SELECT</c> but not a second space).
    /// </summary>
    private static bool HoldsStatement(string text, string stmt, Database database)
    {
        if (StatementTokensOf(stmt, database, quotedIdentifiers: true) is not { Count: > 0 } wanted || StatementTokensOf(text, database, quotedIdentifiers: true) is not { } body)
            return false;
        while (wanted.Count > 1 && wanted[^1] is Operator { Character: ';' })
            wanted.RemoveAt(wanted.Count - 1);
        var spelled = stmt[wanted[0].StartIndex..wanted[^1].EndIndex];
        for (var i = 0; i + wanted.Count <= body.Count; i++)
        {
            var last = body[i + wanted.Count - 1];
            if (!database.Collation.Equals(text[body[i].StartIndex..last.EndIndex], spelled))
                continue;
            var after = i + wanted.Count < body.Count ? body[i + wanted.Count] : null;
            if (!IsStatementBoundary(after) && after is not ReservedKeyword { Keyword: Keyword.End or Keyword.Else })
                continue;
            if (i > 0 && ContinuesStatement(body, i))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the token at <paramref name="index"/> continues the statement
    /// before it: it follows an opening parenthesis, a comma, an operator or
    /// a set operator, or names an <c>INSERT</c>'s source after its target.
    /// </summary>
    private static bool ContinuesStatement(List<Token> body, int index)
    {
        switch (body[index - 1])
        {
            case Operator { Character: ';' or ')' }:
                break;
            case Operator:
            case ReservedKeyword { Keyword: Keyword.Union or Keyword.All or Keyword.Except or Keyword.Intersect or Keyword.In or Keyword.Exists or Keyword.As or Keyword.Into or Keyword.Insert }:
                return true;
        }
        // INSERT t [ ( columns ) ] SELECT …: walk back over the target to the INSERT.
        var position = index - 1;
        if (body[position] is Operator { Character: ')' })
        {
            var depth = 0;
            for (; position >= 0; position--)
            {
                depth += body[position] switch
                {
                    Operator { Character: ')' } => 1,
                    Operator { Character: '(' } => -1,
                    _ => 0,
                };
                if (depth == 0)
                    break;
            }
            position--;
        }
        while (position >= 1 && body[position] is Name && body[position - 1] is Operator { Character: '.' })
            position -= 2;
        if (position >= 0 && body[position] is Name)
            position--;
        if (position >= 0 && body[position] is ReservedKeyword { Keyword: Keyword.Into })
            position--;
        return position >= 0 && body[position] is ReservedKeyword { Keyword: Keyword.Insert };
    }

    /// <summary>Whether the caller may alter <paramref name="guide"/>: <c>ALTER</c> on its module for an <c>OBJECT</c> guide, or on the database.</summary>
    private static bool MayControlPlanGuide(BatchContext batch, Database database, PlanGuide guide) =>
        PermissionEnforcement.HasDatabasePermission(batch, database, Permission.Alter)
        || (guide.ScopeObjectId is { } objectId && PlanGuideModuleById(database, objectId) is { } module
            && PermissionEnforcement.HasObjectAlter(batch, database, objectId, module.SchemaId));

    /// <summary>The procedure, function or trigger with <paramref name="objectId"/>, which an <c>OBJECT</c> guide names; null when it is gone.</summary>
    private static SchemaObject? PlanGuideModuleById(Database database, int objectId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, procedure) in schema.Procedures)
            {
                if (procedure.ObjectId == objectId)
                    return procedure;
            }
            foreach (var (_, function) in schema.Functions)
            {
                if (function.ObjectId == objectId)
                    return function;
            }
            foreach (var (_, trigger) in schema.Triggers)
            {
                if (trigger.ObjectId == objectId)
                    return trigger;
            }
        }
        return null;
    }

    private static PlanGuide? FindPlanGuide(Database database, Func<PlanGuide, bool> match)
    {
        foreach (var (_, guide) in database.PlanGuides)
        {
            if (match(guide))
                return guide;
        }
        return null;
    }

    /// <summary>Whether a slot is <c>nvarchar(max)</c>, the only type the template procedure's outputs take.</summary>
    private static bool IsNVarcharMax(VariableSlot slot) => slot.DeclaredType is NVarcharSqlType { length: SqlType.MaxLengthSentinel };

    /// <summary>
    /// Refuses to drop or alter <paramref name="module"/> while an
    /// <c>OBJECT</c> plan guide names it (Msg 10513).
    /// </summary>
    internal static void RejectChangeOfPlanGuideModule(Database database, SchemaObject module, string written, bool alter)
    {
        if (database.PlanGuides.IsEmptyLockFree() || FindPlanGuide(database, guide => guide.ScopeObjectId == module.ObjectId) is not { } guide)
            return;
        var kind = module switch
        {
            Schemas.Procedure => "procedure",
            Trigger => "trigger",
            UserDefinedFunction { ObjectTypeCode: "TF" } => "table valued function",
            _ => "function",
        };
        throw SimulatedSqlException.ModuleReferencedByPlanGuide(alter, kind, written, guide.Name);
    }

    /// <summary>Refuses to drop <paramref name="table"/> while a plan guide names one of its triggers (Msg 10526).</summary>
    internal static void RejectDropOfPlanGuideTriggerTable(Database database, HeapTable table, string written)
    {
        if (database.PlanGuides.IsEmptyLockFree())
            return;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, trigger) in schema.Triggers)
            {
                if (ReferenceEquals(trigger.Parent, table) && FindPlanGuide(database, guide => guide.ScopeObjectId == trigger.ObjectId) is { } guide)
                    throw SimulatedSqlException.TableTriggerReferencedByPlanGuide(written, trigger.Name, guide.Name);
            }
        }
    }
}
