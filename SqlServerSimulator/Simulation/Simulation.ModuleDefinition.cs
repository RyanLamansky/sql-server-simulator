using System.Text.RegularExpressions;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

public sealed partial class Simulation
{
    // ^(CREATE <ws>) OR (<ws>) ALTER — collapses a CREATE OR ALTER verb phrase
    // to a bare CREATE in the stored definition. SQL Server removes the OR /
    // ALTER keyword tokens but keeps the whitespace that surrounded them, so
    // `CREATE OR ALTER PROCEDURE` is stored as `CREATE   PROCEDURE`
    // (probe-confirmed). The two captured whitespace runs reproduce that.
    private static readonly Regex CreateOrAlterVerb =
        new(@"^(CREATE\s+)OR(\s+)ALTER", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the verbatim module-definition text stored for
    /// <c>OBJECT_DEFINITION</c> / <c>sys.sql_modules</c>, slicing the original
    /// command text from the statement's leading verb keyword (<paramref name="verbStart"/>,
    /// taken from <see cref="StatementContext.StartIndex"/>) through the end of
    /// the body. The leading verb is normalized to <c>CREATE</c> to match SQL
    /// Server, which stores <c>ALTER PROCEDURE …</c> as <c>CREATE PROCEDURE …</c>
    /// and collapses <c>CREATE OR ALTER</c> to <c>CREATE</c> (probe-confirmed
    /// against SQL Server 2025). A plain <c>CREATE</c> is captured verbatim.
    /// </summary>
    private static string BuildModuleDefinition(string commandText, int verbStart, int bodyEnd, bool isAlter, bool createOrAlter)
    {
        var raw = commandText[verbStart..bodyEnd];
        return createOrAlter ? CreateOrAlterVerb.Replace(raw, "$1$2")
            : isAlter ? "CREATE" + raw["ALTER".Length..]
            : raw;
    }
}
