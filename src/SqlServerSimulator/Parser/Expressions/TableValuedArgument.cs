using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The argument a user-defined function call passes to a table-valued
/// parameter: a table variable declared with the parameter's own table type,
/// named bare or in parentheses. The table is looked up by name in the
/// calling batch as the call runs, so a cached plan resolves the replaying
/// batch's variable. Anything else passed there is real's Msg 206 against
/// the type — <c>NULL</c>, a scalar, a table variable of another type or one
/// declared inline (<c>table</c>) — raised while compiling (probed
/// 2026-10-04 against SQL Server 2025).
/// </summary>
internal sealed class TableValuedArgument : Expression
{
    private readonly string variableName;

    private TableValuedArgument(string variableName) => this.variableName = variableName;

    /// <summary>
    /// Parses the argument at the cursor for a parameter of
    /// <paramref name="tableType"/>. On return the cursor sits on the
    /// <c>,</c> or <c>)</c> after it.
    /// </summary>
    public static TableValuedArgument Parse(ParserContext context, TableType tableType)
    {
        var checkpoint = context.SaveCheckpoint();
        var depth = 0;
        while (context.Token is Operator { Character: '(' })
        {
            depth++;
            context.MoveNextRequired();
        }
        if (context.Token is AtPrefixedString variable
            && context.Batch.TryGetTableVariable(VariableKey(variable.Value), out var table))
        {
            context.MoveNextRequired();
            var closed = 0;
            while (closed < depth && context.Token is Operator { Character: ')' })
            {
                closed++;
                context.MoveNextRequired();
            }
            if (closed == depth && context.Token is Operator { Character: ',' or ')' })
            {
                return ReferenceEquals(table.DeclaredTableType, tableType)
                    ? new TableValuedArgument(VariableKey(variable.Value))
                    : throw SimulatedSqlException.OperandTypeClash(table.DeclaredTableType?.Name ?? "table", tableType.Name);
            }
        }
        context.RestoreCheckpoint(checkpoint);

        var argument = Expression.Parse(context);
        throw SimulatedSqlException.OperandTypeClash(
            IsUntypedNullLiteral(argument) ? "NULL" : SimulatedSqlException.FamilyRootName(argument.GetSqlType(context.Batch, name => throw SimulatedSqlException.InvalidColumnName(name.Leaf))),
            tableType.Name);
    }

    private static string VariableKey(string written) => written.StartsWith('@') ? written[1..] : written;

    /// <summary>The table the argument passes, in the batch the call runs in.</summary>
    public HeapTable Resolve(RuntimeContext runtime) => runtime.Batch.TableVariables![this.variableName];

    public override SqlValue Run(RuntimeContext runtime) => throw new InvalidOperationException("A table-valued argument has no scalar value.");

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "@" + this.variableName;

    internal override void Describe(NodeShape shape) => shape.Local(this.variableName);
}
