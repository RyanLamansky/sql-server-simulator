using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    private static bool TryParseSet(ParserContext context)
    {
        var afterSet = context.GetNextRequired();

        if (afterSet is ReservedKeyword { Keyword: Keyword.Identity_Insert })
            return TryParseSetIdentityInsert(context);

        if (afterSet is AtPrefixedString variableToken)
            return TryParseSetVariable(context, variableToken);

        if (afterSet is not UnquotedString unquoted)
            return false;

        var setTarget = unquoted.Value;
        Span<char> upper = stackalloc char[setTarget.Length];
        return setTarget.ToUpperInvariant(upper) switch
        {
            7 => upper switch
            {
                "NOCOUNT" => context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
                _ => false
            },
            21 => upper switch
            {
                "IMPLICIT_TRANSACTIONS" => context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
                _ => false
            },
            _ => false
        };
    }

    /// <summary>
    /// Parses <c>SET @v = expr</c>. Resolves the slot via
    /// <see cref="BatchContext.GetVariableSlot"/> (Msg 137 if undeclared),
    /// evaluates the RHS with no FROM context, then coerces the result
    /// through the slot's declared type via
    /// <see cref="Parser.Expressions.Cast.ApplyCoercion"/> — preserves
    /// silent-truncation / Msg-245 / etc. semantics from the regular CAST
    /// path. Compound forms (<c>+=</c> / <c>-=</c> / etc.) aren't modeled
    /// in this bundle; rewrite as <c>SET @v = @v + expr</c>.
    /// </summary>
    private static bool TryParseSetVariable(ParserContext context, AtPrefixedString variableToken)
    {
        var slot = context.Batch.GetVariableSlot(variableToken.Value);

        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        context.MoveNextRequired();
        var rhs = Expression.Parse(context);
        if (context.Batch.IsSkipping)
            return true;
        var rhsValue = rhs.Run(new RuntimeContext(NoColumnResolver, context.Batch));
        slot.Value = Parser.Expressions.Cast.ApplyCoercion(rhsValue, slot.DeclaredType, slot.DeclaredMaxLength);
        return true;
    }

    /// <summary>
    /// Parses <c>SET IDENTITY_INSERT &lt;table&gt; ON|OFF</c>. ON sets the
    /// session's active <c>IDENTITY_INSERT</c> target after verifying no
    /// other table holds it (Msg 8107); OFF clears the target if it matches.
    /// </summary>
    private static bool TryParseSetIdentityInsert(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var tableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var onOff } || onOff is not (Keyword.On or Keyword.Off))
            return false;

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var heapTable))
            throw SimulatedSqlException.InvalidObjectName(tableName);

        if (onOff == Keyword.On)
        {
            if (context.Connection.IdentityInsertTable is string held && !Collation.Default.Equals(held, heapTable.Name))
                throw SimulatedSqlException.IdentityInsertAlreadyOn(held, heapTable.Name);
            context.Connection.IdentityInsertTable = heapTable.Name;
        }
        else if (Collation.Default.Equals(context.Connection.IdentityInsertTable, heapTable.Name))
        {
            context.Connection.IdentityInsertTable = null;
        }
        return true;
    }
}
