using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TYPE_ID(name)</c>: returns the int <c>user_type_id</c> of a
/// system or user-defined type, or NULL when not found. The name argument
/// is a runtime string parsed as a 1- or 2-part dotted identifier
/// (<c>'dbo.MyType'</c> or <c>'MyType'</c>). System types resolve through
/// <see cref="BuiltInResources.SystypesRowData"/>; user-defined table
/// types resolve through <see cref="Schema.TableTypes"/>. Result type is
/// always <see cref="SqlType.Int32"/>.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-12): the common
/// <c>IF type_id('dbo.X') IS NOT NULL DROP TYPE dbo.X</c> idiom drives
/// most usage; the simulator's resolution matches the registry layer that
/// also backs <c>sys.types</c> / <c>sys.table_types</c>.
/// </remarks>
internal sealed class TypeId : Expression
{
    private readonly Expression nameArg;

    public TypeId(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("type_id", 1);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var nameValue = this.nameArg.Run(runtime);
        if (nameValue.IsNull)
            return SqlValue.Null(SqlType.Int32);

        var nameStr = nameValue.CoerceTo(SqlType.NVarchar).AsString.Trim();
        // Strip surrounding brackets on each segment (real SQL Server is
        // case-insensitive and tolerates bracketed names).
        var dotIndex = nameStr.IndexOf('.', StringComparison.Ordinal);
        string schemaPart, leafPart;
        if (dotIndex >= 0)
        {
            schemaPart = StripBrackets(nameStr[..dotIndex].Trim());
            leafPart = StripBrackets(nameStr[(dotIndex + 1)..].Trim());
        }
        else
        {
            schemaPart = Database.DefaultSchemaName;
            leafPart = StripBrackets(nameStr);
        }

        // System types resolve by name; user-defined table types resolve
        // through the schema's TableTypes dict.
        foreach (var row in BuiltInResources.SystypesRowData)
        {
            if (BuiltInToken.Equals((string)row[0]!, leafPart))
                return SqlValue.FromInt32(Convert.ToInt32(row[3]!, System.Globalization.CultureInfo.InvariantCulture));
        }

        return runtime.Batch.CurrentDatabase.Schemas.TryGetValue(schemaPart, out var schema)
            && schema.TableTypes.TryGetValue(leafPart, out var tableType)
                ? SqlValue.FromInt32(tableType.UserTypeId)
                : SqlValue.Null(SqlType.Int32);
    }

    private static string StripBrackets(string s) =>
        s.Length >= 2 && s[0] == '[' && s[^1] == ']' ? s[1..^1] : s;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumn) => SqlType.Int32;

    internal override string DebugDisplay() => $"TYPE_ID({this.nameArg.DebugDisplay()})";
}
