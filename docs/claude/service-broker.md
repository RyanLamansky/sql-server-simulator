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
  A `LOCAL` route finds the service in the initiator's database, else in exactly one other database of the simulation, so a dialog between databases works.
- What can't be delivered waits on the sender's `sys.transmission_queue` with real's `transmission_status`: no route, a route that isn't local, a missing target service, the sender's database's broker disabled (delivered by `ENABLE_BROKER`), a disabled target queue (delivered by `ALTER QUEUE … STATUS = ON`), and — the trap a first dialog meets — **`ENCRYPTION = ON`, the default, in a database without a master key**, which real's local broker holds for the session keys it can't make.
- A message failing its target's validation, or a contract the target service doesn't list, errors the conversation: the sender gets an Error message — -8408, -8430 (an `EMPTY` type's body on a first message), -9615 (XML parse or schema failure, with the parser's own text) — and moves to `ERROR`; on a later message the target gets -8499 or -9615 and the sender -8462.
  The failed message and every later one are held, "Service Broker received an error message on this conversation…".
- `RECEIVE [TOP (n)] columns FROM queue [INTO @t] [WHERE conversation_handle | conversation_group_id = x]` takes the ready messages of one conversation group no other transaction holds — the first in `queuing_order` — locks the group for its transaction, and returns them, assigns them (`@v = column`, never mixed with returned columns: Msg 8447), or inserts them.
  A column alias is `AS` or juxtaposition; `alias = column` is Msg 102, and so is any other `WHERE`.
  A rollback puts the messages back; a queue with poison-message handling is disabled by the fifth `RECEIVE` in a row that a rollback undid, which the next `RECEIVE` meets as Msg 9617.
- `WAITFOR (RECEIVE …) [, TIMEOUT ms]` and `WAITFOR (GET CONVERSATION GROUP @g FROM q) [, TIMEOUT ms]` wait on the broker's gate, a cancel or `CommandTimeout` ending them, and record `BROKER_RECEIVE_WAITFOR` (which `sys.dm_exec_requests` reports); at the timeout `RECEIVE` returns its empty set and `GET` a NULL group.
- `END CONVERSATION @h` sends the far side an EndDialog (`DISCONNECTED_INBOUND`), or with `WITH ERROR = n DESCRIPTION = 'text'` an Error carrying them (`ERROR`, n from 1, Msg 8165 state 3); the initiator's endpoint then goes, a target's stays `CLOSED`, and the endpoint's messages leave its queue and the transmission queue.
  `WITH CLEANUP` drops the endpoint telling no one.
- `BEGIN CONVERSATION TIMER (@h) TIMEOUT = n` puts a DialogTimer message (sequence -2) on the endpoint's own queue n seconds later; an expired `LIFETIME` errors both sides with -8489.
  Neither runs on a clock of its own: both fire when a statement next reads the broker's state.
- `MOVE CONVERSATION @h TO @g` moves an endpoint and its queued messages to another group of its service (Msg 8439 otherwise).
- `DROP SERVICE` closes its endpoints and errors their far sides with -8470.
- Retention keeps a queue's sent messages (status 3) and received ones (status 0) until the conversation ends.
- The handle statements' not-found is Msg 8426 at a state per statement (`END` 1, `MOVE` 2, timer 3, `SEND` 4, `RELATED_CONVERSATION` 6) and a NULL handle Msg 8418.
- Error message bodies are real's XML document (`<Error xmlns="http://schemas.microsoft.com/SQL/ServiceBroker/Error"><Code>…</Code><Description>…</Description></Error>`) in UTF-16 behind a byte order mark, sequence -1.
- `BEGIN DIALOG` and `RECEIVE` need `RECEIVE` on the queue (Msg 1088 states 41 and 42), `SELECT` on a queue the `SELECT` permission.

## Catalog views

`sys.service_message_types`, `sys.service_contracts`, `sys.service_contract_message_usages`, `sys.message_type_xml_schema_collection_usages`, `sys.services`, `sys.service_contract_usages`, `sys.service_queue_usages`, `sys.service_queues`, `sys.routes`, `sys.remote_service_bindings`, `sys.conversation_priorities`, `sys.conversation_endpoints`, `sys.conversation_groups`, `sys.transmission_queue` and `sys.dm_broker_queue_monitors` (one `INACTIVE` row per queue with an activation procedure).
A queue's `SELECT` lists its messages in its clustered index's order — status, group, priority, conversation, queuing order — as real's scan does.
`sys.event_notifications` stays empty: event notifications aren't built.

## Divergences

- **Delivery failures reach the sender as its transaction commits**, where real's broker reports them a moment later: a `SEND` right after a failing one in the same batch is refused here (Msg 8429, the endpoint already `ER`) where on real it is usually accepted and held.
- **Reads never wait**: a `SELECT` of a queue, or of the conversation views, reads the committed state (plus the session's own changes) where real's would wait on another transaction's uncommitted `SEND` or `RECEIVE` (Msg 1222 under a lock timeout).
- Endpoints carry no session keys, security timestamp or out-of-order bookkeeping, and every priority is 5: `CREATE BROKER PRIORITY` is catalog-only.
- `far_broker_instance` is the simulator's fixed `service_broker_guid`, and a target's `principal_id` is always dbo.
- The DONE token a conversation statement closes with: real's own codes aren't captured, so `RECEIVE` closes as a `SELECT` and the rest send none.

- **The shipped queues aren't in `sys.objects`**, nor are any queue's `queue_messages_<id>` internal tables, which real lists beside its 72 system base tables (see [`catalog-views.md`](catalog-views.md)); `sys.service_queues`, `OBJECT_ID` and `SELECT` reach the shipped queues all the same.
- **Ids are spent only on success**: real spends one on a `CREATE` that fails after allocating it (a duplicate contract, a missing message type), so a later object's id can run ahead of the simulator's.
- **Backup refuses** a database holding a Service Broker object of its own (`NotSupportedException`), since the image doesn't capture them.
- `ALTER SCHEMA … TRANSFER` and `sp_rename` of a queue aren't built.

## Not modeled yet

- Event notifications (`CREATE EVENT NOTIFICATION`), `sys.event_notifications` rows, and the broker's own event notifications such as `BROKER_QUEUE_DISABLED`.
- Remote delivery: a route to another server's address holds its messages for ever, and `sys.dm_broker_connections` / `sys.dm_broker_forwarded_messages` aren't registered.
- The permission a sender needs on the target service (`SEND`), which local delivery here never checks.
