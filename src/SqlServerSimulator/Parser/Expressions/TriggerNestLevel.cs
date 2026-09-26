using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>TRIGGER_NESTLEVEL([object_id [, trigger_type, event_category]])</c>:
/// how many triggers are mid-fire on the session — 0 outside any. With an
/// object id, how many of the frames are that trigger's (an indirect
/// recursion counts each pass); with a type (<c>AFTER</c> / <c>IOT</c>) and
/// category (<c>DML</c> / <c>DDL</c>), only frames of that kind, an object id
/// of 0 counting every trigger. A DDL trigger counts as an AFTER one. A NULL
/// argument answers NULL, and a type or category real doesn't know, a type
/// without a category, or <c>IOT</c> with <c>DDL</c> is Msg 225 (probed
/// 2026-09-26 against SQL Server 2025).
/// </summary>
internal sealed class TriggerNestLevelFunction : Expression
{
    private readonly Expression? objectArg;
    private readonly Expression? typeArg;
    private readonly Expression? categoryArg;

    public TriggerNestLevelFunction(ParserContext context)
    {
        if (context.Token is Operator { Character: ')' })
            return;
        this.objectArg = Parse(context);
        if (context.Token is Operator { Character: ',' })
        {
            this.typeArg = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Operator { Character: ',' })
                this.categoryArg = Parse(context.MoveNextRequiredReturnSelf());
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var connection = runtime.Batch.Connection;
        if (this.objectArg is null)
            return SqlValue.FromInt32(connection.TriggerNestLevel);

        var objectValue = this.objectArg.Run(runtime);
        bool? after = null;
        bool? ddl = null;
        if (this.typeArg is not null)
        {
            if (this.categoryArg is null)
                throw SimulatedSqlException.TriggerNestLevelParametersNotValid();
            var typeValue = this.typeArg.Run(runtime);
            var categoryValue = this.categoryArg.Run(runtime);
            if (typeValue.IsNull || categoryValue.IsNull)
                return SqlValue.Null(SqlType.Int32);
            after = typeValue.CoerceTo(SqlType.NVarchar).AsString switch
            {
                var t when t.Equals("AFTER", StringComparison.OrdinalIgnoreCase) => true,
                var t when t.Equals("IOT", StringComparison.OrdinalIgnoreCase) => false,
                _ => throw SimulatedSqlException.TriggerNestLevelParametersNotValid(),
            };
            ddl = categoryValue.CoerceTo(SqlType.NVarchar).AsString switch
            {
                var c when c.Equals("DML", StringComparison.OrdinalIgnoreCase) => false,
                var c when c.Equals("DDL", StringComparison.OrdinalIgnoreCase) => true,
                _ => throw SimulatedSqlException.TriggerNestLevelParametersNotValid(),
            };
            if (after == false && ddl == true)
                throw SimulatedSqlException.TriggerNestLevelParametersNotValid();
        }
        if (objectValue.IsNull)
            return SqlValue.Null(SqlType.Int32);

        var objectId = objectValue.CoerceTo(SqlType.Int32).AsInt32;
        var count = 0;
        foreach (var frame in connection.FiringTriggers)
        {
            if ((objectId == 0 || frame.ObjectId == objectId)
                && (after is not { } wantAfter || (frame.IsAfter || frame.IsDdl) == wantAfter)
                && (ddl is not { } wantDdl || frame.IsDdl == wantDdl))
            {
                count++;
            }
        }
        return SqlValue.FromInt32(count);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.objectArg is not null)
            _ = AssignmentRules.ArgumentType(this.objectArg, SqlType.Int32, batch, resolveColumnType);
        if (this.typeArg is not null)
            _ = AssignmentRules.ArgumentType(this.typeArg, SqlType.NVarchar, batch, resolveColumnType);
        if (this.categoryArg is not null)
            _ = AssignmentRules.ArgumentType(this.categoryArg, SqlType.NVarchar, batch, resolveColumnType);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => "TRIGGER_NESTLEVEL(...)";

    internal override void Describe(NodeShape shape) => shape.Child(this.objectArg).Child(this.typeArg).Child(this.categoryArg);
}
