using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>UPDATE(column)</c> — true when the firing statement's column list
/// included the named column. An INSERT reports every column true
/// whatever its column list named; an UPDATE reports the SET-clause columns,
/// whether or not the assignment changed the value and even when the
/// statement matched no rows; a DELETE reports every column false. All
/// probe-confirmed against SQL Server 2025.
/// </summary>
/// <remarks>
/// <para>
/// A predicate rather than a built-in scalar because that's what real
/// accepts: <c>SELECT UPDATE(c1)</c> raises Msg 156, so the construct is
/// legal only where a boolean is expected. Modeling it as a bit-returning
/// function in <c>ResolveBuiltIn</c> would accept a shape real rejects.
/// </para>
/// <para>
/// The column resolves to its stable <c>column_id</c> at parse time, which
/// for a trigger body is each time the body fires (bodies are re-tokenized
/// per fire). Real resolves at CREATE TRIGGER and raises Msg 207 there;
/// the simulator's deferred module-body validation moves that to the first
/// fire — the same asymmetry every other trigger-body name reference has.
/// </para>
/// </remarks>
internal sealed class UpdatePredicate : BooleanExpression
{
    private readonly int columnId;
    private readonly string columnName;

    private UpdatePredicate(int columnId, string columnName)
    {
        this.columnId = columnId;
        this.columnName = columnName;
    }

    /// <summary>
    /// Parses <c>UPDATE ( column )</c> with the cursor on the <c>UPDATE</c>
    /// keyword. Only a bare column name is accepted — real raises Msg 102
    /// near <c>'.'</c> for a qualified name and near <c>')'</c> for the
    /// no-arg form.
    /// </summary>
    public static new BooleanExpression Parse(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        if (context.Token is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnName = name.Value;

        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        // Outside a trigger body the construct has nothing to report on.
        // Real's wording names the IF form even when it appears elsewhere.
        return context.Batch.TriggerFrame is not { } frame
            ? throw SimulatedSqlException.UpdateOnlyWithinCreateTrigger()
            : new UpdatePredicate(ResolveColumnId(frame.Trigger, columnName), columnName);
    }

    /// <summary>
    /// Maps the named column of the trigger's parent to its stable
    /// <c>column_id</c>, raising Msg 207 when the parent has no such column.
    /// A view parent has no stable ids, so its ordinals stand in — matching
    /// how the mask is built for a view-parented INSTEAD OF trigger.
    /// </summary>
    private static int ResolveColumnId(Trigger trigger, string columnName)
    {
        var columns = trigger.Parent switch
        {
            HeapTable table => table.Columns,
            View view => view.OutputColumns,
            _ => null,
        };
        if (columns is not null)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (Collation.Baseline.Equals(columns[i].Name, columnName))
                    return trigger.Parent is HeapTable ? columns[i].ColumnId : i + 1;
            }
        }
        throw SimulatedSqlException.InvalidColumnName(columnName);
    }

    public override bool? Run(RuntimeContext runtime) =>
        runtime.Batch.TriggerFrame is { } frame && frame.IsColumnUpdated(this.columnId);

    // No operand expressions: the column is resolved to an id at parse time,
    // so there's no Expression child for a visitor to reach.
    internal override void VisitOperandExpressions(Action<Expression> visitor)
    {
    }

    internal override string DebugDisplay() => $"UPDATE({this.columnName})";
}
