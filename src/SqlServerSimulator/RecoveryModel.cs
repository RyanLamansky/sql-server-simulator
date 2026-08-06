namespace SqlServerSimulator;

/// <summary>
/// A database's recovery model, as <c>sys.databases.recovery_model</c> encodes
/// it. The simulator has no transaction log, so the setting drives nothing —
/// it is carried so an imported or explicitly-configured database describes
/// itself the way the original did.
/// </summary>
/// <remarks>
/// The numeric values are real's own (probe-confirmed against SQL Server 2025:
/// <c>model</c> reports 1, and a database SET to each of the three reports
/// 1 / 2 / 3). A bacpac's <c>RecoveryMode</c> property uses a <em>different</em>
/// encoding — DacFx omits the property for FULL and writes 1 for SIMPLE,
/// 2 for BULK_LOGGED — so the loader translates rather than casting.
/// </remarks>
internal enum RecoveryModel : byte
{
    Full = 1,
    BulkLogged = 2,
    Simple = 3,
}
