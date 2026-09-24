namespace SqlServerSimulator.Storage;

/// <summary>
/// Where a type specification is written, which decides the error an
/// out-of-range precision or scale raises — real reports the same mistake
/// three ways (probed 2026-09-24 against SQL Server 2025).
/// </summary>
internal enum TypeSpecSite
{
    /// <summary>
    /// A <c>CAST</c> / <c>CONVERT</c> target, where a precision past the
    /// type's maximum written alone clamps to it (<c>numeric(39)</c> is
    /// <c>numeric(38, 0)</c>, <c>float(54)</c> is <c>float</c>).
    /// </summary>
    Cast,

    /// <summary>
    /// A variable, parameter or alias type: a lone precision past the maximum
    /// is Msg 2750, one written with a scale Msg 2717, and a scale past the
    /// precision Msg 192.
    /// </summary>
    Scalar,

    /// <summary>
    /// A column: any precision past the maximum is Msg 2750 and a scale past
    /// the precision Msg 183 naming the column.
    /// </summary>
    Column,
}
