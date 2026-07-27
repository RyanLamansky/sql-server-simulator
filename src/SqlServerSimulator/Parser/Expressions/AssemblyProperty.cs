using SqlServerSimulator.Clr;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ASSEMBLYPROPERTY('assembly_name', 'property')</c>: returns a
/// <c>sql_variant</c> carrying one manifest fact about a registered CLR
/// assembly. An unknown assembly or unrecognized property returns NULL;
/// property names are case-insensitive.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025: the four <c>Version*</c> properties
/// report the manifest version as <c>int</c>s, <c>SimpleName</c> /
/// <c>Architecture</c> / <c>MvID</c> / <c>CLRName</c> report strings, and
/// <c>PublicKey</c> / <c>Culture</c> read NULL for an unsigned,
/// neutral-culture assembly. Note that <c>CLRName</c>'s embedded version reads
/// <c>0.0.0.0</c> for an unsigned assembly even though <c>VersionMajor</c> and
/// friends report the real manifest version off the same bytes — see
/// <see cref="ClrAssemblyMetadata.BuildClrName"/>.
/// Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/assemblyproperty-transact-sql
/// </remarks>
internal sealed class AssemblyProperty : Expression
{
    private readonly Expression assemblyArg;
    private readonly Expression propertyArg;

    public AssemblyProperty(ParserContext context)
    {
        this.assemblyArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var assemblyValue = this.assemblyArg.Run(runtime);
        var propertyValue = this.propertyArg.Run(runtime);
        if (assemblyValue.IsNull || propertyValue.IsNull)
            return SqlValue.Null(SqlType.SqlVariant);

        var assemblyName = assemblyValue.CoerceTo(SqlType.NVarchar).AsString;
        if (!runtime.Batch.CurrentDatabase.Assemblies.TryGetValue(assemblyName, out var assembly))
            return SqlValue.Null(SqlType.SqlVariant);

        var property = propertyValue.CoerceTo(SqlType.NVarchar).AsString;
        if (property.Length > 32)
            return SqlValue.Null(SqlType.SqlVariant);

        var identity = ClrAssemblyMetadata.ReadIdentity(assembly.Content, "CREATE", assembly.Name);
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "ARCHITECTURE" => Text("msil"),
            "CLRNAME" => Text(assembly.ClrName),
            // NULL for the neutral culture SQLCLR assemblies carry.
            "CULTURE" => identity.Culture.Length == 0 ? SqlValue.Null(SqlType.SqlVariant) : Text(identity.Culture),
            "MVID" => Text(identity.Mvid.ToString().ToUpperInvariant()),
            // NULL unless the assembly is strong-named.
            "PUBLICKEY" => identity.PublicKey.Length == 0
                ? SqlValue.Null(SqlType.SqlVariant)
                : SqlValue.FromVariant(SqlValue.FromVarbinary(identity.PublicKey)),
            "SIMPLENAME" => Text(identity.Name),
            "VERSIONBUILD" => Number(identity.Version.Build),
            "VERSIONMAJOR" => Number(identity.Version.Major),
            "VERSIONMINOR" => Number(identity.Version.Minor),
            "VERSIONREVISION" => Number(identity.Version.Revision),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };

        static SqlValue Text(string value) => SqlValue.FromVariant(SqlValue.FromNVarchar(value));
        static SqlValue Number(int value) => SqlValue.FromVariant(SqlValue.FromInt32(value));
    }

    internal override string DebugDisplay() =>
        $"ASSEMBLYPROPERTY({this.assemblyArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
