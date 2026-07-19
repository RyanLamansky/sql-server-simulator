using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FULLTEXTSERVICEPROPERTY('property_name')</c>: returns the value of a
/// Full-Text Service-level property. Unlike <c>SERVERPROPERTY</c> (which real
/// SQL Server projects as <c>sql_variant</c>), this function returns a plain
/// <see cref="SqlType.Int32"/> — probe-confirmed — so the result type is
/// always <c>int</c> regardless of whether the property-name argument is a
/// compile-time constant. The simulator reports Full-Text as installed
/// (<c>SERVERPROPERTY('IsFullTextInstalled') = 1</c>, and CREATE FULLTEXT
/// CATALOG / INDEX are modeled), so for self-consistency
/// <c>FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')</c> returns <c>1</c>; the
/// other documented resource-tuning properties return <c>0</c>. An
/// unrecognized property name returns a NULL <c>int</c> (matches the real
/// server convention). Property names are case-insensitive.
/// </summary>
internal sealed class FullTextServiceProperty : Expression
{
    private readonly Expression nameArg;

    public FullTextServiceProperty(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var n = this.nameArg.Run(runtime);
        if (n.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        // Longer than any recognized property name; also bounds the stackalloc
        // against an adversarially long argument.
        if (name.Length > 32)
            return SqlValue.Null(SqlType.Int32);
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "CONNECTTIMEOUT" => SqlValue.FromInt32(0),
            "ISFULLTEXTINSTALLED" => SqlValue.FromInt32(1),
            "LOADOSRESOURCES" => SqlValue.FromInt32(0),
            "RESOURCEUSAGE" => SqlValue.FromInt32(0),
            "VERIFYRESOURCEUSAGE" => SqlValue.FromInt32(0),
            _ => SqlValue.Null(SqlType.Int32),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => SqlType.Int32;

    internal override string DebugDisplay() => $"FULLTEXTSERVICEPROPERTY({this.nameArg.DebugDisplay()})";
}
