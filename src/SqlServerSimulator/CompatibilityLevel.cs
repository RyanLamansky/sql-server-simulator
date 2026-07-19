namespace SqlServerSimulator;

/// <summary>
/// SQL Server database compatibility levels controlling version-conditional
/// behaviors (e.g. verbose truncation messages, query optimizer rules).
/// </summary>
/// <remarks>
/// <para>
/// Values match the integer accepted by
/// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c>; <see cref="int"/> casts
/// round-trip the wire form. Greater-than/less-than comparisons between
/// members are well-defined and used at decision sites
/// (e.g. <c>level &gt;= CompatibilityLevel.Sql160</c>).
/// </para>
/// <para>
/// Using a named enum rather than a raw <see cref="int"/> means that when a
/// level is retired by SQL Server in the future, deleting the corresponding
/// member surfaces every dependent reference at compile time rather than
/// letting them drift silently.
/// </para>
/// </remarks>
internal enum CompatibilityLevel
{
    /// <summary>SQL Server 2008 / 2008 R2.</summary>
    Sql100 = 100,

    /// <summary>SQL Server 2012.</summary>
    Sql110 = 110,

    /// <summary>SQL Server 2014.</summary>
    Sql120 = 120,

    /// <summary>SQL Server 2016.</summary>
    Sql130 = 130,

    /// <summary>SQL Server 2017.</summary>
    Sql140 = 140,

    /// <summary>SQL Server 2019.</summary>
    Sql150 = 150,

    /// <summary>SQL Server 2022.</summary>
    Sql160 = 160,

    /// <summary>SQL Server 2025.</summary>
    Sql170 = 170,
}
