using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// Single source of truth for the SQL Server reference build the simulator
/// emulates. Every version-bearing surface derives from here — the
/// <c>SERVERPROPERTY</c> version family, <c>@@VERSION</c> /
/// <c>@@MICROSOFTVERSION</c>, <c>xp_msver</c>,
/// <c>SimulatedDbConnection.ServerVersion</c>, and the TDS LOGINACK /
/// prelogin VERSION bytes — so a reference-build refresh is a one-file bump.
/// The graduated tests pin the derived literals independently
/// (<c>ServerPropertyTests</c>, <c>XpMsverTests</c>), so a typo here fails
/// the suite rather than propagating silently.
/// </summary>
internal static class ReferenceBuild
{
    /// <summary>
    /// SQL Server 2025 RTM-CU7 — the live reference instance behavior probes
    /// run against. SSMS gates report viewers and Activity Monitor on
    /// per-build client feature checks, so a real build number is
    /// load-bearing, not cosmetic.
    /// </summary>
    public static readonly Version Version = new(17, 0, 4065, 4);

    /// <summary>Servicing level paired with <see cref="Version"/> (<c>SERVERPROPERTY('ProductUpdateLevel')</c>).</summary>
    public const string UpdateLevel = "CU7";

    /// <summary>Servicing KB paired with <see cref="Version"/> (<c>SERVERPROPERTY('ProductUpdateReference')</c>).</summary>
    public const string UpdateReference = "KB5096981";

    /// <summary>"17.0.4065.4" — <c>SERVERPROPERTY('ProductVersion')</c> and the <c>xp_msver</c> ProductVersion row.</summary>
    public static readonly string ProductVersion = Version.ToString();

    /// <summary>"17" — <c>SERVERPROPERTY('ProductMajorVersion')</c>.</summary>
    public static readonly string ProductMajorVersion = Version.Major.ToString(CultureInfo.InvariantCulture);

    /// <summary>"0" — <c>SERVERPROPERTY('ProductMinorVersion')</c>.</summary>
    public static readonly string ProductMinorVersion = Version.Minor.ToString(CultureInfo.InvariantCulture);

    /// <summary>"4065" — <c>SERVERPROPERTY('ProductBuild')</c>.</summary>
    public static readonly string ProductBuild = Version.Build.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// "17.00.4065" (two-digit minor) — <c>SERVERPROPERTY('ResourceVersion')</c>,
    /// and the string real SqlClient computes from the LOGINACK version bytes,
    /// which <c>SimulatedDbConnection.ServerVersion</c> mirrors in-process.
    /// </summary>
    public static readonly string MajorMinorBuild = string.Create(CultureInfo.InvariantCulture, $"{Version.Major}.{Version.Minor:00}.{Version.Build}");

    /// <summary>
    /// <c>@@MICROSOFTVERSION</c>: <c>(major &lt;&lt; 24) | (minor &lt;&lt; 16) | build</c>
    /// = 285216737 (0x11000FE1), matching the reference instance.
    /// </summary>
    public static readonly int MicrosoftVersion = (Version.Major << 24) | (Version.Minor << 16) | Version.Build;

    /// <summary>
    /// The <c>@@VERSION</c> banner: real's multi-line shape and build-date
    /// line, with the simulator's own identity standing in for the host-OS
    /// line.
    /// </summary>
    public static readonly string Banner =
        $"Microsoft SQL Server 2025 (RTM-{UpdateLevel}) ({UpdateReference}) - {ProductVersion} (X64) \n" +
        "\tJul  8 2026 23:26:08 \n" +
        "\tCopyright (C) 2025 Microsoft Corporation\n" +
        "\tDeveloper Edition (64-bit) on SQL Server Simulator";

    /// <summary>
    /// The <c>xp_msver</c> FileVersion row. The encoding (product year,
    /// zero-padded major ×10, zero-padded revision, servicing-branch tag,
    /// build-date stamp) is the reference binary's literal file-version
    /// resource — not derivable, so bump it together with <see cref="Version"/>.
    /// </summary>
    public const string FileVersion = "2025.0170.4065.04 ((sql2025_rtm_qfe-cu7).260709-0512)";
}
