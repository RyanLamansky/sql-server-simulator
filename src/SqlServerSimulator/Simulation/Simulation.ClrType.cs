using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the <c>EXTERNAL NAME assembly.[class]</c> tail of
    /// <c>CREATE TYPE</c>, binds the class and registers the CLR type. Cursor
    /// on entry: the <c>EXTERNAL</c> word.
    /// </summary>
    /// <remarks>
    /// Probed 2026-09-28 against SQL Server 2025: a third name part is Msg 102
    /// at its dot; a taken name is Msg 219, a missing assembly Msg 6267 and a
    /// missing class Msg 6556 (then Msg 6597); the class's own conformance
    /// errors follow (<see cref="ClrUserDefinedType.Bind"/>), and a class
    /// another type already maps is Msg 8188.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = Clr.TrimmingJustification.RegisteredAssembly)]
    private static bool ParseCreateClrType(ParserContext context, Schema schema, MultiPartName typeName)
    {
        if (context.GetNextRequired() is not Name nameWord || !nameWord.Value.Equals("NAME", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name assemblyToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '.' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name classToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '.' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.Batch.IsSkipping)
            return true;

        if (schema.TableTypes.ContainsKey(typeName.Leaf) || schema.AliasTypes.ContainsKey(typeName.Leaf))
            throw SimulatedSqlException.TypeAlreadyExists(typeName.ToString());

        var database = schema.Database;
        if (!database.Assemblies.TryGetValue(assemblyToken.Value, out var assembly))
            throw SimulatedSqlException.AssemblyNotFoundForType(assemblyToken.Value);
        if (!context.Batch.Connection.Simulation.EnableClr)
            throw SimulatedSqlException.ClrExecutionDisabled();
        var type = assembly.Load().GetType(classToken.Value)
            ?? throw SimulatedSqlException.Aggregate([
                SimulatedSqlException.ClrTypeClassNotFound(classToken.Value, assembly.Name),
                SimulatedSqlException.ClrCreateTypeFailed()]);

        var udt = ClrUserDefinedType.Bind(schema, typeName.Leaf, assembly, classToken.Value, type, database.AllocateUserTypeId());
        if (ClrUserDefinedType.FindByClrType(database, type) is not null)
            throw SimulatedSqlException.ClrTypeAlreadyMapped(classToken.Value, assembly.Name);

        schema.AliasTypes[typeName.Leaf] = new AliasType(
            schema,
            typeName.Leaf,
            underlyingType: udt.SqlType,
            declaredMaxLength: udt.MaxByteSize,
            declaredPrecision: null,
            declaredScale: null,
            isNullable: true,
            userTypeId: udt.UserTypeId,
            createDate: context.Batch.CurrentStatement.UtcNow,
            spelledNumeric: false);
        RecordSlotUndo<AliasType>(context, schema.AliasTypes, typeName.Leaf, null);
        RecordDdlEvent(context, "CREATE_TYPE", schema.Name, typeName.Leaf, "TYPE");
        return true;
    }

    /// <summary>
    /// The first CLR type that maps a class of <paramref name="assembly"/>,
    /// which <c>DROP ASSEMBLY</c> refuses to orphan (Msg 6598).
    /// </summary>
    private static string? FindClrTypeDependent(Database database, SqlAssembly assembly)
    {
        AliasType? first = null;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var alias in schema.AliasTypes.Values)
            {
                if (alias.UnderlyingType is ClrUdtSqlType { Udt.Assembly: var owner } && owner == assembly
                    && (first is null || alias.UserTypeId < first.UserTypeId))
                {
                    first = alias;
                }
            }
        }

        return first?.Name;
    }
}
