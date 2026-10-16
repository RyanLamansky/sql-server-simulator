# Database Mail

The simulator models Database Mail as a SQL Server 2025 Linux server has it: `Database Mail XPs` off as the server ships, msdb's `sysmail_*` tables and views, every configuration procedure, and `sp_send_dbmail` queueing items that an external program then fails to deliver.
No mail ever leaves the simulator, so every item ends as real's ends against a mail server it can't reach.
Probed 2026-10-16 against SQL Server 2025 (RTM-CU7, 17.0.4065.4, Linux), with accounts naming hosts under `.invalid`.

## Where it lives

- The thirteen tables and six views are seeded in msdb (`Simulation.MsdbMail.cs`) in real's column shapes, with the `SMTP` row of `sysmail_servertype` and the seven settings of `sysmail_configuration`.
  The tables are heaps without real's keys; the procedures raise the key violations themselves, under real's constraint names.
- The procedures (`Simulation.DatabaseMail.cs`, `Simulation.DatabaseMailSend.cs`) ride the Agent procedures' msdb dispatch, argument binding and error attribution (see [`agent.md`](agent.md#dispatch-errors-and-return-codes)): resolved only through a name naming msdb, writing through T-SQL run as msdb's `dbo`, so every write joins the caller's transaction, and each refusal raised at its line of real's source, often inside `sysmail_verify_profile_sp`, `sysmail_verify_account_sp`, `sysmail_verify_principal_sp` or `sysmail_verify_accountparams_sp`.
- Whether the queue is stopped and whether the external program is running are the simulation's (`Simulation.databaseMailStopped`, `databaseMailProcessRunning`), outside any transaction.

## The `Database Mail XPs` gate

With the option off, `sp_send_dbmail`, `sysmail_start_sp`, `sysmail_stop_sp` and `sysmail_help_status_sp` are Msg 15281 naming `dbo.<procedure>`, at line 0, after their arguments bind and leaving the caller's return code unset; `xp_sysmail_activate`, `xp_sysmail_attachment_load` and `xp_sysmail_format_query` are the same message naming `sys.`, at line 1, as `xp_cmdshell`'s.
Every other procedure, and every table and view, works with the option off.
Turning it on finds the queue started.

## Accounts, profiles and grants

- **Accounts.** `sysmail_add_account_sp` checks its parameters first (a NULL server type is Msg 14614, a user name beside default credentials 14666, a password without a user name 14615, each answering 1), then writes the account (a duplicate name under msdb's collation is Msg 2627, a NULL name or address 515, answering 2) and its server row (a type `sysmail_servertype` lacks is Msg 547, a NULL port 515, answering 4, the account taken back with it).
  A refused write spends an identity value, as real's does.
  The server defaults to the instance's name, port 25, `SMTP`; a user name is kept in a server credential named by a new GUID, which `sys.credentials` lists and deleting the account drops.
- **Updating an account** keeps whatever is left NULL, except the user name: leaving it out drops the credential unless `@no_credential_change = 1`.
  An id with a name renames the account.
- **Profiles.** A duplicate or NULL profile name is Msg 2627 / 515 answering 0; `sysmail_update_profile_sp` with neither a name nor a description is Msg 14610.
  Deleting a profile takes its accounts and grants with it.
- **Profile accounts.** The profile is verified first (answering 1), then the account (2); a pair added twice is Msg 2627 answering 0, an update needs a sequence number (Msg 14611) and silently changes nothing for a pair that isn't there, and a delete needs one side (Msg 14608) and removes every pair it names.
- **Principal profiles.** A principal is an msdb user or `public`; any other role is Msg 14607.
  A grant is kept by sid: `public` as `0x00`, which `guest`'s sid shares, so a grant to `guest` collides with one to `public` and `sysmail_help_principalprofile_sp` lists `public`'s grants as `guest`'s unless the call names `public`.
  Marking a profile a principal's default clears that principal's other defaults; a NULL `@is_default` is the write's own Msg 515.
- **Settings.** `sysmail_configure_sp` sets any value, NULL included, without checking it; a name that is no setting changes nothing, and the description changes only when given.

## `sp_send_dbmail`

The checks run in real's order, each answering its own return code: the gate, the stopped queue (Msg 14641), the profile — named (`sysmail_verify_profile_sp`, answering 4), else the caller's default private profile, else the default public one (Msg 14636) — and for a caller outside `sysadmin` its access, public or granted to its msdb user (Msg 14607 at line 141); then the five switches that may not be NULL (Msg 14618), the body format, importance and sensitivity (Msg 14626, 14622, 14623, each naming the value upper-cased), something to send (Msg 14624 over body, query, attachments and subject, an empty string counting as none) and someone to send it to (Msg 14624 over the three recipient lists).

- **Attachments** split on `;` and are read through `Simulation.OpenBulkFile`, the server disk the bulk loads read.
  An extension `ProhibitedExtensions` lists is Msg 14630 from `sp_GetAttachmentData`, naming the whole path; a missing file, one larger than `MaxFileSize`, and any existing file named by a caller outside `sysadmin` (a SQL login, which real can't impersonate) are Msg 22051, an extended procedure's error sent past any `TRY`, answering 1.
  Each attachment is stored whole in `sysmail_attachments` under the path as written.
- **The query** runs as the caller in `@execute_query_database`, by default the caller's database.
  Any error it raises, a database that doesn't exist, or a result width below 9 is Msg 22050 sent past any `TRY`, answering 102, whatever `@append_query_error` says.
  Its output follows `sqlcmd`'s layout: a column as wide as its type shows (or its name), numbers and the `date` / `time` / `datetime2` / `datetimeoffset` types right-aligned, NULL cut to the column, `(n rows affected)` after a blank line unless `NOCOUNT` is on, PRINT lines in place, and every line cut into pieces of `@query_result_width`.
  It follows the body after a CRLF, or with `@attach_query_result_as_file = 1` becomes an attachment of UTF-16 with a byte order mark, named `@query_attachment_filename` or `QueryResults` and six random digits.
- The item is written with `sent_status` 0, and its attempt to be delivered follows at once; `Mail (Id: n) queued.` is printed unless `@exclude_query_output = 1`, which is all that switch changes.
  A rollback takes the item and everything its delivery wrote.

## What becomes of an item

Real's external program takes the item from the queue moments after it commits.
Against an unreachable server it logs an error, shows the item `retrying` with a row in `sysmail_send_retries`, waits `AccountRetryDelay` seconds, tries again `AccountRetryAttempts` times, and fails the item, its `sent_date` the last attempt's whole second.
The simulator plays that whole history at once, inside the sender's statement:

- the program's first item logs `DatabaseMail process is started` under the server's `Administrator`;
- a profile without accounts fails the item with one error row naming no item (*the list of accounts which could send this mail is empty*);
- otherwise one error row per attempt names the item and the profile's first account, with the exception report real writes for a host that doesn't resolve, a retry row keeps the retry count, and the item fails with no `sent_account_id`.

`sysmail_stop_sp` and `sysmail_start_sp` log who stopped or started the queue on every call, and a stop shuts down a running program, logging its shutdown; while stopped the mail queue drops out of `sysmail_help_queue_sp`.
`sysmail_delete_mailitems_sp` deletes items with their attachments and retry rows and logs how many it deleted, even none; `sysmail_delete_log_sp` logs nothing.

## Permissions

Only a `sysadmin` (or msdb's `dbo`) runs the configuration procedures; `DatabaseMailUserRole` holds `EXECUTE` on `sp_send_dbmail`, `sysmail_help_status_sp` and `sysmail_delete_mailitems_sp` and `SELECT` on the six views, every other caller being Msg 229.
The views show a caller outside `sysadmin` only the items it sent, their attachments and the log rows naming them, and `sysmail_delete_mailitems_sp` deletes only its own.

## Divergences

- The retries happen at once rather than `AccountRetryDelay` apart, so an item is never seen `unsent` or `retrying` and its log rows carry the sending statement's time.
- Every account's server is unreachable, whatever it names; real reports *No such host is known* only for a name that doesn't resolve, and refusal or a time-out otherwise.
- The program's process id is a fixed stand-in, and it never shuts down on its own after `DatabaseMailExeMinimumLifeTime`.
- `sysmail_help_queue_sp` reports both queues empty and `INACTIVE`, timed by the simulation's start.
- The query's widths and value forms follow the common types; a type outside them takes 256 columns, and `@query_no_truncate` changes nothing.
- The line numbers of a few refusals real wasn't seen raising (an update's principal verification, the help procedures' return codes) follow their siblings'.

## Not modeled yet

- Delivering mail, and with it `sent` items, `sent_account_id`, account failover across a profile's accounts, and `sysmail_send_retries` evolving over time.
- `sysmail_help_admin_account_sp`, the credential procedures (`sysmail_create_user_credential_sp` and its pair), `sysmail_logmailevent_sp`, and the broker activation real's queues run (`sp_sysmail_activate`, `sp_ExternalMailQueueListener`): the Database Mail broker objects ship (see [`service-broker.md`](service-broker.md)) but no message crosses them.
- The procedures as catalog objects: `msdb.sys.procedures` and `OBJECT_ID('msdb.dbo.sp_send_dbmail')` don't list them.
