using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Globalization;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static void RegisterSecurity(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        // sys.dm_tran_locks: per-Hold rows across every schema-bound
        // SchemaLock, every HeapTable.TableDataLock, and every per-row
        // entry in HeapTable.RowLocks. GRANT entries come from
        // LockResource.Holders; WAIT entries from connection registry's
        // WaitingOnResource / WaitingForMode. Shipped column subset is
        // the most commonly read seven fields; the full real-SQL-Server
        // shape has ~18 columns most apps never touch.
        Sys("dm_tran_locks",
        [
            new("resource_type", SqlType.NVarchar, 60, false),
            new("resource_database_id", SqlType.Int32, null, false),
            new("resource_description", SqlType.NVarchar, 256, true),
            new("resource_associated_entity_id", SqlType.BigInt, null, true),
            new("request_mode", SqlType.NVarchar, 60, false),
            new("request_status", SqlType.NVarchar, 60, false),
            new("request_session_id", SqlType.Int32, null, false),
        ], LockDmvs.EnumerateDmTranLocks);

        // sys.dm_os_waiting_tasks: one row per currently-waiting
        // connection. session_id / blocking_session_id are smallint
        // matching real SQL Server; wait_type is `LCK_M_<mode>` per
        // SQL Server's convention.
        Sys("dm_os_waiting_tasks",
        [
            new("session_id", SqlType.SmallInt, null, true),
            new("wait_type", SqlType.NVarchar, 60, true),
            new("resource_description", SqlType.NVarchar, 2000, true),
            new("blocking_session_id", SqlType.SmallInt, null, true),
        ], LockDmvs.EnumerateDmOsWaitingTasks);

        // sys.dm_tran_version_store: one row per finalized HistoricalVersion
        // across every per-table chain. Pending HVs (Xmax = PendingXmax)
        // are excluded. Real SQL Server's exact column order is preserved
        // so existing diagnostic queries port unchanged.
        Sys("dm_tran_version_store",
        [
            new("transaction_sequence_num", SqlType.BigInt, null, false),
            new("version_sequence_num", SqlType.BigInt, null, false),
            new("database_id", SqlType.SmallInt, null, false),
            new("rowset_id", SqlType.BigInt, null, false),
            new("status", SqlType.TinyInt, null, false),
            new("min_length_in_bytes", SqlType.SmallInt, null, false),
            new("record_length_first_part_in_bytes", SqlType.SmallInt, null, false),
            new("record_image_first_part", VarbinarySqlType.MaxForm, null, true),
            new("record_length_second_part_in_bytes", SqlType.SmallInt, null, true),
            new("record_image_second_part", VarbinarySqlType.MaxForm, null, true),
        ], VersionStoreDmvs.EnumerateDmTranVersionStore);

        // sys.dm_tran_version_store_space_usage: aggregate sizing per
        // database. The simulator approximates pages as ceil(bytes / 8192)
        // since HV payloads aren't backed by real pages.
        Sys("dm_tran_version_store_space_usage",
        [
            new("database_id", SqlType.Int32, null, false),
            new("reserved_page_count", SqlType.BigInt, null, false),
            new("reserved_space_kb", SqlType.BigInt, null, false),
        ], VersionStoreDmvs.EnumerateDmTranVersionStoreSpaceUsage);

        // sys.dm_tran_active_snapshot_database_transactions: one row per
        // active SI tx with an allocated snapshot Xid. RCSI per-statement
        // snapshots are not tracked here (matching real SQL Server).
        Sys("dm_tran_active_snapshot_database_transactions",
        [
            new("transaction_id", SqlType.BigInt, null, false),
            new("transaction_sequence_num", SqlType.BigInt, null, false),
            new("commit_sequence_num", SqlType.BigInt, null, true),
            new("session_id", SqlType.Int32, null, false),
            new("is_snapshot", SqlType.Bit, null, false),
            new("first_snapshot_sequence_num", SqlType.BigInt, null, true),
            new("max_version_chain_traversed", SqlType.Int32, null, false),
            new("average_version_chain_traversed", SqlType.Float, null, false),
            new("elapsed_time_seconds", SqlType.BigInt, null, false),
        ], VersionStoreDmvs.EnumerateDmTranActiveSnapshotDatabaseTransactions);

        // sys.extended_properties: per-database user-defined annotations
        // attached to schemas / tables / columns / etc. via the
        // sp_addextendedproperty / sp_updateextendedproperty /
        // sp_dropextendedproperty trio. Real SQL Server's `value` column is
        // typed `sql_variant` — the simulator surfaces it as `nvarchar(MAX)`
        // since sql_variant isn't modeled; AW's 538 properties are all
        // nvarchar values so functional fidelity is preserved.
        Sys("extended_properties",
        [
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", SqlType.SystemName, 60, true),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("value", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.MaxLengthSentinel, true),
        ], EnumerateSysExtendedProperties);

        // sys.database_principals: probe-confirmed shipped subset of columns
        // (real SQL Server's full row is ~16 cols). The simulator's principal
        // model is a thin name + id + type triple; columns we don't track
        // (authentication_type, default_schema_name, default_language_name,
        // owning_principal_id, modify_date) are emitted as NULL.
        Sys("database_principals",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("default_schema_name", SqlType.SystemName, 128, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("owning_principal_id", SqlType.Int32, null, true),
            new("sid", SqlType.Varbinary, 85, true),
            new("is_fixed_role", SqlType.Bit, null, false),
            new("authentication_type", SqlType.TinyInt, null, true),
            new("authentication_type_desc", nvarchar60Catalog, 60, true),
        ], EnumerateSysDatabasePrincipals);

        // sys.database_permissions: probe-confirmed 8-col shipped subset.
        // Real SQL Server's row carries a few additional internal columns
        // (e.g. revert_audit_flag); the simulator surfaces the user-visible
        // set only.
        Sys("database_permissions",
        [
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", nvarchar60Catalog, 60, true),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("grantee_principal_id", SqlType.Int32, null, false),
            new("grantor_principal_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("permission_name", nvarchar128Catalog, 128, true),
            new("state", charOne, 1, false),
            new("state_desc", nvarchar60Catalog, 60, true),
        ], EnumerateSysDatabasePermissions);

        // sys.database_role_members: 2-col shipped subset (real SQL Server
        // surfaces just these two — no additional internal columns).
        Sys("database_role_members",
        [
            new("role_principal_id", SqlType.Int32, null, false),
            new("member_principal_id", SqlType.Int32, null, false),
        ], EnumerateSysDatabaseRoleMembers);

        // sys.server_principals: probe-confirmed 14-col shape against SQL
        // Server 2025 (2026-07-15), projected over the per-Simulation login
        // registry (Simulation.Logins) plus two synthetic fixed rows: sa
        // (principal_id 1) and public (principal_id 2). Columns the simulator
        // doesn't track (credential_id, disabled flag) surface as their real
        // low-privilege defaults.
        Sys("server_principals",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, false),
            new("sid", SqlType.Varbinary, 85, true),
            new("type", charOne, 1, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_disabled", SqlType.Bit, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("default_database_name", SqlType.SystemName, 128, true),
            new("default_language_name", SqlType.SystemName, 128, true),
            new("credential_id", SqlType.Int32, null, true),
            new("owning_principal_id", SqlType.Int32, null, true),
            new("is_fixed_role", SqlType.Bit, null, false),
            new("tenant_id", SqlType.UniqueIdentifier, null, true),
        ], EnumerateSysServerPrincipals);

        // sys.sql_logins: probe-confirmed 14-col shape against SQL Server 2025
        // (2026-07-15). Same leading 10 columns as sys.server_principals,
        // filtered to type='S' (SQL logins) — sa plus the registry logins,
        // never the public server role. password_hash surfaces NULL: the
        // simulator deliberately doesn't expose its stored PWDCOMPARE hash,
        // matching what a low-privilege reader sees on the reference instance.
        Sys("sql_logins",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, false),
            new("sid", SqlType.Varbinary, 85, true),
            new("type", charOne, 1, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_disabled", SqlType.Bit, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("default_database_name", SqlType.SystemName, 128, true),
            new("default_language_name", SqlType.SystemName, 128, true),
            new("credential_id", SqlType.Int32, null, true),
            new("is_policy_checked", SqlType.Bit, null, true),
            new("is_expiration_checked", SqlType.Bit, null, true),
            new("password_hash", SqlType.Varbinary, 256, true),
        ], EnumerateSysSqlLogins);
    }

    /// <summary>
    /// Rows for <c>sys.extended_properties</c>. Walks every entry in
    /// <see cref="Database.ExtendedProperties"/> (per-database flat dict)
    /// and projects the 6-column shape. The <c>class_desc</c> string is
    /// derived from the class number per real SQL Server's enum (0 =
    /// DATABASE, 1 = OBJECT_OR_COLUMN, 3 = SCHEMA — the only classes the
    /// simulator currently emits; others fall through as the string form
    /// of the class number for forward compat). Value is coerced to
    /// <c>nvarchar(MAX)</c> since the simulator doesn't model
    /// <c>sql_variant</c>; for AW's all-nvarchar workload, this is a
    /// lossless surfacing.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysExtendedProperties(Parser.BatchContext batch, Database database)
    {
        foreach (var kvp in database.ExtendedProperties)
        {
            var key = kvp.Key;
            var classDesc = key.Class switch
            {
                0 => "DATABASE",
                1 => "OBJECT_OR_COLUMN",
                3 => "SCHEMA",
                7 => "INDEX",
                _ => key.Class.ToString(CultureInfo.InvariantCulture),
            };
            yield return [
                SqlValue.FromByte(key.Class),
                SqlValue.FromSystemName(classDesc),
                SqlValue.FromInt32(key.MajorId),
                SqlValue.FromInt32(key.MinorId),
                SqlValue.FromSystemName(key.Name),
                kvp.Value.IsNull ? SqlValue.Null(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)) : kvp.Value.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysDatabasePrincipals(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullSchemaName = SqlValue.Null(SqlType.SystemName);
        var nullOwningId = SqlValue.Null(SqlType.Int32);
        var nullSid = SqlValue.Null(SqlType.Varbinary);
        var nullAuthType = SqlValue.Null(SqlType.TinyInt);
        var nullAuthDesc = SqlValue.Null(SqlType.NVarchar);
        // 4-letter padding to fit char(2) — the type column is 2 bytes in
        // real SQL Server's catalog. SqlValue.FromChar pads to declared length.
        var charTwo = SqlType.GetChar(2);
        foreach (var p in database.Principals.Values.OrderBy(p => p.PrincipalId))
        {
            var createDate = SqlValue.FromDateTime(p.CreateDate);
            yield return [
                SqlValue.FromSystemName(p.Name),
                SqlValue.FromInt32(p.PrincipalId),
                SqlValue.FromChar(charTwo, p.TypeCode),
                SqlValue.FromNVarchar(p.TypeDescription),
                nullSchemaName,
                createDate,
                createDate,
                nullOwningId,
                nullSid,
                p.IsFixedRole ? trueBit : falseBit,
                nullAuthType,
                nullAuthDesc,
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysDatabasePermissions(Parser.BatchContext batch, Database database)
    {
        var charTwo = SqlType.GetChar(2);
        var charOne = SqlType.GetChar(1);
        foreach (var perm in database.Permissions)
        {
            var classDesc = perm.Class switch
            {
                0 => "DATABASE",
                1 => "OBJECT_OR_COLUMN",
                3 => "SCHEMA",
                4 => "DATABASE_PRINCIPAL",
                _ => "DATABASE",
            };
            var stateDesc = perm.State switch
            {
                "D" => "DENY",
                "G" => "GRANT",
                "R" => "REVOKE",
                "W" => "GRANT_WITH_GRANT_OPTION",
                _ => "GRANT",
            };
            yield return [
                SqlValue.FromByte(perm.Class),
                SqlValue.FromNVarchar(classDesc),
                SqlValue.FromInt32(perm.MajorId),
                SqlValue.FromInt32(perm.MinorId),
                SqlValue.FromInt32(perm.GranteePrincipalId),
                SqlValue.FromInt32(perm.GrantorPrincipalId),
                SqlValue.FromChar(charTwo, perm.TypeCode),
                SqlValue.FromNVarchar(perm.PermissionName),
                SqlValue.FromChar(charOne, perm.State),
                SqlValue.FromNVarchar(stateDesc),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseRoleMembers(Parser.BatchContext batch, Database database)
    {
        foreach (var (roleId, memberId) in database.RoleMembers)
        {
            yield return [
                SqlValue.FromInt32(roleId),
                SqlValue.FromInt32(memberId),
            ];
        }
    }

    /// <summary>
    /// Derives a deterministic 16-byte synthetic <c>sid</c> from a login name.
    /// Real SQL logins carry a 16-byte random GUID sid; the simulator fills the
    /// four 32-bit quadrants with a per-quadrant-salted FNV-1a hash so the same
    /// name always maps to the same bytes without persisting a GUID.
    /// </summary>
    private static byte[] DeriveLoginSid(string name)
    {
        var sid = new byte[16];
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var hash = Simulation.Fnv1a32.Initial;
            hash.Mix(name);
            hash.Mix((byte)quadrant);
            var value = hash.Value;
            var offset = quadrant * 4;
            sid[offset] = (byte)value;
            sid[offset + 1] = (byte)(value >> 8);
            sid[offset + 2] = (byte)(value >> 16);
            sid[offset + 3] = (byte)(value >> 24);
        }
        return sid;
    }

    /// <summary>
    /// Projects <c>sys.server_principals</c> over the per-Simulation login
    /// registry plus the two synthetic fixed rows (<c>sa</c> = principal_id 1,
    /// <c>public</c> = principal_id 2). Rows emit in principal_id order.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysServerPrincipals(Parser.BatchContext batch, Database database)
    {
        var simulation = batch.Connection.Simulation;
        var charOne = SqlType.GetChar(1);
        var falseBit = SqlValue.FromBoolean(false);
        var sqlLogin = SqlValue.FromNVarchar("SQL_LOGIN");
        var loginType = SqlValue.FromChar(charOne, "S");
        var nullCredentialId = SqlValue.Null(SqlType.Int32);
        var nullOwningId = SqlValue.Null(SqlType.Int32);
        var master = SqlValue.FromSystemName("master");
        var usEnglish = SqlValue.FromSystemName("us_english");
        var nullTenant = SqlValue.Null(SqlType.UniqueIdentifier);
        var zeroTenant = SqlValue.FromGuid(Guid.Empty);
        var seedDate = SqlValue.FromDateTime(simulation.SeedDate);

        // sa: the fixed SQL-authentication login, principal_id 1.
        yield return [
            SqlValue.FromSystemName("sa"),
            SqlValue.FromInt32(1),
            SqlValue.FromVarbinary([0x01]),
            loginType,
            sqlLogin,
            falseBit,
            seedDate,
            seedDate,
            master,
            usEnglish,
            nullCredentialId,
            nullOwningId,
            falseBit,
            nullTenant,
        ];

        // public: the fixed server role, principal_id 2. owning_principal_id
        // points at sa (1); is_fixed_role is 0 (probe-confirmed).
        yield return [
            SqlValue.FromSystemName("public"),
            SqlValue.FromInt32(2),
            SqlValue.FromVarbinary([0x02]),
            SqlValue.FromChar(charOne, "R"),
            SqlValue.FromNVarchar("SERVER_ROLE"),
            falseBit,
            seedDate,
            seedDate,
            SqlValue.Null(SqlType.SystemName),
            SqlValue.Null(SqlType.SystemName),
            nullCredentialId,
            SqlValue.FromInt32(1),
            falseBit,
            nullTenant,
        ];

        foreach (var login in simulation.Logins.Values.OrderBy(l => l.PrincipalId))
        {
            yield return [
                SqlValue.FromSystemName(login.Name),
                SqlValue.FromInt32(login.PrincipalId),
                SqlValue.FromVarbinary(DeriveLoginSid(login.Name)),
                loginType,
                sqlLogin,
                falseBit,
                SqlValue.FromDateTime(login.CreateDate),
                SqlValue.FromDateTime(login.PasswordLastSetTime),
                master,
                usEnglish,
                nullCredentialId,
                nullOwningId,
                falseBit,
                zeroTenant,
            ];
        }
    }

    /// <summary>
    /// Projects <c>sys.sql_logins</c>: the type='S' subset of
    /// <c>sys.server_principals</c> (<c>sa</c> plus the registry logins, never
    /// the <c>public</c> server role), with the policy / expiration / hash
    /// tail. Rows emit in principal_id order.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSqlLogins(Parser.BatchContext batch, Database database)
    {
        var simulation = batch.Connection.Simulation;
        var charOne = SqlType.GetChar(1);
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var sqlLogin = SqlValue.FromNVarchar("SQL_LOGIN");
        var loginType = SqlValue.FromChar(charOne, "S");
        var nullCredentialId = SqlValue.Null(SqlType.Int32);
        var master = SqlValue.FromSystemName("master");
        var usEnglish = SqlValue.FromSystemName("us_english");
        var nullPasswordHash = SqlValue.Null(SqlType.Varbinary);
        var seedDate = SqlValue.FromDateTime(simulation.SeedDate);

        yield return [
            SqlValue.FromSystemName("sa"),
            SqlValue.FromInt32(1),
            SqlValue.FromVarbinary([0x01]),
            loginType,
            sqlLogin,
            falseBit,
            seedDate,
            seedDate,
            master,
            usEnglish,
            nullCredentialId,
            trueBit,
            falseBit,
            nullPasswordHash,
        ];

        foreach (var login in simulation.Logins.Values.OrderBy(l => l.PrincipalId))
        {
            yield return [
                SqlValue.FromSystemName(login.Name),
                SqlValue.FromInt32(login.PrincipalId),
                SqlValue.FromVarbinary(DeriveLoginSid(login.Name)),
                loginType,
                sqlLogin,
                falseBit,
                SqlValue.FromDateTime(login.CreateDate),
                SqlValue.FromDateTime(login.PasswordLastSetTime),
                master,
                usEnglish,
                nullCredentialId,
                trueBit,
                falseBit,
                nullPasswordHash,
            ];
        }
    }
}
