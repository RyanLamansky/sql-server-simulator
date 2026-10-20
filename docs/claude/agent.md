# SQL Server Agent's msdb surface

The simulator models the Agent as a SQL Server 2025 Linux server has it before anyone enables it: the service stopped (`sys.dm_server_services`, `Agent XPs` 0), every job, step, schedule, operator, alert and category procedure working against msdb's tables, and nothing ever running.
Probed 2026-10-16 against SQL Server 2025 (RTM-CU7, 17.0.4065.4, Linux).

## The stopped service

A stopped Agent accepts every definition; what it can't take is being told.
Each procedure that would notify the service (`xp_sqlagent_notify`) makes its change and then sends Msg 22022 at severity 0 — *SQLServerAgent is not currently running so it cannot be notified of this action.* — with no procedure and line 0.
Which calls notify is real's own rule, not the obvious one:

- a job targeted at the local server notifies on `sp_update_job` unless the call changed only the description, on `sp_delete_job`, on its first step only (a second step, at any position, sends nothing), on deleting all its steps (`@step_id = 0`) but not one, and on attaching, detaching, updating or force-deleting a schedule it uses;
- `sp_add_jobserver` and `sp_delete_jobserver` always notify, as do `sp_add_alert`, `sp_delete_alert` and the notification pair;
- operators and categories never notify.

`sp_start_job` and `sp_stop_job` run their checks and then reach the service, whose Msg 22022 arrives at severity 16 — sent to the client past any `TRY`, the batch going on and the procedure answering 1 even inside one.
The Agent never starting is the modeled state, so `sysjobhistory`, `sysjobactivity` and `syssessions` stay empty and `sp_help_job` reports every job idle (`current_execution_status` 4), never run (`last_run_outcome` 5) and unscheduled, though an `@execution_status` filter lists none of them, idle included (probed 2026-10-19 against SQL Server 2025).

## Dispatch, errors and return codes

The procedures are C# handlers (`Simulation.Agent*.cs`) that resolve only where real's do: a name naming msdb (`msdb.dbo.sp_add_job`, `msdb..sp_add_job`), or an unqualified or `dbo` name with msdb current — elsewhere the ordinary Msg 2812.
They write msdb's tables through T-SQL run in msdb as its `dbo` (`AgentSql`), so the writes take locks, join the session's transaction and roll back with it, as real's do.

Real's procedures are T-SQL, and their errors behave as such:

- Binding is a T-SQL procedure's: Msg 201 at state 4, Msg 8114 at state 1, the caller's return code unset.
  More arguments than parameters is Msg 8144 whatever their names, ahead of a missing or unknown one (probed 2026-10-20 against SQL Server 2025, as for a user procedure and the master system procedures).
- Each refusal is a `RAISERROR` at a line of real's source, very often in the verification procedure the called one delegates to — `sp_verify_job`, `sp_verify_jobstep`, `sp_verify_schedule`, `sp_verify_job_identifiers`, `msdb.dbo.sp_verify_schedule_identifiers` … — which `ERROR_PROCEDURE()` and the client both report (`SimulatedSqlException.AgentSite`).
  Some inner names arrive schema-qualified and some don't, as real names them — even one verifier per caller: `sp_attach_schedule` and `sp_detach_schedule` call `msdb.dbo.sp_verify_job_identifiers`, the other job procedures the bare name; the called procedure itself is named as the call wrote it (`msdb..sp_delete_alert` included, probed 2026-10-18 against SQL Server 2025), Database Mail's too.
- Outside a `TRY` the procedure goes on to its `RETURN (1)`; under one the error transfers control before the `RETURN`, so the caller's return code keeps its value.
  `sp_add_alert` answers 0 even when it refuses.
- `sp_add_job` of a NULL name is its INSERT's own Msg 515, at its line 148 with the Msg 3621 that follows, and answers 515.
- Msg 229 for a caller without `EXECUTE` is at line 1, naming the called procedure even where the refusal it stands in for would name an inner one.
- `sp_delete_notification` goes on past its verifier: a missing alert or operator is `sp_verify_notification`'s Msg 14262, then its own for the notification it can't find, which is all a pair with none raises (a `TRY` catches the first).

## What is kept, and how

- **Jobs.** A job name is trimmed and unique under msdb's collation; an empty name is taken by `sp_add_job` but names nothing to the identifier check (Msg 14294).
  The description defaults to *No description available.*, an empty one in `sp_update_job` clears it to NULL, and each notification level falls to 0 without its operator.
  `version_number` starts at 1 and rises with every successful `sp_update_job`, step change and step deletion, whether or not anything changed; target servers and schedules leave it alone.
- **Steps.** A step goes in at `@step_id` (the end by default), the steps at and past it moving up, while the `goto` targets of other steps stay where they were; a deleted step moves the later ones down and resets every `goto` naming it or a later step to the default action.
  The subsystem is kept as written (`tsql` stays lower case); a T-SQL step's database defaults to `master`, every other subsystem's database is NULL whatever was given.
  A `goto` naming no step or the step itself is Msg 14235; one naming a step past the end is only the warning Msg 14236.
  The subsystems taken are `sp_enum_sqlagent_subsystems`' six plus CmdExec, PowerShell and the two Analysis Services ones (which then need `@server`); SSIS and ActiveScripting are Msg 14234.
- **Target servers.** Only the local server exists outside a multi-server setup, by `(local)` or its name in any case; `sp_help_jobserver` and the targets aspect report it as `@@SERVERNAME` enlisted on 1998-11-13.
- **Schedules.** msdb ships the data collector's seven, so a user's first schedule is id 8.
  Names need not be unique — `sp_add_schedule` takes a second of the same name, and naming one ambiguously later is Msg 14371.
  A one-time, Agent-start or idle schedule drops its frequency fields, a recurring one without a sub-day unit gets unit 1, and a daily one drops its relative interval and recurrence factor; weekly and monthly keep what they are given.
  `sp_detach_schedule` by name finds only the job's own schedules (Msg 14262), by id any (then Msg 14374 at severity 0, answering 1).
  `sp_update_jobschedule` and `sp_delete_jobschedule` refuse a schedule that exists but isn't the job's with Msg 14374 at severity 16, and a name no schedule carries with Msg 14262 (*Schedule Name* in the update's wording).
  `sp_delete_job` deletes the schedules it leaves unused unless `@delete_unused_schedule = 0`.
- **Descriptions.** `@include_description = 1` writes `sp_get_schedule_description`'s text, its times the stored integers as they are (*Every day at 233000*).
- **Categories.** msdb ships real's 21, user ones numbered from 100; a job category is `LOCAL` or `MULTI-SERVER`, an alert or operator category `NONE`, and the default `LOCAL` with `@class = 'ALERT'` is refused.
  `sp_update_category` renames any of a class's categories, a shipped one included, trimming its names; a name the class already holds, the category's own included, is Msg 14261, and a missing category is the Msg 2786 real's procedure meets formatting its own Msg 14262 (probed 2026-10-19 against SQL Server 2025).
- **`sp_update_operator` is refused**: it belongs to the `Agent XPs` component, off as the stopped Agent leaves it, so once its arguments bind it is Msg 15281 at line 0 with the caller's return code unset (probed 2026-10-19 against SQL Server 2025).
- **Operators, alerts, notifications.** An alert watches exactly one condition (Msg 14500) that no other alert watches (Msg 14501), its response and job checked by `sp_verify_alert` — `@enabled` 0 or 1, a delay of at least 0, an event-description mask 0 to 7, a job with a target server (Msg 14527) — and an empty text stored as NULL; `has_notification` is the notification methods ORed together, recomputed as operators and notifications go.
  `sp_update_alert` lays the arguments given over the alert and runs the same checks, though not the condition-taken one, so two alerts may come to watch one condition; it finds the alert only as written, takes `@occurrence_count` only as 0, refuses a new name an alert holds — the alert's own included — keeps the name for an empty one, and notifies only of a change beyond the counters and dates (probed 2026-10-19 against SQL Server 2025).
  `sp_help_alert` refuses an `@alert_name` or `@alert_id` no alert has (Msg 14262), while a category with none lists nothing.

`sysjobs_view`, `sysschedules_localserver_view` and `sysoriginatingservers_view` are seeded views and `dbo.agent_datetime(@date, @time)` a seeded function reading the integers through a style-120 string, so its out-of-range input is the conversion's own Msg 241, 242 or 8115.

## Permissions

The three Agent roles are seeded in msdb with real's memberships (`SQLAgentOperatorRole` ⊂ `SQLAgentReaderRole` ⊂ `SQLAgentUserRole`) and the procedures check `EXECUTE` as real's grants give it: `SQLAgentUserRole` the job, step, schedule and help procedures and `sp_help_operator` / `sp_help_category`, `SQLAgentOperatorRole` `sp_help_alert` and `sp_purge_jobhistory`, and only a `sysadmin` (or msdb's `dbo`) the operator, alert, notification and category writes.
`SELECT` is granted on `syscategories`, `sysjobs_view` and `sysschedules_localserver_view` to `SQLAgentUserRole`, on `sysalerts`, `sysnotifications` and `sysoperators` to `SQLAgentOperatorRole`; the base tables stay closed to everyone else.

- A member of `SQLAgentUserRole` sees only the jobs and schedules it owns: another login's job is Msg 14262 as if absent, and naming another owner is Msg 14515 (jobs) or 14366 (schedules), even `sa`.
- `SQLAgentReaderRole` sees every job; changing one it doesn't own is Msg 14525, attaching or detaching a schedule Msg 14377 / 14391, a schedule it doesn't own Msg 14394.
- `SQLAgentOperatorRole` may also enable and disable any job (`sp_update_job` with `@enabled` alone) and start or stop any (Msg 14393 otherwise) — a start checks the caller before the job's server, a stop the server first.
- Target servers have no ownership check at all: a reader may remove another login's job's target.

## Divergences

- msdb's Agent tables are heaps here, so a bare `SELECT` reads them in insertion order where real's clustered indexes read `sysjobs` in `job_id` order and `sysjobsteps` by job and step; the procedures order their own results as real's do.
- A system message an alert names is assumed to exist below 50000 (sys.messages holds only `sp_addmessage`'s user messages here); real refuses a number its catalog lacks, such as 1.
  Both take RAISERROR's own 50000, which real's catalog lists no row for.
- The data collector's schedules are dated by the simulation's creation where real's carry the instance's install date.

## Not modeled yet

- Starting the Agent, and with it running a job's T-SQL steps, writing history and activity, schedules' next run dates, and the `$(ESCAPE_SQUOTE(…))` tokens.
- `sp_add_proxy` and the proxy procedures (`sysproxies` is seeded empty, and a step naming a proxy is Msg 14262) and the multi-server (MSX / TSX) procedures.
- The procedures as catalog objects: `msdb.sys.procedures`, `OBJECT_ID('msdb.dbo.sp_add_job')` and `sp_helptext` don't list them.
- `sp_stop_job @originating_server` naming the local server, which would stop every job it owns, is a `NotSupportedException`.
