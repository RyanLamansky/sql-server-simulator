using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>[database.]$PARTITION.function(value)</c>: the 1-based partition number
/// <paramref name="functionName"/> maps the value to, as an <c>int</c> that is
/// never NULL. The value converts to the function's parameter type as an
/// assignment does. The function is looked up by name at each evaluation, so
/// a cached plan follows a split, a merge or a drop-and-recreate; a name that
/// resolves to nothing is Msg 208 — state 212 when the database qualifier
/// names no database, 213 otherwise — naming the call as written (probed
/// 2026-09-27 against SQL Server 2025).
/// </summary>
internal sealed class PartitionFunctionCall(string? databaseName, string functionName, string written, Expression argument) : Expression
{
    private readonly string? databaseName = databaseName;
    private readonly string functionName = functionName;
    private readonly string written = written;
    private readonly Expression argument = argument;

    /// <summary>
    /// Parses the call from its <c>$partition</c> token (the cursor's position
    /// on entry), leaving the cursor on the closing parenthesis.
    /// <paramref name="databaseName"/> is the qualifier written ahead of
    /// <c>$partition</c>, if any. No argument is Msg 102 at the parenthesis;
    /// a second is Msg 8144 state 80.
    /// </summary>
    public static PartitionFunctionCall Parse(ParserContext context, string? databaseName)
    {
        if (context.GetNextRequired() is not Operator { Character: '.' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var argument = Parse(context);
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.TooManyArgumentsToFunction(name.Value, state: 80);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var written = (databaseName is null ? "" : databaseName + ".") + "$partition." + name.Value;
        var call = new PartitionFunctionCall(databaseName, name.Value, written, argument);
        if (!context.Batch.IsSkipping)
            _ = call.Resolve(context.Batch);
        return call;
    }

    /// <summary>The function the call names, or Msg 208 when there is none.</summary>
    private PartitionFunction Resolve(BatchContext batch)
    {
        Database? database;
        if (this.databaseName is null)
            database = batch.CurrentDatabase;
        else if (!batch.Connection.Simulation.Databases.TryGetValue(this.databaseName, out database))
            throw SimulatedSqlException.PartitionInvalidObjectName(this.written, state: 212);
        return database.PartitionFunctions.TryGetValue(this.functionName, out var function)
            ? function
            : throw SimulatedSqlException.PartitionInvalidObjectName(this.written, state: 213);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var argumentType = this.argument.GetSqlType(batch, resolveColumnType);
        if (!batch.IsSkipping)
            AssignmentRules.RequireAssignable(this.argument, argumentType, this.Resolve(batch).ParameterType);
        return SqlType.Int32;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.argument.Run(runtime);
        if (runtime.Batch.IsSkipping)
            return SqlValue.FromInt32(1);
        var function = this.Resolve(runtime.Batch);
        if (value.IsNull)
            return SqlValue.FromInt32(function.PartitionOf(value));
        SqlValue converted;
        try
        {
            converted = value.CoerceTo(function.ParameterType);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.TryConversionOverflow(value, function.ParameterType)
                ?? SimulatedSqlException.ArithmeticOverflow(function.ParameterType.ToString()!);
        }
        return SqlValue.FromInt32(function.PartitionOf(converted));
    }

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => $"{this.written}({this.argument.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.LocalExact(this.written).Child(this.argument);
}
