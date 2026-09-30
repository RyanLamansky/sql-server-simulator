using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_addtype / sp_droptype — the pre-CREATE TYPE spelling of alias types.
// Real builds a CREATE TYPE / DROP TYPE string from its checks and executes it,
// so what the checks let through comes back from the ordinary statement — at
// line 1, in no procedure — and the simulator does the same, running the text
// through the dynamic-SQL path.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] AddTypeParameters =
    [
        new("typename", SqlType.NVarchar, 128),
        new("phystype", SqlType.NVarchar, 128),
        new("nulltype", SqlType.NVarchar, 8, SqlValue.FromNVarchar("NULL")),
        new("owner", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] DropTypeParameters =
    [
        new("typename", SqlType.NVarchar, 128),
    ];

    // The names sp_addtype's base-type lookup finds: the built-in types with
    // their single-word synonyms and the multi-word ANSI spellings (probed
    // 2026-09-30). sysname and every alias type are not among them.
    private static readonly FrozenSet<string> AddTypeBaseTypes = FrozenSet.ToFrozenSet(
    [
        "bigint", "binary", "binary varying", "bit", "char", "char varying", "character", "character varying", "date", "datetime",
        "datetime2", "datetimeoffset", "dec", "decimal", "double precision", "float", "geography", "geometry", "hierarchyid", "image",
        "int", "integer", "json", "money", "national char", "national char varying", "national character", "national character varying",
        "national text", "nchar", "ntext", "numeric", "nvarchar", "real", "rowversion", "smalldatetime", "smallint", "smallmoney",
        "sql_variant", "text", "time", "timestamp", "tinyint", "uniqueidentifier", "varbinary", "varchar", "vector", "xml",
    ], StringComparer.Ordinal);

    /// <summary>
    /// <c>sp_addtype @typename, @phystype [, @nulltype [, @owner]]</c> creates
    /// an alias type in <c>dbo</c>. Real's checks run in this order, each from
    /// its own line of the procedure: the owner warning, <c>@nulltype</c>, a
    /// text after a base type's parenthesis, a MAX or <c>xml</c> base, a
    /// non-numeric size, and the base type's existence. What they pass on is
    /// the <c>CREATE TYPE</c> statement's to refuse (probed 2026-09-30 against
    /// SQL Server 2025). A NULL <c>@typename</c> builds no statement, so the
    /// call does nothing.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddType(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_addtype", calledAs, arguments, AddTypeParameters);
        if (!values[3].IsNull && !values[3].AsString.TrimEnd(' ').Equals("dbo", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SimulatedInfoOutcome(SimulatedSqlException.SystemProcedureMessage(batch, calledAs, 24, 15166,
                "Warning: User types created via sp_addtype are contained in dbo schema. The @owner parameter if specified is ignored."));
        }

        var nullType = values[2].IsNull ? "NULL" : values[2].AsString.TrimEnd(' ').ToUpperInvariant();
        if (nullType is not ("NULL" or "NOT NULL" or "NONULL"))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.AddTypeUsage(), 51);

#pragma warning disable CA1308 // real quotes @phystype back lowercased in its own messages, so lowercase is the output form
        var physical = values[1].IsNull ? "(null)" : values[1].AsString.TrimEnd().ToLowerInvariant();
#pragma warning restore CA1308
        var refusal = CheckAddTypeBase(physical);
        if (refusal is not null)
            throw AtSystemProcedureLine(calledAs, refusal.Value.Error, refusal.Value.Line);

        if (values[0].IsNull)
            yield break;
        var name = values[0].AsString;
        var statement = new StringBuilder("create type [dbo].[").Append(name.Replace("]", "]]", StringComparison.Ordinal)).Append("] from ").Append(physical)
            .Append(nullType == "NULL" ? " null" : " not null").ToString();
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }

    /// <summary>
    /// The check ladder over <c>@phystype</c> (already right-trimmed and lowercased,
    /// as real quotes it back): the error and the line it comes from, or null
    /// when every check passes.
    /// </summary>
    private static (SimulatedSqlException Error, int Line)? CheckAddTypeBase(string physical)
    {
        var open = physical.IndexOf('(', StringComparison.Ordinal);
        var close = physical.IndexOf(')', StringComparison.Ordinal);
        var baseName = physical;
        string? sizes = null;
        if (close > 0 && close < physical.Length - 1)
            return (SimulatedSqlException.DataTypeDoesNotExist(physical), physical.Contains(',', StringComparison.Ordinal) ? 65 : 94);
        if (open > 0 && close == physical.Length - 1 && close > open + 1)
        {
            baseName = physical[..open].TrimEnd();
            sizes = physical[(open + 1)..close];
        }

        if (sizes == "max" && baseName is "varchar" or "nvarchar" or "varbinary")
            return (SimulatedSqlException.AddTypeCannotDefineMaxTypes(), 139);
        if (baseName == "xml")
            return (SimulatedSqlException.AddTypeFromXml(), 146);
        if (sizes is not null && Array.Exists(sizes.Split(','), static piece => !double.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return (SimulatedSqlException.DataTypeDoesNotExist(physical), 154);
        if (!AddTypeBaseTypes.Contains(baseName))
            return (SimulatedSqlException.DataTypeDoesNotExist(physical), 163);
        return null;
    }

    /// <summary>
    /// <c>sp_droptype @typename</c> drops an alias or table type of <c>dbo</c>
    /// by the statement <c>DROP TYPE</c>, which refuses a type still in use.
    /// Any other name — a system type, another schema's type, a missing one —
    /// is Msg 15036.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDropType(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_droptype", calledAs, arguments, DropTypeParameters);
        var name = values[0].IsNull ? null : values[0].AsString;
        var dbo = batch.CurrentDatabase.Schemas[Database.DefaultSchemaName];
        if (name is null || !(dbo.AliasTypes.TryGetValue(name, out var alias) ? alias is not null : dbo.TableTypes.ContainsKey(name)))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.DataTypeDoesNotExist(name ?? "(null)"), 14);

        var statement = "drop type [dbo].[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }
}
