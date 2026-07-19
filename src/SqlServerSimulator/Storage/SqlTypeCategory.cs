namespace SqlServerSimulator.Storage;

/// <summary>
/// Coarse classification used by <see cref="SqlType.Promote"/> and the
/// binary-expression dispatchers to dispatch in a single jump-table-friendly
/// step. The granularity matches the SQL Server data-type-precedence chart's
/// family boundaries; each concrete <see cref="SqlType"/> pins its category
/// at construction so callers read a field instead of running a chain of
/// <c>is</c>/<c>==</c> checks.
/// </summary>
internal enum SqlTypeCategory : byte
{
    Other,
    Integer,
    Decimal,
    Money,
    Approximate,
    String,
    DateTime,
    UniqueIdentifier,
}
