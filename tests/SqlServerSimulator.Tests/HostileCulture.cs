using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// Runs a test process under a culture that formats and parses unlike the
/// invariant one, so an engine path that consults the host's culture instead
/// of running invariantly fails a test rather than a consumer's machine.
/// </summary>
/// <remarks>
/// <c>fi-FI</c> is the default because it differs from the invariant culture
/// wherever a leak usually shows: its negative sign is U+2212 rather than
/// <c>-</c> (the leak integers have, which no analyzer flags), its decimal
/// separator is a comma, its time separator a period and its month names
/// non-ASCII. <c>SIMULATOR_TEST_CULTURE</c> names another for a local run —
/// <c>th-TH</c> for a Buddhist-calendar year, <c>tr-TR</c> for dotted and
/// dotless I in casing.
/// </remarks>
internal static class HostileCulture
{
    /// <summary>Pins the current and every new thread's culture and UI culture.</summary>
    public static void Pin()
    {
        var culture = CultureInfo.GetCultureInfo(Environment.GetEnvironmentVariable("SIMULATOR_TEST_CULTURE") ?? "fi-FI");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
