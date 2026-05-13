using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>OBJECT_NAME(object_id [, database_id])</c>: returns the leaf
/// name of the object with the given <c>object_id</c>, or NULL when not
/// found. The optional database-id argument is accepted and ignored —
/// the simulator hosts one database per <see cref="Simulation"/> instance.
/// Walks <see cref="Database.Schemas"/> → <see cref="Schema.SchemaObjects"/>
/// (the shared object-name namespace: tables / views / functions /
/// procedures / sequences / triggers) plus <see cref="Schema.TableTypes"/>.
/// Probe-confirmed against SQL Server 2025 (2026-05-13): NULL argument
/// returns NULL; missing / negative id returns NULL; result type is
/// <see cref="SqlType.SystemName"/>.
/// </summary>
internal sealed class ObjectName : Expression
{
    private readonly Expression idArg;

    public ObjectName(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            // database_id arg: accepted and ignored (single-DB simulator).
            _ = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Tokens.Operator { Character: ',' })
                throw SimulatedSqlException.FunctionRequiresNArguments("object_name", 2);
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        foreach (var schema in runtime.Batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.ObjectId == id)
                    return SqlValue.FromString(SqlType.SystemName, obj.Name);
            }
            foreach (var tableType in schema.TableTypes.Values)
            {
                if (tableType.ObjectId == id)
                    return SqlValue.FromString(SqlType.SystemName, tableType.Name);
            }
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => $"OBJECT_NAME({this.idArg.DebugDisplay()})";
}
