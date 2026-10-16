namespace SqlServerSimulator;

// The errors sp_create_plan_guide, sp_control_plan_guide,
// sys.fn_validate_plan_guide and sp_get_query_template raise, and the refusal
// to drop or alter a module a plan guide names. Wording, class and state are
// probed 2026-10-15 against SQL Server 2025. Every one the procedures raise
// ends the batch and rolls the transaction back, or dooms it under a TRY, as
// under XACT_ABORT; the module refusal ends only its statement.
partial class SimulatedSqlException
{
    /// <summary>Msg 10501: an <c>@type</c> other than <c>SQL</c>, <c>OBJECT</c> or <c>TEMPLATE</c>.</summary>
    internal static SimulatedSqlException PlanGuideTypeNotAllowed(string name, string type) =>
        Aborting($"Cannot create plan guide '{name}' because type '{type}' provided is not allowed.", 10501, 1);

    /// <summary>Msg 10502: a guide over the statement and scope another guide already covers.</summary>
    internal static SimulatedSqlException PlanGuideMatchesExisting(string name, string existing) =>
        Aborting($"Cannot create plan guide '{name}' because the statement specified by @stmt and @module_or_batch, or by @plan_handle and @statement_start_offset, matches the existing plan guide '{existing}' in the database. Drop the existing plan guide before creating the new plan guide.", 10502, 1);

    /// <summary>Msg 10503: an <c>sp_control_plan_guide</c> operation it doesn't know.</summary>
    internal static SimulatedSqlException PlanGuideOperationNotAllowed(string operation) =>
        Aborting($"Operation '{operation}' is not allowed.", 10503, 1);

    /// <summary>Msg 10504: hints that don't compile as an <c>OPTION</c> clause.</summary>
    internal static SimulatedSqlException PlanGuideHintsIncorrect(string name) =>
        Aborting($"Cannot create plan guide '{name}' because parameter @hints is incorrect. Use N'OPTION ( <query_hint> [ ,...n ] )'.", 10504, 1);

    /// <summary>Msg 10505: an <c>OBJECT</c> guide's module named with more than two parts.</summary>
    internal static SimulatedSqlException PlanGuideModuleNameNotTwoPart(string name, string module) =>
        Aborting($"Cannot create plan guide '{name}' because value '{module}' provided for @module_or_batch is not legal two-part name. Use 'schema_name.object_name'.", 10505, 2);

    /// <summary>Msg 10506: an <c>@stmt</c> holding more than one statement.</summary>
    internal static SimulatedSqlException PlanGuideStatementNotSingle(string name) =>
        Aborting($"Cannot create plan guide '{name}' because parameter @stmt has more than one statement.", 10506, 1);

    /// <summary>Msg 10507: an <c>@stmt</c> that is none of the module's or batch's statements.</summary>
    internal static SimulatedSqlException PlanGuideStatementNotInScope(string name) =>
        Aborting($"Cannot create plan guide '{name}' because the statement specified by @stmt and @module_or_batch, or by @plan_handle and @statement_start_offset, does not match any statement in the specified module or batch. Modify the values to match a statement in the module or batch.", 10507, 1);

    /// <summary>
    /// Msg 10508: <c>sp_control_plan_guide</c> naming a guide the database
    /// doesn't hold, the state telling the operation apart: 1 for
    /// <c>DROP</c>, 2 for <c>ENABLE</c>, 3 for <c>DISABLE</c>.
    /// </summary>
    internal static SimulatedSqlException PlanGuideDoesNotExist(string operation, string name, byte state) =>
        Aborting($"Cannot '{operation}' plan guide '{name}' because it does not exist or you do not have permission. Verify plan guide name and database of current session, and that you have needed permission.", 10508, state);

    /// <summary>Msg 10509: an <c>@stmt</c> that is no statement a plan guide can take, such as an <c>EXEC</c> or a <c>SET</c>.</summary>
    internal static SimulatedSqlException PlanGuideStatementIneligible(string name) =>
        Aborting($"Cannot create plan guide '{name}' because the statement specified by @stmt or @statement_start_offset either contains a syntax error or is ineligible for use in a plan guide. Provide a single valid Transact-SQL statement or a valid starting position of the statement within the batch. To obtain a valid starting position, query the 'statement_start_offset' column in the sys.dm_exec_query_stats dynamic management function.", 10509, 1);

    /// <summary>Msg 10510: a guide name the database already holds.</summary>
    internal static SimulatedSqlException PlanGuideNameExists(string name) =>
        Aborting($"Cannot create plan guide '{name}' because there is already a plan guide with that name in the database. Use a unique name.", 10510, 1);

    /// <summary>
    /// Msg 10513: <c>DROP</c> (state 1) or <c>ALTER</c> (state 2) of a module
    /// an <c>OBJECT</c> plan guide names; <paramref name="kind"/> is real's
    /// word for it — <c>procedure</c>, <c>function</c>, <c>table valued
    /// function</c> or <c>trigger</c>.
    /// </summary>
    internal static SimulatedSqlException ModuleReferencedByPlanGuide(bool alter, string kind, string module, string guide) =>
        new($"Cannot {(alter ? "alter" : "drop")} {kind} '{module}' because it is referenced by plan guide '{guide}'. Use sp_control_plan_guide to drop the plan guide first. Record the plan guide definition for future use if needed.", 10513, 16, alter ? (byte)2 : (byte)1);

    /// <summary>Msg 10515: an <c>OBJECT</c> guide's module that is missing, or no procedure, function or trigger of the database's own.</summary>
    internal static SimulatedSqlException PlanGuideModuleNotFound(string name, string module) =>
        Aborting($"Cannot create plan guide '{name}' because the module '{module}' does not exist or you do not have needed permission.", 10515, 1);

    /// <summary>Msg 10516: a batch, or a statement with its parameters, that doesn't compile.</summary>
    internal static SimulatedSqlException PlanGuideBatchDoesNotCompile(string name) =>
        Aborting($"Cannot create plan guide '{name}' because @module_or_batch can not be compiled.", 10516, 1);

    /// <summary>Msg 10517: a <c>SQL</c> or <c>TEMPLATE</c> guide created without <c>ALTER</c> on the database.</summary>
    internal static SimulatedSqlException PlanGuideCreatePermissionDenied(string name) =>
        Aborting($"Cannot create plan guide '{name}' because you do not have needed permission. Alter database permission required.", 10517, 1);

    /// <summary>Msg 10518: <c>sp_control_plan_guide</c> without <c>ALTER</c> on the guide's module or the database.</summary>
    internal static SimulatedSqlException PlanGuideControlPermissionDenied(string name) =>
        Aborting($"Cannot execute sp_control_plan_guide because of insufficient permissions to control plan guide '{name}'. Alter permission on object referenced by plan guide, or alter database permission required.", 10518, 1);

    /// <summary>Msg 10519: a guide over a statement no hint applies to — a query reading no table, whatever <c>@hints</c> says.</summary>
    internal static SimulatedSqlException PlanGuideHintsNotApplicable(string name) =>
        Aborting($"Cannot create plan guide '{name}' because the hints specified in @hints cannot be applied to the statement specified by either @stmt or @statement_start_offset. Verify that the hints can be applied to the statement.", 10519, 1);

    /// <summary>Msg 10520: a parameter the guide's type requires to be NULL given a value, the state naming the pair.</summary>
    internal static SimulatedSqlException PlanGuideParameterMustBeNull(string name, string type, string parameter, byte state) =>
        Aborting($"Cannot create plan guide '{name}' because @type was specified as '{type}' and a non-NULL value is specified for the parameter '{parameter}'. This type requires a NULL value for the parameter. Specify NULL for the parameter, or change the type to one that allows a non-NULL value for the parameter.", 10520, state);

    /// <summary>
    /// Msg 10521: a parameter the guide's type requires left NULL, the state
    /// naming the pair. A NULL <c>@stmt</c> is state 4, whose message real
    /// fills with the parameter's name where the type goes and its type where
    /// the name goes.
    /// </summary>
    internal static SimulatedSqlException PlanGuideParameterRequired(string name, string type, string parameter, byte state) =>
        Aborting($"Cannot create plan guide '{name}' because @type was specified as '{type}' and the parameter '{parameter}' is NULL. This type requires a non-NULL value for the parameter. Specify a non-NULL value for the parameter, or change the type to one that allows a NULL value for the parameter.", 10521, state);

    /// <summary>Msg 10522: a <c>TEMPLATE</c> guide whose hints are anything but one <c>PARAMETERIZATION</c> hint.</summary>
    internal static SimulatedSqlException PlanGuideTemplateHintsIllegal(string name) =>
        Aborting($"Cannot create plan guide '{name}' because @hints has illegal value. @hints must be OPTION(PARAMETERIZATION FORCED) or OPTION(PARAMETERIZATION SIMPLE) if @type is 'template'.", 10522, 1);

    /// <summary>
    /// Msg 10523: <c>sp_get_query_template</c> over a text that isn't one
    /// statement — state 2 for no statement or one that doesn't parse, 3 for
    /// more than one.
    /// </summary>
    internal static SimulatedSqlException QueryTemplateNotSingleQuery(byte state) =>
        Aborting("Cannot generate query template because @querytext does not contain a valid single query.", 10523, state);

    /// <summary>
    /// Msg 10524: <c>sp_get_query_template</c> over a statement forced
    /// parameterization leaves as written — state 1 when no literal could
    /// become a parameter, 2 when every one that could sits where real keeps
    /// it.
    /// </summary>
    internal static SimulatedSqlException QueryTemplateNotParameterizable(byte state) =>
        Aborting("Cannot parameterize @querytext.", 10524, state);

    /// <summary>Msg 10527: an <c>OBJECT</c> guide over a temporary object.</summary>
    internal static SimulatedSqlException PlanGuideOnTemporaryObject(string name, string module) =>
        Aborting($"Cannot create plan guide '{name}' because the object '{module}' is a temporary object.", 10527, 1);

    /// <summary>Msg 10528: a guide name opening with <c>#</c>.</summary>
    internal static SimulatedSqlException PlanGuideNameInvalid(string name) =>
        Aborting($"Cannot create plan guide '{name}' because its name is invalid. Plan guide name cannot begin with a '#' character.", 10528, 1);

    /// <summary>Msg 1038 state 4: an empty plan guide name, which ends the batch as a compile error does.</summary>
    internal static SimulatedSqlException PlanGuideNameEmpty() =>
        new("An object or column name is missing or empty. For SELECT INTO statements, verify each column has a name. For other statements, look for empty alias names. Aliases defined as \"\" or [] are not allowed. Change the alias to a valid name.", 1038, 15, 4) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 10529: a <c>TEMPLATE</c> guide over a statement another template guide covers.</summary>
    internal static SimulatedSqlException PlanGuideTemplateExists(string name, string existing) =>
        Aborting($"Cannot create plan guide '{name}' because there is already a planguide '{existing}' of @type 'template' on @stmt.", 10529, 1);

    /// <summary>Msg 10538: <c>sys.fn_validate_plan_guide</c> over an id that is no guide of the database's, or NULL.</summary>
    internal static SimulatedSqlException PlanGuideIdNotFound() =>
        Aborting("Cannot find the plan guide either because the specified plan guide ID is NULL or invalid, or you do not have permission on the object referenced by the plan guide. Verify that the plan guide ID is valid, the current session is set to the correct database context, and you have ALTER permission on the object referenced by the plan guide or ALTER DATABASE permission.", 10538, 1);

    /// <summary>Msg 10526: <c>DROP TABLE</c> of a table whose trigger a plan guide names.</summary>
    internal static SimulatedSqlException TableTriggerReferencedByPlanGuide(string table, string trigger, string guide) =>
        new($"Cannot drop table '{table}' because its trigger '{trigger}' is referenced by plan guide '{guide}'. Use sp_control_plan_guide to drop the plan guide first. Record the plan guide definition for future use if needed.", 10526, 16, 1);

    /// <summary>
    /// <paramref name="errors"/> sent as one report that ends the batch as
    /// under <c>XACT_ABORT</c> — a compile's errors and the plan-guide refusal
    /// that follows them — whose first entry is what a <c>CATCH</c> and
    /// <c>@@ERROR</c> read (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException AbortingAggregate(List<SimulatedSqlException> errors)
    {
        var aggregate = Aggregate(errors);
        return new SimulatedSqlException(aggregate.Message, [.. aggregate.Errors])
        {
            AbortsAsUnderXactAbort = true,
            diagnosticsResolved = aggregate.diagnosticsResolved,
            CatchReadsFirstEntry = true,
        };
    }

    private static SimulatedSqlException Aborting(string message, int number, byte state) =>
        new(message, number, 16, state) { AbortsAsUnderXactAbort = true };
}
