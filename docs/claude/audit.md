# SQL Server Audit

Server audits, server and database audit specifications, their catalog, and the records an audit writes, as SQL Server 2025 on Linux has them.
Everything here was probed 2026-10-17 against SQL Server 2025 (17.0.4065.4) on Linux unless it says otherwise.

## The engine underneath

An audit is an Extended Events session real runs internally under the name `<audit>$A`, taking the private `SecAudit.audit_event` event into a security-audit file target.
The simulator keeps that shape without a session definition: `AuditServer` (one per `Simulation`, made on first use by `Simulation.AuditsFor`) holds the audits and the server specifications, each database holds its own specifications (`Database.AuditSpecifications`), and a record is an `XeFiring` of `audit_event` whose data columns are the record's fields.
An audit's `WHERE` predicate parses and binds through the Extended Events predicate machinery (`XePredicateSyntax`) against `audit_event`, so its stored text, its comparators and its refusals (Msg 25706 for an unknown field, 25713 for a constant of the wrong kind — `action_id` is an `int` field, so `action_id = 'SL'` is refused) are an event session's.

`Simulation.RunningAudits` lists the running audits, published on every start and stop and null while none runs, so a firing site's whole cost on a server auditing nothing is one field read.

## DDL

`CREATE SERVER AUDIT name TO { FILE (…) | APPLICATION_LOG | SECURITY_LOG } [WITH (…)] [WHERE …]`, `ALTER SERVER AUDIT`, `DROP SERVER AUDIT`, and `CREATE | ALTER | DROP { SERVER | DATABASE } AUDIT SPECIFICATION`, parsed by `Simulation.TryParseAuditDdl` ahead of the unbuilt-statement list.
The grammar is read whole as the batch compiles, so its refusals stop the batch: a missing `TO` is Msg 102 near `AUDIT`, a `FILE` target without `FILEPATH` Msg 102 near the audit's name, a size without its unit near `MAXSIZE`, `MAX_FILES = UNLIMITED` near `MAX_FILES`, a fraction or a number past `int` Msg 1080, `STATE` in a `CREATE` and `AUDIT_GUID` in an `ALTER` Msg 102 near the option, a `#` audit name Msg 1074 (real's message for a temporary function), and `URL`, `EXTERNAL_MONITOR` and `OPERATOR_AUDIT`, which a Linux server lacks, Msg 40517.
`RETENTION_DAYS` is Msg 102: the Linux grammar has no such option.
A repeated file option or `WITH` option is taken, its last value standing.

As the statement runs, in real's order:

- **`master`.** Server audit and server specification DDL anywhere else is Msg 33074, its state naming the statement, ahead of any permission check; a database specification is made in the database it watches, `master` and `tempdb` included.
- **Permission.** `ALTER ANY SERVER AUDIT` (which `CONTROL SERVER` covers) for an audit or a server specification — `CREATE` without it is Msg 15247, `ALTER` and `DROP` Msg 15151 as for a missing audit — and `ALTER ANY DATABASE AUDIT` in the database (which `ALTER ANY SERVER AUDIT` implies) for a database specification.
  `ON_FAILURE = SHUTDOWN` in a `CREATE` needs `SHUTDOWN` too (Msg 33228); an `ALTER` setting it isn't checked.
- **Ids.** `audit_id` counts from 65536 server-wide, a refusal past the permission check spending one (a missing path, a taken name, a predicate that won't bind).
  A specification's id counts per database, `master`'s counter numbering the server specifications too, so a server specification and a database's can share an id.
- **Options.** A `MAXSIZE` of 1 MB is Msg 33070 (0 means unlimited), `MAX_FILES` beside `MAX_ROLLOVER_FILES` 33238, `RESERVE_DISK_SPACE = ON` with no size 33077, a `QUEUE_DELAY` from 1 to 999 33227 (state 1 in `CREATE`, 2 in `ALTER`), an empty `FILEPATH` 15021, and a path naming no directory the server holds — a relative one included — 33072.
  The path is the server's disk as the other file surfaces see it (`Simulation.IsServerDirectory`): a Linux path is stored with its `.` and `..` settled and a `/` after it, a drive path as written.
  An `AUDIT_GUID` that isn't a GUID is Msg 8169, and all zeros means none was given.
- **Names.** A taken audit name, and a GUID another audit holds, are Msg 15530 naming the new audit; a server audit holds one server specification and one per database (Msg 33230), and a specification's name is unique in its scope (Msg 15530).

`ALTER SERVER AUDIT … WITH (STATE = ON | OFF)` stands alone (Msg 33223 beside anything else), starting a running audit or stopping a stopped one does nothing, and inside a user transaction it is Msg 574.
Any other change to a running audit, and its drop, is Msg 33071 state 1; the other DDL runs inside a transaction and rolls back with it.
`modify_date` moves only for `MODIFY NAME`, never for a change of target, options or predicate, and a specification's never moves.
Dropping an audit leaves the specifications naming it, which keep its GUID and record nothing.

A specification's items are `ADD (group)` or, in a database specification, `ADD (action [, …] ON [class::]securable BY principal [, …])` for the seven actions `SCHEMA_OBJECT_ACCESS_GROUP` covers; `DROP` takes the same items, only in an `ALTER`.
A group the scope doesn't take — a login group in a database specification, a database action in a server one — is Msg 102 near it, a column list Msg 33211, a missing object or schema Msg 15151 naming it, and a missing principal Msg 15151 naming it as a user whatever it is.
Adding an item the specification holds, or dropping one it doesn't, does nothing.
Any change but `STATE` to an enabled specification is Msg 33229, even when the same statement's `STATE = OFF` would allow it, and dropping an enabled one Msg 33071 state 3; `STATE` inside a user transaction is Msg 574.

Each statement raises its DDL trigger event — `CREATE_SERVER_AUDIT` and the rest to a server trigger, the database specification's to a database trigger with `UserName` and `DatabaseName` — and a running audit whose specification takes `AUDIT_CHANGE_GROUP` records it (below).

## The catalog

`sys.server_audits`, `sys.server_file_audits`, `sys.server_audit_specifications` and `sys.server_audit_specification_details` list nothing to a login without `ALTER ANY SERVER AUDIT` or `VIEW SERVER SECURITY AUDIT`; `sys.database_audit_specifications` and its details list a database's to a session holding `ALTER ANY DATABASE AUDIT` there too.
`sys.server_file_audits.log_file_name` is `name_GUID.sqlaudit`, which each file's name extends.
A server specification's group rows read class 100 and principal 2 (the server's `public`), a database specification's securable class 0, 1 or 3 with the principal's id; the server details list by specification then action id, a database's by class, securable, principal and action, the specifications interleaved.
`is_session_context_enabled` reads 0 and `session_context_keys` NULL.

`sys.dm_server_audit_status` lists the running audits with their start time, a stand-in `event_session_address` and the file they write — 20,480 bytes at the start, then whole 4 KB buffers.

`sys.dm_audit_actions` and `sys.dm_audit_class_type_map` list real's 663 and 110 rows in real's order, dumped into `Audit/Metadata/` (some 75 KB of tab-separated text, zipped into the assembly as the Extended Events catalog is) and read once per process by `AuditCatalog`.
`PermissionBits.tsv` beside them is the bit each permission of the server, database, schema and object classes sets in a record's `permission_bitmask`, read off real's records of a `GRANT` of each.

A backup carries a database's specifications (`DatabaseImage.Audit.cs`), and a restore brings each back with its id, dates, state and audit GUID — an enabled one recording for that audit at once if the server holds it.

## The files

A `TO FILE` audit opens `name_GUID_0_<FILETIME>.sqlaudit` under its path as it starts, the FILETIME its start to the millisecond.
The files are the simulator's own, kept in memory on the audit state (`AuditServer.Files`) so they outlive their audit, and the server-file surfaces (`xp_dirtree`, `xp_fileexist`) see them; nothing reaches the host's disk.
An audit to either event log keeps nothing a statement reads.

`sys.fn_get_audit_file(pattern, initial_file_name, audit_record_offset)` and `sys.fn_get_audit_file_v2`, which adds a start and an end time, read the records back in real's 49-column shape: every file the pattern matches in name order — a relative pattern under `/var/opt/mssql/log/`, `*` any run and `?` one character, a directory alone nothing — each record as it was written.
A NULL pattern, or one under a directory the server doesn't hold, is Msg 33224; an initial file and offset must name a record of a matched file (Msg 33225 otherwise, or when one comes without the other) and read on past it; `_v2`'s times take `YYYY-MM-DDThh:mm:ssZ` alone, another time being Msg 37620 and anything else Msg 241.
Reading needs `VIEW SERVER SECURITY AUDIT` (Msg 300 then 297).

An audit writes to itself as it starts — an `AUSC` record, class `A `, whose `additional_information` is `<action_info …><session><![CDATA[name$A]]></session><action>event enabled</action><startup_type>manual</startup_type><object><![CDATA[audit_event]]></object></action_info>` — and as it stops, `destroyed` and then `event disabled`, whatever its specifications name; its predicate judges these too.

## Records

`AuditEvents.Write` takes an `AuditOccurrence` — the action, the class its group is looked up under, the record's `class_type` and the securable, statement and extra information — and writes it to every running audit whose enabled specifications cover it, once per audit however many cover it: a server specification by the action's group (`sys.dm_audit_actions.containing_group_name` for the action on that class), a database specification of the action's database by its group, or — for an object access — by the action on the object, its schema or the database for a principal the session's user is or is a member of.
The session's fields — `session_id`, the server principal (the effective login) and the session's own (`session_server_principal_name`, the original login), the database principal in the action's database, `server_instance_name`, `application_name`, `host_name`, `client_ip` (`local machine` in process), `connection_id` — are filled for every record.

`AUDIT_CHANGE_GROUP` records each audit and specification statement as `CR`, `AL` or `DR` with class `A `, `SA` or `DA`, in `master` for the server's and the specification's database for a database specification, naming the object as it was before the statement (a rename names the old name); a `STATE` change is recorded before it takes effect.

### Divergences

- `audit_file_offset` reads 16384 for every record, a file's first buffer, where real's asynchronous audits step to later buffers as they flush; reading from a file and offset therefore reads on from the next file.
- A file's size counts each record at the simulator's estimate of real's encoding, some 300 bytes and two a character of its text, so it grows near, not at, real's.
- An `AUSC` record's `permission_bitmask` and `sequence_group_id` read zeros where real's carry stray bytes, and its `connection_id` the session's where real's reads a constant.
- `client_tls_version` and its name read 0 and empty, as an in-process connection's; real's TLS clients report 771 and `1.2`.

### Not modeled yet

- The audit's own Extended Events session: `sys.dm_xe_sessions` and its companions don't list `<audit>$A`.
- `ON_FAILURE`'s effect (no write here fails), `QUEUE_DELAY`'s batching, `MAXSIZE` and the rollover and file-count limits, `RESERVE_DISK_SPACE`'s preallocation, and the event logs' records.
- `SESSION_CONTEXT_KEYS` and the other Azure-only specification options.
