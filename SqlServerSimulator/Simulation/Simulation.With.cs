using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses a <c>WITH cte_name [(col, …)] AS (SELECT …) [, …]</c> prefix
    /// and registers each binding on <paramref name="context"/>'s
    /// <see cref="ParserContext.CteBindings"/>. The bindings live for the
    /// immediately-following statement only — the statement loop clears
    /// the slot on its next iteration. Each CTE name is registered as a
    /// sentinel (null <see cref="CteBinding.Plan"/>) before its body is
    /// parsed so a self-reference inside the body resolves to the sentinel
    /// and surfaces as <see cref="NotSupportedException"/> from
    /// <c>ParseSingleFromSource</c> (recursive CTEs aren't modeled).
    /// </summary>
    /// <remarks>
    /// On entry <see cref="ParserContext.Token"/> is the <c>WITH</c>
    /// keyword. On return it sits on the first token of the dispatched
    /// statement (<c>SELECT</c> / <c>INSERT</c> / <c>UPDATE</c> /
    /// <c>DELETE</c> / <c>MERGE</c>) so the surrounding switch can resume
    /// dispatch.
    /// </remarks>
    private static void ParseCteBindings(ParserContext context)
    {
        var bindings = new Dictionary<string, CteBinding>(StringComparer.OrdinalIgnoreCase);
        context.CteBindings = bindings;

        while (true)
        {
            if (context.GetNextRequired() is not Name cteName)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (bindings.ContainsKey(cteName.Value))
                throw SimulatedSqlException.DuplicateCteName(cteName.Value);

            string[]? renameList = null;
            context.MoveNextRequired();
            if (context.Token is Operator { Character: '(' })
            {
                renameList = ParseCteColumnRenameList(context);
                context.MoveNextRequired();
            }

            if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            // Register the sentinel before parsing the body so a self-
            // reference resolves to a null-plan binding (which surfaces as
            // NotSupportedException at FROM-resolution time). Replaced
            // with the real plan once the body's Selection materializes.
            var binding = new CteBinding(cteName.Value, []);
            bindings[cteName.Value] = binding;

            // depth: 1 because the body sits inside `(...)` — Selection's
            // projection loop only treats a `)` as a Selection-terminator
            // when depth > 0; depth 0 raises Msg 102 on `)`.
            var body = Selection.Parse(context, depth: 1);

            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (body.HasOrderBy && !body.HasTopOrOffsetOrFetch)
                throw SimulatedSqlException.OrderByInvalidInCte();

            string[] columnNames;
            if (renameList is not null)
            {
                if (renameList.Length < body.Schema.Length)
                    throw SimulatedSqlException.CteHasMoreColumnsThanList(cteName.Value);
                if (renameList.Length > body.Schema.Length)
                    throw SimulatedSqlException.CteHasFewerColumnsThanList(cteName.Value);
                columnNames = renameList;
            }
            else
            {
                columnNames = body.ColumnNames;
            }

            // Replace the sentinel using a fresh CteBinding (the rename-
            // promoted column names + the resolved plan).
            bindings[cteName.Value] = new CteBinding(cteName.Value, columnNames) { Plan = body };

            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ',' })
                break;
        }
    }

    /// <summary>
    /// Parses the optional <c>(col1, col2, …)</c> column-rename list that
    /// follows a CTE name. Enters with <see cref="ParserContext.Token"/>
    /// on the <c>(</c>; on return <see cref="ParserContext.Token"/> sits
    /// on the closing <c>)</c>.
    /// </summary>
    private static string[] ParseCteColumnRenameList(ParserContext context)
    {
        var names = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name columnName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(columnName.Value);

            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
                return [.. names];
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
