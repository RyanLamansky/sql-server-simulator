# Service Broker

Message types, contracts, queues, services, routes, remote service bindings and conversation priorities, with the catalog views over them, and the dialogs that run between services.
Everything here was probed 2026-10-16 against SQL Server 2025 (17.0.4065.4) within one database unless a line says otherwise.

## Where the state lives

- **Queues are schema objects** (`ServiceQueue`, type `SQ`) in `Schema.Queues`, sharing the object namespace: `sys.objects`, `OBJECT_ID`, `OBJECTPROPERTY(…, 'IsQueue')`, object-class `GRANT`s and the Msg 2714 / 3705 collisions all come from that.
- **Everything else is database-scoped** on `DatabaseBroker` (`Database.ServiceBroker`), created on first use together with the objects real ships, so a database no statement asks about carries none of it.
  `Database.ServiceBrokerIfUsed` reads it without creating it; a path that only needs to know whether anything is there uses that.
- **Conversations are the simulation's** (`BrokerRuntime`, `Simulation.Broker`), not a database's: endpoints, conversation groups and held messages across every database, under one gate that a `WAITFOR (RECEIVE …)` waits on.
  A queue's messages sit on `ServiceQueue.Messages` under that same gate.
  Every change a statement makes is logged on its undo log as a broker entry (`UndoLog.RecordBrokerChange`), so a rollback reverses it and a commit publishes it; until then another session doesn't see it.
- Message type, contract and service names compare **as written** (`Latin1_General_BIN`), whatever the database's collation, so `M1` and `m1` are two message types; routes, bindings and priorities are `sysname`s in the database's collation, and a queue's name follows the database like any object's.

## What every database ships

Fourteen message types (`http://schemas.microsoft.com/SQL/ServiceBroker/Error` = 1 … `DEFAULT` = 14), six contracts (`DEFAULT` = 6), three services over three queues (`QueryNotificationErrorsQueue`, `EventNotificationErrorsQueue`, `ServiceBrokerQueue` — fixed object ids, different in `master`), and the `AutoCreatedLocal` route (65536).
msdb adds Database Mail's `SendMail` / `SendMailStatus` types and contract, `InternalMailService` / `ExternalMailService` over their activated queues, and `syspolicy_event_listener` — what SMO's `IsMailHost` looks for.
A user object's id starts at 65536 per class (65537 for routes).
None of the shipped objects can be dropped or altered — Msg 9663 at a state per statement, a shipped queue's drop Msg 3716 for the service it serves — but `AutoCreatedLocal` is an ordinary route that `DROP ROUTE` removes.

## DDL

- `CREATE MESSAGE TYPE … [AUTHORIZATION owner] [VALIDATION = NONE | EMPTY | WELL_FORMED_XML | VALID_XML WITH SCHEMA COLLECTION x]`, `ALTER MESSAGE TYPE … VALIDATION = …`.
  An unbracketed `DEFAULT` is Msg 156; a missing collection Msg 15151.
- `CREATE CONTRACT … ( type SENT BY INITIATOR | TARGET | ANY [, …] )`; a type named twice is Msg 8403.
  There is no `ALTER CONTRACT`.
- `CREATE QUEUE … [WITH STATUS, RETENTION, ACTIVATION (STATUS, PROCEDURE_NAME, MAX_QUEUE_READERS, EXECUTE AS SELF | OWNER | 'user'), POISON_MESSAGE_HANDLING (STATUS)] [ON filegroup | [DEFAULT]]`, commas between options optional.
  A `CREATE`'s activation must name all three of procedure, readers and `EXECUTE AS`, else Msg 102 at the queue's name; a missing procedure is Msg 2812 state 64, readers past 32767 Msg 187, an unknown filegroup Msg 1921 state 11.
  An activation with no `STATUS` is on; `SELF` stores the creating principal and `OWNER` -2 in `execute_as_principal_id`; the procedure is stored as `[schema].[name]` and survives the procedure's drop.
  `ALTER QUEUE` takes the same options plus `ACTIVATION (DROP)`, `REBUILD`, `REORGANIZE` and `MOVE TO`, the last three accepted with nothing to do.
- `CREATE SERVICE … ON QUEUE q [( contract [, …] )]`; `ALTER SERVICE … [ON QUEUE q] [( ADD | DROP CONTRACT c [, …] )]`.
  A contract named twice, or added when present, is Msg 8404; dropping one the service doesn't carry is Msg 15151.
- `CREATE / ALTER ROUTE … WITH [SERVICE_NAME, BROKER_INSTANCE, LIFETIME,] ADDRESS [, MIRROR_ADDRESS]`: no address is Msg 102 state 22 at `WITH`, a malformed one Msg 9744.
- `CREATE / ALTER REMOTE SERVICE BINDING … TO SERVICE 's' WITH USER = u [, ANONYMOUS = ON | OFF]`.
- `CREATE / ALTER BROKER PRIORITY … FOR CONVERSATION [SET (CONTRACT_NAME, LOCAL_SERVICE_NAME, REMOTE_SERVICE_NAME, PRIORITY_LEVEL)]`: a level outside 1 to 10 is Msg 33302, a second priority with the name or the same three properties Msg 33303.
- `ALTER SCHEMA … TRANSFER` moves a queue, its object id and its services' binding kept, and `sp_rename` renames one; a queue real ships is Msg 15349 to the first and Msg 15001 to the second.
- `DROP` of each takes neither `IF EXISTS` nor a list (Msg 156 at `IF`); a message type a contract names, a contract or queue a service names, is Msg 3716 (states 10, 11, 14).
- The `CREATE`s are Msg 2714 at their own state when the name is taken (message type 10, contract 11, service 13, queue 14, route 16), and Msg 262 without their permission (a queue state 18, the others 17).
- Every statement raises its DDL event (`DDL_SSB_EVENTS` and its children; `DDL_BROKER_PRIORITY_EVENTS`), with no `SchemaName` but for a queue, and rolls back with its transaction.

A queue is read with `SELECT` under the `SELECT` permission and refuses every other DML: `INSERT` / `UPDATE` / `DELETE` are Msg 416, `TRUNCATE TABLE` Msg 4708, `DROP TABLE` Msg 3705, `ALTER TABLE` Msg 4909.
Its rowset is fifteen columns, `status` through `message_enqueue_time`.

`GRANT` / `DENY` / `REVOKE` reach `SERVICE::`, `CONTRACT::`, `MESSAGE TYPE::`, `ROUTE::` and `REMOTE SERVICE BINDING::` (classes 17, 16, 15, 19, 18), each taking the permissions `sys.fn_builtin_permissions` lists for its class, and a queue takes `RECEIVE` and `SELECT` where every other DML permission is Msg 4606.

## Conversations

- `BEGIN DIALOG [CONVERSATION] @h FROM SERVICE s TO SERVICE 'name' [, 'instance' | 'CURRENT DATABASE'] [ON CONTRACT c] [WITH RELATED_CONVERSATION = @h | RELATED_CONVERSATION_GROUP = @g, LIFETIME = n, ENCRYPTION = ON | OFF]` creates only the initiator's endpoint, `STARTED_OUTBOUND`, in a new conversation group unless one is related (a group id no group has yet names the new one).
  The far service is a string or a variable — `NULL` is Msg 156 — and the lifetime 1 to 2147483647 seconds (Msg 8411); a missing `FROM` service or contract is Msg 208 at state 72 or 73, and a NULL target Msg 8421.
- `SEND ON CONVERSATION {@h | (@h, …)} [MESSAGE TYPE t] [(body)]` converts the body to `varbinary(max)` — an `xml` one as its UTF-16 text behind a byte order mark, an empty one as no body — and refuses a NULL one (Msg 8433), a type the contract lacks (8431) or the other side sends (8432), a missing type (208 state 74), and an ended or errored endpoint (8429, naming its state).
  Delivery within the instance is **in the sending transaction**: the first message routes the dialog, creates the target's endpoint and moves both to `CONVERSING`, and the message is on the target's queue, visible to the sender's own session at once and to others at commit.
- Routing reads the initiator's database's routes: one naming the far service wins over one naming none, and only a `LOCAL` address delivers.
  A `LOCAL` route finds the service in the initiator's database, else in exactly one other database of the simulation, so a dialog between databases works — from a `TRUSTWORTHY` database only: the message is enqueued there as the sending database's owner, so from any other it waits on the transmission queue with real's `Error: 916, State: 3` status until `SET TRUSTWORTHY ON` delivers it (probed 2026-10-16 against SQL Server 2025).
- What can't be delivered waits on the sender's `sys.transmission_queue` with real's `transmission_status`: no route, a route that isn't local, a missing target service, the sender's database's broker disabled (delivered by `ENABLE_BROKER`), a disabled target queue (delivered by `ALTER QUEUE … STATUS = ON`), and — the trap a first dialog meets — **`ENCRYPTION = ON`, the default, in a database without a master key**, which real's local broker holds for the session keys it can't make.
- A message failing its target's validation, or a contract the target service doesn't list, errors the conversation: the sender gets an Error message — -8408, -8430 (an `EMPTY` type's body on a first message), -9615 (XML parse or schema failure, with the parser's own text) — and moves to `ERROR`; on a later message the target gets -8499 or -9615 and the sender -8462.
  The failed message and every later one are held, "Service Broker received an error message on this conversation…".
- `RECEIVE [TOP (n)] columns FROM queue [INTO @t] [WHERE conversation_handle | conversation_group_id = x]` takes the ready messages of one conversation group no other transaction holds — the first in `queuing_order` — locks the group for its transaction, and returns them, assigns them (`@v = column`, never mixed with returned columns: Msg 8447), or inserts them.
  A column alias is `AS` or juxtaposition; `alias = column` is Msg 102, and so is any other `WHERE`.
  The `WHERE` operand is a variable or a character literal, anything else (`NULL`, a call, a parenthesized value, a number) Msg 102 or 156; one that can't compare with a `uniqueidentifier` is a compile error, a handle's reported twice (probed 2026-10-20 against SQL Server 2025).
  A handle must name a conversation of the database (Msg 8426), though another queue's receives nothing, while a group naming none receives nothing; a NULL is Msg 8418 or 8420 — each at state 20 with Msg 3621 after it.
  A `TRY` catching a `RECEIVE`'s error gets its empty result set first, where the other conversation statements report no count.
  A rollback puts the messages back; a queue with poison-message handling is disabled by the fifth `RECEIVE` in a row that a rollback undid, which the next `RECEIVE` meets as Msg 9617.
- `WAITFOR (RECEIVE …) [, TIMEOUT ms]` and `WAITFOR (GET CONVERSATION GROUP @g FROM q) [, TIMEOUT ms]` wait on the broker's gate, a cancel or `CommandTimeout` ending them, and record `BROKER_RECEIVE_WAITFOR` (which `sys.dm_exec_requests` reports); at the timeout `RECEIVE` returns its empty set and `GET` a NULL group.
- `END CONVERSATION @h` sends the far side an EndDialog (`DISCONNECTED_INBOUND`), or with `WITH ERROR = n DESCRIPTION = 'text'` an Error carrying them (`ERROR`, n from 1, Msg 8165 state 3); the initiator's endpoint then goes, a target's stays `CLOSED`, and the endpoint's messages leave its queue and the transmission queue.
  A dialog that has sent nothing (`STARTED_OUTBOUND`) has no target to tell, so its end, an error included, only removes the initiator.
  `WITH CLEANUP` drops the endpoint telling no one, a `CLOSED` target's included, which every other statement treats as not found (probed 2026-10-20).
- `BEGIN CONVERSATION TIMER (@h) TIMEOUT = n` puts a DialogTimer message (sequence -2) on the endpoint's own queue n seconds later; an expired `LIFETIME` errors both sides with -8489.
  Neither runs on a clock of its own: both fire when a statement next reads the broker's state.
- `MOVE CONVERSATION @h TO @g` moves an endpoint and its queued messages to another group of its service (Msg 8439 otherwise).
- `DROP SERVICE` closes its endpoints and errors their far sides with -8470.
- Retention keeps a queue's sent messages (status 3) and received ones (status 0) until the conversation ends.
- The handle statements' not-found is Msg 8426 at a state per statement (`END` 1, `MOVE` 2, timer 3, `SEND` 4, `RELATED_CONVERSATION` 6) and a NULL handle Msg 8418.
- Error message bodies are real's XML document (`<Error xmlns="http://schemas.microsoft.com/SQL/ServiceBroker/Error"><Code>…</Code><Description>…</Description></Error>`) in UTF-16 behind a byte order mark, sequence -1.
- `BEGIN DIALOG` and `RECEIVE` need `RECEIVE` on the queue (Msg 1088 states 41 and 42), `SELECT` on a queue the `SELECT` permission.

## Activation

- A message committed to a queue whose activation is on starts its procedure on a background session of its own (`Simulation.NoteQueueArrival`), in the queue's database, as the activation's principal: dbo for `OWNER` and for `SELF` run by dbo, otherwise `EXECUTE AS USER`, whose `EXECUTE` on the procedure real checks.
  `@@NESTLEVEL` reads 1 in it, as on real.
- One reader starts at once; another, up to `MAX_QUEUE_READERS`, only once the running ones have had five seconds, as real's queue monitor ramps up.
- A reader that ends having received something runs again while ready messages remain; one that received nothing leaves the queue `NOTIFIED`, activated again only once a `RECEIVE` takes from it — or `ALTER QUEUE` turns activation on again, which also starts a reader for messages already waiting.
- Every error and message the procedure raises goes to the error log as real writes it: `The activated proc '[dbo].[p]' running on queue 'db.dbo.q' output the following:  '…'` under process `spidNNs`.
- `sys.dm_broker_activated_tasks` lists each running reader (spid, database, queue, procedure, principal) and `sys.dm_broker_queue_monitors` reports `RECEIVES_OCCURRING` while one runs, `NOTIFIED`, or `INACTIVE`.

## Query notifications

The server side of `SqlDependency` and `SqlNotificationRequest`, over the TDS endpoint only (the request rides ALL_HEADERS type 1, read into `SimulatedDbConnection.PendingQueryNotification` once a connection reset has been served).
- Each `SELECT` the request runs subscribes (`Simulation.SubscribeQuery`): a query real won't watch — a `*`, a table not named `schema.table`, `TOP`, `DISTINCT`, a subquery, an outer join, a set operator, an aggregate without `GROUP BY`, `COUNT` (where `COUNT_BIG` belongs), a nondeterministic function, a temporary table — gets the `subscribe` / `statement` / `invalid` notification at once, id 0; any other is a `sys.dm_qn_subscriptions` row.
- The first committed write to a watched table (`insert`, `update`, `delete`, `truncate`, hooked at `IndexUsageAccounting.NoteWrite` and `TRUNCATE`) sends the `change` / `data` notification naming the subscription and ends it.
- A notification is real's `<qn:QueryNotification … id type source info database_id sid><qn:Message>…</qn:Message></qn:QueryNotification>` in UTF-16 behind a byte order mark, on a dialog of its own from the database's `QueryNotificationService` under `PostQueryNotification`, to the service the request names (`service="…";local database=…`, the quotes `SqlDependency` writes taken off).
- `SqlDependency.Start`'s queue, service and activation procedure, and its listener's timer-and-`WAITFOR (RECEIVE …)` loop, run unchanged.

## The broker instance

Each database carries its own `service_broker_guid` (`Database.ServiceBrokerGuid`), which `far_broker_instance` and `DBCC DBINFO` report; `master` and `model` carry the empty one.
`ALTER DATABASE … SET` and `RESTORE … WITH` share the switches (`Simulation.TrySwitchBroker`):

- `ENABLE_BROKER` enables it and delivers what its disabled broker held; `DISABLE_BROKER` disables it.
- `NEW_BROKER` draws a new broker id and drops every endpoint, group and queued or held message of the database without a word, then enables it.
- `ERROR_BROKER_CONVERSATIONS` moves every endpoint to `ERROR` with Msg -9719's Error ("The database for this conversation endpoint is attached or restored.") on its own queue, then enables it.
- Enabling a broker whose id another database's enabled broker carries — a restored copy beside its original — is refused: `ALTER DATABASE` with Msg 9772 then 5069, a restore with Msg 9773's notice (naming the database by id, ahead of Msg 3014) and the broker left disabled, its conversations untouched.

## Backup and restore

A backup carries the broker id, every broker object under its id (queues with their settings and messages, `AutoCreatedLocal` as altered or dropped) and the database's conversations: endpoints, groups and the messages its transmission queue holds, what an open transaction made left out.
A restore replaces whatever the database held with that, the conversations under their original handles — a copy restored beside its original shares them, each database's under its own (`BrokerKey`) — and comes back with its broker **disabled** and its id unchanged, over itself or as a copy, unless it names a switch.
The switch applies as the database recovers, a `RESTORE … WITH RECOVERY` alone included; with `NORECOVERY` it is Msg 3031 (`'norecovery' conflicts with option(s) 'broker_option'`), and a second, different switch is Msg 102 at it, the same one twice accepted.

## Event notifications

`CREATE EVENT NOTIFICATION n ON { SERVER | DATABASE | QUEUE q } [WITH FAN_IN] FOR event [, …] TO SERVICE 'svc', { 'broker_instance' | 'current database' }` and `DROP EVENT NOTIFICATION n [, …] ON …` (`Simulation.EventNotifications.cs`); there is no `ALTER` (Msg 102 at `NOTIFICATION`).
A database's and its queues' notifications sit on `Database.EventNotifications`, the server's on `Simulation.ServerEventNotifications`, each an array replaced whole, so the DDL path's test for one costs an array-length read when there is none.

- The events come from `sys.event_notification_event_types` (`EventNotificationEventTypes`: the DDL catalog plus the queue events and the SQL Trace classes with their `TRC_*` groups, 432 rows): a group stands for its leaves, each listed in `sys.events` / `sys.server_events` with the group named, and an event two items name is Msg 1095 naming the first leaf repeated.
- `ON QUEUE` takes only `QUEUE_ACTIVATION` and `BROKER_QUEUE_DISABLED`, `ON DATABASE` only the database-level DDL events, `ON SERVER` every DDL event and the trace classes but not the queue events; anything else is Msg 1098, an unknown name Msg 1084, all three refused as the batch compiles.
- A name is unique per parent — the database and each queue apart — Msg 2714 state 21 otherwise; notifications aren't in `sys.objects`.
- `'current database'` reads as the database's broker id, which `broker_instance` then reports, and in `master` or `model` is Msg 2116; any other string is accepted as written.
- The permission is `CREATE DATABASE DDL EVENT NOTIFICATION` on a database, `ALTER` on the queue, and on the server `CREATE DDL EVENT NOTIFICATION` for DDL events and `CREATE TRACE EVENT NOTIFICATION` for trace ones, each Msg 2104 state 5; a server notification needs its login to have a user in msdb (Msg 2102 state 2), where its dialog begins, and the checks run after `'current database'`'s.
  Msg 2104, 2102 and 2116 end the batch and roll back as under `XACT_ABORT`, dooming a transaction when caught, where a name taken (Msg 2714 state 21) ends only its statement (probed 2026-10-20 against SQL Server 2025).
- `WITH FAN_IN` shows as `creator_sid` (the creator's sid); `principal_id` is the creator, a server notification's its login's server principal.
- `DROP` drops the whole list or none: a missing name, or one the principal may not drop, is Msg 15151, which ends the batch.
- `CREATE` and `DROP` of a database's or queue's notification raise `CREATE_EVENT_NOTIFICATION` / `DROP_EVENT_NOTIFICATION`, whose `EVENTDATA()` names the target (`TargetSchemaName`, `TargetObjectName`, `TargetObjectType` `Database` or `SERVICE QUEUE`); a server's raises neither.

Each notification sends on one dialog of its own from its home database's `EventNotificationService` (its database, msdb for a server's) under `PostEventNotification`: the initiator's endpoint is `STARTED_OUTBOUND` from the `CREATE`, the first event routes it to the service in the database carrying the broker instance (an enabled one ahead of a copy sharing the id) and opens the target's endpoint, and every event is an `EventNotification` message, its body the `EVENT_INSTANCE` in UTF-16 behind a byte order mark, sequence numbers counting up.
`DROP` ends the dialog with an EndDialog to the far side (`DISCONNECTED_INBOUND`); `NEW_BROKER` or a restore ending the conversations makes the next event begin another dialog.
What the service can't take waits on the home database's transmission queue as a dialog's messages do — no such service, a disabled queue, the home database's broker disabled — and a service without the contract errors the dialog with Msg -8408.

- **DDL events** send the `EVENTDATA()` document the DDL triggers build (see [`triggers.md`](triggers.md)), once the statement commits: a rolled-back `CREATE TABLE` tells no one, and in a transaction the message waits for its commit.
  A server's notification takes a database-level event from every database.
- **`QUEUE_ACTIVATION`** tells once that ready messages wait on the queue — as one commits to it, or at the `CREATE` when some already wait — and again only after a `RECEIVE` takes from it.
- **`BROKER_QUEUE_DISABLED`** tells as the fifth `RECEIVE` in a row a rollback undid disables a poison-handling queue, which is disabled from that rollback on.
- Both queue events carry `EventType`, `PostTime`, `SPID`, `ServerName`, `LoginName`, `UserName`, `DatabaseName`, `SchemaName`, `ObjectName` and `ObjectType` `QUEUE`.
- **`AUDIT_LOGIN`** / **`AUDIT_LOGOUT`** tell as a session opens (a pooled reset `EventSubClass` 2) and as one that logged in ends, with the trace's columns: the session's options as `TextData`, host, application, client process id, login, sid, packet size, a duration in microseconds, `EventSequence` counting the server's trace events.
- **`DEADLOCK_GRAPH`** tells as the deadlock monitor picks a victim, from session 3: the `xml_deadlock_report` graph in the trace's form — a `<deadlock-list>` holding a `<deadlock victim="…">`, every line within a level deeper and no `<victim-list>` — as `TextData`'s markup.

## Catalog views

`sys.service_message_types`, `sys.service_contracts`, `sys.service_contract_message_usages`, `sys.message_type_xml_schema_collection_usages`, `sys.services`, `sys.service_contract_usages`, `sys.service_queue_usages`, `sys.service_queues`, `sys.routes`, `sys.remote_service_bindings`, `sys.conversation_priorities`, `sys.conversation_endpoints`, `sys.conversation_groups`, `sys.transmission_queue` and `sys.dm_broker_queue_monitors` (one `INACTIVE` row per queue with an activation procedure).
A queue's `SELECT` lists its messages in its clustered index's order — status, group, priority, conversation, queuing order — as real's scan does.
`sys.event_notifications`, `sys.server_event_notifications`, `sys.events` and `sys.server_events` (which list the triggers' events too, `is_trigger_event` 1, in `object_id` order) and `sys.event_notification_event_types` project the [event notifications](#event-notifications); a principal sees a database's notifications it owns, all of them with `ALTER ANY DATABASE EVENT NOTIFICATION`, and a queue's where it may alter the queue, and a login the server's it created, all of them with `ALTER ANY EVENT NOTIFICATION`.

## Divergences

- **A `QUEUE_ACTIVATION` event reports session 17** and `sa` / `dbo`, standing for real's queue monitor, a system task whose own session id varies; a trace event's `BinaryData` is twelve zero bytes, as `login`'s `options` field is, and its `Reads`, `Writes` and `CPU` zero.
- **A notification's events go on its one dialog** whatever happens to the far side: an EndDialog or Error the target sends back isn't read, where real's initiator would end the dialog and send the next event on a new one.
- **Delivery failures reach the sender as its transaction commits**, where real's broker reports them a moment later: a `SEND` right after a failing one in the same batch is refused here (Msg 8429, the endpoint already `ER`) where on real it is usually accepted and held.
- **Reads never wait**: a `SELECT` of a queue, or of the conversation views, reads the committed state (plus the session's own changes) where real's would wait on another transaction's uncommitted `SEND` or `RECEIVE` (Msg 1222 under a lock timeout).
- Endpoints carry no session keys, security timestamp or out-of-order bookkeeping, and every priority is 5: `CREATE BROKER PRIORITY` is catalog-only.
- A target's `principal_id` is always dbo.
- **A reader that failed is not retried**: real's queue monitor starts it again every five seconds while messages wait (an `EXECUTE` denied, say), where here it stays `NOTIFIED` until a `RECEIVE` or an `ALTER QUEUE`.
- Inside an activated procedure real reports `@@TRANCOUNT` one higher than its own transactions account for, and `@@SPID` as 1; here both read as an ordinary session's.
- A query notification's dialog is begun as it fires, where real begins it as the query subscribes (its `STARTED_OUTBOUND` endpoint visible meanwhile).
- The DONE token a conversation statement closes with: real's own codes aren't captured, so `RECEIVE` closes as a `SELECT` and the rest send none.

- **Ids are spent only on success**: real spends one on a `CREATE` that fails after allocating it (a duplicate contract, a missing message type), so a later object's id can run ahead of the simulator's.

## Not modeled yet

- The trace event classes but `AUDIT_LOGIN`, `AUDIT_LOGOUT` and `DEADLOCK_GRAPH`: a notification takes them, and nothing sends them.
  Real's `ERRORLOG` carries the error log line under its error number and the session's columns, `LOCK_DEADLOCK` the lock's (probed 2026-10-16 against SQL Server 2025).
- `WITH FAN_IN`'s collapsing of one owner's notifications to one service into a single send.
- Remote delivery: a route to another server's address holds its messages for ever, and `sys.dm_broker_connections` / `sys.dm_broker_forwarded_messages` aren't registered.
- The `SEND` permission on the target service: within one database real delivers without it (probed 2026-10-16 against SQL Server 2025), and between databases the sending database's owner's user in the target is what real would check, which a `sa`-owned probe couldn't reach.
- A query notification's timeout: a subscription lives until its table changes, and real's `timeout` notification is never sent; the query rules are read off the statement's tokens, so a shape between the listed ones (a comma-joined `FROM`, a view) is judged loosely.
- Query notifications in-process: the ADO.NET surface has no `Notification` property.
