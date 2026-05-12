using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
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
    /// Parses <c>SET @v = expr</c> and the compound forms <c>SET @v += expr</c>
    /// / <c>-=</c> / <c>*=</c> / <c>/=</c> / <c>%=</c> / <c>&amp;=</c> / <c>|=</c>
    /// / <c>^=</c>. Resolves the slot via <see cref="BatchContext.GetVariableSlot"/>
    /// (Msg 137 if undeclared); compound forms desugar to the equivalent
    /// <c>FromCompoundOp(op, VariableReference(@v), rhs)</c> so the existing
    /// arithmetic / string-concat dispatch runs unchanged (NULL propagates,
    /// string <c>+=</c> concatenates, decimal/money widening matches plain
    /// <c>+</c>). The compound op's two characters must be adjacent in the
    /// source (probe-confirmed: <c>SET @v + = 5</c> with a space raises
    /// Msg 102 near <c>'+'</c>). After the arithmetic step the result is
    /// coerced through the slot's declared type via
    /// <see cref="Cast.ApplyCoercion"/>, preserving
    /// silent-truncation / Msg-245 semantics from the regular CAST path.
    /// </summary>
    private static bool TryParseSetVariable(ParserContext context, AtPrefixedString variableToken)
    {
        var slot = context.Batch.GetVariableSlot(variableToken.Value);

        context.MoveNextRequired();
        if (TryConsumeAssignmentOperator(context) is not char assignOp)
            return false;

        context.MoveNextRequired();
        var rhs = Expression.Parse(context);
        if (context.Batch.IsSkipping)
            return true;
        var assignedExpr = assignOp == '='
            ? rhs
            : TwoSidedExpression.FromCompoundOp(assignOp, new VariableReference(variableToken, context), rhs);
        var rhsValue = assignedExpr.Run(new RuntimeContext(NoColumnResolver, context.Batch));
        slot.Value = Cast.ApplyCoercion(rhsValue, slot.DeclaredType, slot.DeclaredMaxLength);
        return true;
    }

    /// <summary>
    /// At the current token position, detects whether the parser is sitting
    /// on the assignment-operator slot of a SET / UPDATE-SET statement.
    /// Returns <c>'='</c> for a plain assignment (one token consumed), the
    /// arithmetic char for compound (<c>+ - * / % &amp; | ^</c>, two tokens
    /// consumed), or <c>null</c> when the position isn't a recognized
    /// assignment operator (caller raises Msg 102). Compound forms require
    /// the arith char and the trailing <c>=</c> to be adjacent in source
    /// (no intervening whitespace) — probe-confirmed against SQL Server 2025.
    /// On a successful match, <see cref="ParserContext.Token"/> is left at
    /// the last consumed operator token; callers advance once more to step
    /// onto the RHS first token.
    /// </summary>
    private static char? TryConsumeAssignmentOperator(ParserContext context) =>
        context.Token is not Operator first
            ? null
            : first.Character == '='
                ? '='
                : first.Character is not ('+' or '-' or '*' or '/' or '%' or '&' or '|' or '^')
                    ? null
                    : context.GetNextRequired() is not Operator { Character: '=' } second || second.StartIndex != first.EndIndex
                        ? null
                        : first.Character;

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
