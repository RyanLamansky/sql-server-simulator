using System.Data;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator;

// The parameter declaration a parameterized text command runs under, and the
// Msg 8178 a declared parameter without a value raises before the batch runs.
partial class Simulation
{
    /// <summary>
    /// The first parameter <paramref name="command"/>'s declaration names
    /// without supplying a value, as Msg 8178, or null when every one is
    /// supplied. A parameter is unsupplied when the collection lacks it or
    /// holds it as an input whose value is C# null rather than
    /// <see cref="DBNull"/> — the parameter SqlClient declares and sends with
    /// its default flag and no value — and a declared default supplies it.
    /// The declaration is the one the client sent over the wire, or else the
    /// one SqlClient would build from the collection; real quotes it verbatim
    /// with the statement (probed 2026-10-09 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException? UnsuppliedParameter(SimulatedDbCommand command)
    {
        var parameters = command.Parameters;
        var declaration = command.ParameterDeclaration;
        if (declaration is null)
        {
            var anyUnsupplied = false;
            for (var i = 0; i < parameters.Count && !anyUnsupplied; i++)
                anyUnsupplied = IsUnsupplied(parameters[i]);
            if (!anyUnsupplied)
                return null;
            declaration = SqlClientParameterDeclaration(parameters);
        }

        foreach (var (name, hasDefault) in SplitParameterDeclaration(declaration))
        {
            if (hasDefault)
                continue;
            var bare = name.AsSpan(1);
            SimulatedDbParameter? supplied = null;
            for (var i = 0; i < parameters.Count && supplied is null; i++)
            {
                var parameter = parameters[i];
                var parameterName = parameter.ParameterName.AsSpan();
                if (parameterName.StartsWith('@'))
                    parameterName = parameterName[1..];
                if (parameterName.Equals(bare, StringComparison.OrdinalIgnoreCase))
                    supplied = parameter;
            }
            if (supplied is null || IsUnsupplied(supplied))
                return SimulatedSqlException.ParameterizedQueryExpectsParameter(declaration, command.CommandText, name).PinLine(0);
        }
        return null;

        static bool IsUnsupplied(SimulatedDbParameter parameter) =>
            parameter.Value is null
            && parameter.Direction is ParameterDirection.Input or ParameterDirection.InputOutput
            && parameter.TypeName.Length == 0;
    }

    /// <summary>
    /// The declaration SqlClient's <c>sp_executesql</c> call carries for
    /// <paramref name="parameters"/>: each non-return parameter in order,
    /// comma-joined without spaces, its type spelled from the parameter's
    /// <see cref="DbType"/> and size as SqlClient spells it — a MAX type with
    /// a trailing space — and an output-capable one suffixed <c>output</c>
    /// (probed 2026-10-09 against SQL Server 2025 through SqlClient 7).
    /// </summary>
    private static string SqlClientParameterDeclaration(SimulatedDbParameterCollection parameters)
    {
        var declaration = new StringBuilder();
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.Direction is ParameterDirection.ReturnValue)
                continue;
            if (declaration.Length != 0)
                _ = declaration.Append(',');
            if (!parameter.ParameterName.StartsWith('@'))
                _ = declaration.Append('@');
            _ = declaration.Append(parameter.ParameterName).Append(' ').Append(SqlClientTypeName(parameter));
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                _ = declaration.Append(" output");
        }
        return declaration.ToString();

        static string SqlClientTypeName(SimulatedDbParameter parameter)
        {
            var size = parameter.Size;
            return parameter.DbType switch
            {
                DbType.AnsiString => Sized("varchar", 8000),
                DbType.AnsiStringFixedLength => Sized("char", 8000),
                DbType.Binary => Sized("varbinary", 8000),
                DbType.Boolean => "bit",
                DbType.Byte => "tinyint",
                DbType.Currency => "money",
                DbType.Date => "date",
                DbType.DateTime => "datetime",
                DbType.DateTime2 => Scaled("datetime2"),
                DbType.DateTimeOffset => Scaled("datetimeoffset"),
                DbType.Decimal => string.Create(CultureInfo.InvariantCulture, $"decimal({(parameter.Precision == 0 ? 29 : parameter.Precision)},{parameter.Scale})"),
                DbType.Double => "float",
                DbType.Guid => "uniqueidentifier",
                DbType.Int16 => "smallint",
                DbType.Int32 => "int",
                DbType.Int64 => "bigint",
                DbType.Object => "sql_variant",
                DbType.Single => "real",
                DbType.StringFixedLength => Sized("nchar", 4000),
                DbType.Time => Scaled("time"),
                DbType.Xml => "xml",
                _ => Sized("nvarchar", 4000),
            };

            string Sized(string name, int limit) =>
                size < 0 || size > limit ? $"{name}(max) "
                : string.Create(CultureInfo.InvariantCulture, $"{name}({(size == 0 ? limit : size)})");

            string Scaled(string name) => string.Create(CultureInfo.InvariantCulture, $"{name}({(parameter.Scale == 0 ? 7 : parameter.Scale)})");
        }
    }

    /// <summary>
    /// The parameters a declaration string like <c>@a int, @b decimal(10,2)
    /// OUTPUT, @c int = 5</c> declares, in order, each name with its <c>@</c>
    /// and whether it declares a default, honoring parenthesized type
    /// arguments and quoted defaults when splitting.
    /// </summary>
    internal static List<(string Name, bool HasDefault)> SplitParameterDeclaration(string declaration)
    {
        var declared = new List<(string, bool)>();
        var depth = 0;
        var quoted = false;
        var hasDefault = false;
        var segmentStart = 0;
        for (var i = 0; i <= declaration.Length; i++)
        {
            if (i < declaration.Length)
            {
                var c = declaration[i];
                if (c == '\'')
                    quoted = !quoted;
                if (quoted)
                    continue;
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;
                else if (c == '=' && depth == 0)
                    hasDefault = true;

                if (c != ',' || depth != 0)
                    continue;
            }

            var segment = declaration.AsSpan(segmentStart, i - segmentStart).Trim();
            segmentStart = i + 1;
            var segmentHasDefault = hasDefault;
            hasDefault = false;
            if (segment.Length == 0 || segment[0] != '@')
                continue;

            var end = 0;
            while (end < segment.Length && !char.IsWhiteSpace(segment[end]))
                end++;

            declared.Add((segment[..end].ToString(), segmentHasDefault));
        }

        return declared;
    }
}
