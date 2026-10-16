namespace SqlServerSimulator;

// What msdb's SQL Server Agent procedures raise, in SQL Server 2025's numbers
// and wordings (probed 2026-10-16). Each is raised at a line of real's source,
// often from the verification procedure the called one delegates to, which
// Agent sets on the error (AtAgentSite) for the dispatch to attribute.
partial class SimulatedSqlException
{
    /// <summary>
    /// The line of real's source and the procedure that raises an Agent
    /// procedure's error when it isn't the one called — an inner verification
    /// procedure such as <c>sp_verify_job</c> — or null for the called one.
    /// </summary>
    internal (int Line, string? Procedure)? AgentSite;

    /// <summary>
    /// Set on an Agent procedure's error raised under an open <c>TRY</c>,
    /// which real's T-SQL procedure hands to the <c>CATCH</c> before its
    /// <c>RETURN</c>, so the caller's return code keeps its value (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal bool CaughtBeforeReturn;

    /// <summary>Sets <see cref="AgentSite"/> and returns this exception.</summary>
    internal SimulatedSqlException AtAgentSite(int line, string? procedure = null)
    {
        this.AgentSite = (line, procedure);
        return this;
    }

    /// <summary>Msg 14234: a name an Agent procedure looked up isn't one of those the named procedure lists.</summary>
    internal static SimulatedSqlException AgentInvalidReturnedBy(string parameter, string listedBy) =>
        new($"The specified '{parameter}' is invalid (valid values are returned by {listedBy}).", 14234, 16, 1);

    /// <summary>Msg 14235: a step's <c>goto</c> action naming no step or the step itself.</summary>
    internal static SimulatedSqlException AgentGotoStepInvalid(string parameter, int stepId) =>
        new($"The specified '{parameter}' is invalid (valid values are greater than 0 but excluding {stepId}).", 14235, 16, 1);

    /// <summary>Msg 14261: an Agent object's name is already taken.</summary>
    internal static SimulatedSqlException AgentAlreadyExists(string parameter, string value) =>
        new($"The specified {parameter} ('{value}') already exists.", 14261, 16, 1);

    /// <summary>Msg 14262: an Agent object a parameter names doesn't exist (or isn't visible to the caller).</summary>
    internal static SimulatedSqlException AgentDoesNotExist(string parameter, string value) =>
        new($"The specified {parameter} ('{value}') does not exist.", 14262, 16, 1);

    /// <summary>Msg 14266: a parameter outside the values an Agent procedure lists.</summary>
    internal static SimulatedSqlException AgentInvalidValue(string parameter, string validValues) =>
        new($"The specified '{parameter}' is invalid (valid values are: {validValues}).", 14266, 16, 1);

    /// <summary>Msg 14232: <c>sp_stop_job</c> given nothing to name a job by.</summary>
    internal static SimulatedSqlException AgentStopJobNeedsIdentifier() =>
        new("Specify either a job_name, job_id, or an originating_server.", 14232, 16, 1);

    /// <summary>Msg 14256 / 14257: a job started or stopped that targets no server.</summary>
    internal static SimulatedSqlException AgentJobHasNoServer(bool start, string jobName, Guid jobId) =>
        new($"Cannot {(start ? "start" : "stop")} the job \"{jobName}\" (ID {jobId.ToString("D").ToUpperInvariant()}) because it does not have any job server or servers defined. Associate the job with a job server by calling sp_add_jobserver.",
            start ? 14256 : 14257, 16, 1);

    /// <summary>Msg 14269: a job already targeted at the server.</summary>
    internal static SimulatedSqlException AgentJobAlreadyTargeted(string jobName, string server) =>
        new($"Job '{jobName}' is already targeted at server '{server}'.", 14269, 16, 1);

    /// <summary>Msg 14270: a job not targeted at the server.</summary>
    internal static SimulatedSqlException AgentJobNotTargeted(string jobName, string server) =>
        new($"Job '{jobName}' is not currently targeted at server '{server}'.", 14270, 16, 1);

    /// <summary>Msg 14274: <c>sp_add_job</c> handed a job id, which only a job an MSX server sends carries.</summary>
    internal static SimulatedSqlException AgentJobFromMsx() =>
        new("Cannot add, update, or delete a job (or its steps or schedules) that originated from an MSX server.", 14274, 16, 1);

    /// <summary>Msg 14275: an originating server other than the local one.</summary>
    internal static SimulatedSqlException AgentOriginatingServerInvalid() =>
        new("The originating server must be either local server or MSX server.", 14275, 16, 1);

    /// <summary>Msg 14278: a schedule whose frequency fields don't fit together, with real's reason.</summary>
    internal static SimulatedSqlException AgentScheduleInvalid(string reason) =>
        new($"The schedule for this job is invalid (reason: {reason}).", 14278, 16, 1);

    /// <summary>Msg 14279: <c>sp_delete_job</c> given nothing to name a job by.</summary>
    internal static SimulatedSqlException AgentDeleteJobNeedsIdentifier() =>
        new("Supply either @job_name, @job_id or @originating_server.", 14279, 16, 1);

    /// <summary>Msg 14280: <c>sp_help_job</c> given both a job and filters.</summary>
    internal static SimulatedSqlException AgentHelpJobMixedArguments() =>
        new("Supply either a job name (and job aspect), or one or more job filter parameters.", 14280, 16, 1);

    /// <summary>Msg 14287: a schedule time whose minutes or seconds are past 59.</summary>
    internal static SimulatedSqlException AgentTimeInvalidPart(string parameter, string part) =>
        new($"The '{parameter}' supplied has an invalid {part}.", 14287, 16, 1);

    /// <summary>Msg 14288: a schedule ending before it starts.</summary>
    internal static SimulatedSqlException AgentEndBeforeStart(string end, string start) =>
        new($"{end} cannot be before {start}.", 14288, 16, 1);

    /// <summary>Msg 14294: neither or both of a job's identifiers.</summary>
    internal static SimulatedSqlException AgentSupplyEither(string first, string second, string what) =>
        new($"Supply either {first} or {second} to identify the {what}.", 14294, 16, 1);

    /// <summary>Msg 14371: a schedule name more than one schedule carries.</summary>
    internal static SimulatedSqlException AgentScheduleNameAmbiguous(string name) =>
        new($"There are two or more schedules named \"{name}\". Specify @schedule_id instead of @schedule_name to uniquely identify the schedule.", 14371, 16, 1);

    /// <summary>Msg 14372: <c>sp_delete_schedule</c> of a schedule jobs still use.</summary>
    internal static SimulatedSqlException AgentScheduleInUse() =>
        new("The schedule was not deleted because it is being used by one or more jobs.", 14372, 16, 1);

    /// <summary>Msg 14373: neither or both of a schedule's identifiers.</summary>
    internal static SimulatedSqlException AgentSupplyScheduleIdentifier() =>
        new("Supply either @schedule_id or @schedule_name to identify the schedule.", 14373, 16, 1);

    /// <summary>Msg 14366: a caller outside <c>sysadmin</c> naming a schedule's owner.</summary>
    internal static SimulatedSqlException AgentScheduleOwnerNeedsSysadmin() =>
        new("Only members of sysadmin role can modify the owner of a schedule.", 14366, 16, 1);

    /// <summary>Msg 14377: attaching a schedule to another login's job outside <c>sysadmin</c>.</summary>
    internal static SimulatedSqlException AgentAttachNotOwner() =>
        new("The schedule was not attached to the specified job. The caller must own the job or the operation must be performed by a sysadmin.", 14377, 16, 1);

    /// <summary>Msg 14391: detaching a schedule from another login's job outside <c>sysadmin</c>.</summary>
    internal static SimulatedSqlException AgentDetachNotOwner() =>
        new("Only owner of a job or members of sysadmin role can detach a schedule.", 14391, 16, 1);

    /// <summary>Msg 14394: changing or deleting another login's schedule outside <c>sysadmin</c>.</summary>
    internal static SimulatedSqlException AgentScheduleNotOwner() =>
        new("Only owner of a job schedule or members of sysadmin role can modify or delete the job schedule.", 14394, 16, 1);

    /// <summary>Msg 14393: starting or stopping another login's job outside <c>sysadmin</c> and <c>SQLAgentOperatorRole</c>.</summary>
    internal static SimulatedSqlException AgentStartStopNotOwner() =>
        new("Only owner of a job or members of role sysadmin or SQLAgentOperatorRole can start and stop the job.", 14393, 16, 1);

    /// <summary>Msg 14500: an alert on no condition.</summary>
    internal static SimulatedSqlException AgentAlertNeedsCondition() =>
        new("Supply either a non-zero message ID, non-zero severity, non-null performance condition, or non-null WMI namespace and query.", 14500, 16, 1);

    /// <summary>Msg 14501: a second alert on a condition one already watches.</summary>
    internal static SimulatedSqlException AgentAlertConditionTaken(string existing) =>
        new($"An alert ('{existing}') has already been defined on this condition.", 14501, 16, 1);

    /// <summary>Msg 14515: a caller outside <c>sysadmin</c> naming another login as a job's owner.</summary>
    internal static SimulatedSqlException AgentOwnerNeedsSysadmin() =>
        new("Only a member of the sysadmin server role can add a job for a different owner with @owner_login_name.", 14515, 16, 1);

    /// <summary>Msg 14524: a category named by both id and name.</summary>
    internal static SimulatedSqlException AgentSupplyEitherCategory() =>
        new("Supply either @category_id or @category_name.", 14524, 16, 1);

    /// <summary>Msg 14525: changing another login's job outside <c>sysadmin</c>.</summary>
    internal static SimulatedSqlException AgentJobOwnedByOther() =>
        new("Only members of sysadmin role are allowed to update or delete jobs owned by a different login.", 14525, 16, 1);

    /// <summary>Msg 14545: step flags the step's subsystem doesn't take.</summary>
    internal static SimulatedSqlException AgentFlagsInvalidForSubsystem(string subsystem) =>
        new($"The @flags parameter is not valid for a job step of type '{subsystem}'.", 14545, 16, 1);

    /// <summary>Msg 14583: a caller outside <c>sysadmin</c> naming a step's database user.</summary>
    internal static SimulatedSqlException AgentDatabaseUserNeedsSysadmin() =>
        new("Only a sysadmin can specify '@database_user_name' parameter.", 14583, 16, 1);

    /// <summary>
    /// Msg 14261 for a notification: the alert already notifies the operator.
    /// </summary>
    internal static SimulatedSqlException AgentNotificationExists(string alert, string op) =>
        new($"The specified notification ('{alert} / {op}') already exists.", 14261, 16, 1);

    /// <summary>
    /// Msg 50000 state 2: <c>sp_add_jobstep_internal</c>'s CATCH re-raising
    /// the Msg 515 its INSERT of a NULL step name met.
    /// </summary>
    internal static SimulatedSqlException AgentStepNameNull() =>
        new("Cannot insert the value NULL into column 'step_name', table 'msdb.dbo.sysjobsteps'; column does not allow nulls. INSERT fails.", 50000, 16, 2);

    /// <summary>
    /// Msg 22022 at severity 16: <c>sp_start_job</c> and <c>sp_stop_job</c>
    /// with the Agent service stopped, which <c>xp_sqlagent_notify</c> sends
    /// past any <c>TRY</c>.
    /// </summary>
    internal static SimulatedSqlException AgentNotRunning() =>
        new(AgentNotRunningText, 22022, 16, 1);

    /// <summary>The text of Msg 22022, which a stopped Agent's notification sends at severity 0 too.</summary>
    internal const string AgentNotRunningText = "SQLServerAgent is not currently running so it cannot be notified of this action.";
}
