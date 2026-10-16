# Service Broker

Message types, contracts, queues, services, routes, remote service bindings and conversation priorities, with the catalog views over them.
Everything here was probed 2026-10-16 against SQL Server 2025 (17.0.4065.4) within one database unless a line says otherwise.

## Where the state lives

- **Queues are schema objects** (`ServiceQueue`, type `SQ`) in `Schema.Queues`, sharing the object namespace: `sys.objects`, `OBJECT_ID`, `OBJECTPROPERTY(…, 'IsQueue')`, object-class `GRANT`s and the Msg 2714 / 3705 collisions all come from that.
- **Everything else is database-scoped** on `DatabaseBroker` (`Database.ServiceBroker`), created on first use together with the objects real ships, so a database no statement asks about carries none of it.
  `Database.ServiceBrokerIfUsed` reads it without creating it; a path that only needs to know whether anything is there uses that.
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

## Catalog views

`sys.service_message_types`, `sys.service_contracts`, `sys.service_contract_message_usages`, `sys.message_type_xml_schema_collection_usages`, `sys.services`, `sys.service_contract_usages`, `sys.service_queue_usages`, `sys.service_queues`, `sys.routes`, `sys.remote_service_bindings`, `sys.conversation_priorities` and `sys.dm_broker_queue_monitors` (one `INACTIVE` row per queue with an activation procedure).
`sys.event_notifications` stays empty: event notifications aren't built.

## Divergences

- **The shipped queues aren't in `sys.objects`**, nor are any queue's `queue_messages_<id>` internal tables, which real lists beside its 72 system base tables (see [`catalog-views.md`](catalog-views.md)); `sys.service_queues`, `OBJECT_ID` and `SELECT` reach the shipped queues all the same.
- **Ids are spent only on success**: real spends one on a `CREATE` that fails after allocating it (a duplicate contract, a missing message type), so a later object's id can run ahead of the simulator's.
- **Backup refuses** a database holding a Service Broker object of its own (`NotSupportedException`), since the image doesn't capture them.
- `ALTER SCHEMA … TRANSFER` and `sp_rename` of a queue aren't built.

## Not modeled yet

- Event notifications (`CREATE EVENT NOTIFICATION`), `sys.event_notifications` rows, and the broker's own event notifications such as `BROKER_QUEUE_DISABLED`.
