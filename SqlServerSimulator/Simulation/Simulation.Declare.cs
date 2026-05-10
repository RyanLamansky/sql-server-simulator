using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DECLARE @v TYPE [= expr] [, @w TYPE [= expr] ...]</c>.
    /// Variables register on <see cref="BatchContext.Variables"/> with their
    /// declared type and (optional) initializer-evaluated value, defaulting
    /// to typed NULL. Re-declaring an existing name (including a name
    /// occupied by a SqlClient parameter) raises Msg 134.
    /// </summary>
    /// <remarks>
    /// On entry the cursor is on the <c>DECLARE</c> keyword. On return the
    /// cursor sits on the first token after the last declaration — typically
    /// a <c>;</c>, the next statement keyword, or end of batch.
    /// </remarks>
    private static int? TryParseDeclare(ParserContext context)
    {
        var rowsAffected = (int?)null;

        do
        {
            if (context.GetNextRequired() is not AtPrefixedString variableToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var variableName = variableToken.Value;
            if (context.Batch.Variables.ContainsKey(variableName))
                throw SimulatedSqlException.VariableAlreadyDeclared(variableName);

            // Optional AS keyword between name and type spec — `DECLARE @v AS INT`.
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.As })
                context.MoveNextRequired();

            var (declaredType, declaredMaxLength) = ParseDeclareTypeSpec(context, variableName);

            // Optional initializer.
            var initialValue = SqlValue.Null(declaredType);
            if (context.Token is Operator { Character: '=' })
            {
                context.MoveNextRequired();
                var initExpression = Expression.Parse(context);
                initialValue = Parser.Expressions.Cast.ApplyCoercion(initExpression.Run(NoColumnResolver), declaredType, declaredMaxLength);
                rowsAffected = 1; // initializer counts as one row for @@ROWCOUNT (probe-confirmed)
            }

            context.Batch.Variables[variableName] = new VariableSlot(declaredType, declaredMaxLength, initialValue, parameter: null);
        } while (context.Token is Operator { Character: ',' });

        return rowsAffected;
    }

    /// <summary>
    /// Parses a SqlType reference following a variable name in <c>DECLARE</c>:
    /// a type-name token plus optional <c>(N)</c> / <c>(p, s)</c> spec,
    /// resolving via <see cref="SqlType.GetByName"/>. On entry the cursor is
    /// on the type-name token; on return it sits one past the type spec.
    /// Length/scale information beyond the SqlType (e.g. <c>varchar(N)</c>'s
    /// max-length) is captured by length-bearing singleton variants of the
    /// type itself when applicable.
    /// </summary>
    private static (SqlType Type, int? MaxLength) ParseDeclareTypeSpec(ParserContext context, string variableName)
    {
        if (context.Token is not Name typeName)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : context.MatchContextual(ContextualKeyword.Max)
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);

            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    declaredScale = scaleValue.AsInt32;
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            context.MoveNextRequired();
        }

        return SqlType.GetByName(typeName, declaredMaxLength, declaredScale, 1, variableName);
    }

    /// <summary>
    /// Column resolver passed when running an expression that has no FROM
    /// clause (DECLARE initializer, SET RHS). Any column reference in such
    /// an expression should fail at evaluate time as an unknown identifier;
    /// this resolver provides a default by raising Msg 207.
    /// </summary>
    internal static SqlValue NoColumnResolver(MultiPartName name) =>
        throw SimulatedSqlException.InvalidColumnName(name);
}
