using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Dynamic Data Masking DDL: the MASKED WITH clause a column definition and
// ALTER COLUMN share, and ALTER COLUMN … ADD | DROP MASKED.
partial class Simulation
{
    /// <summary>
    /// Parses <c>MASKED WITH ( FUNCTION = '…' )</c> from the <c>MASKED</c>
    /// keyword and returns the function text, validated later against the
    /// column's resolved type (<see cref="MaskingFunction.Parse"/>). Leaves the
    /// cursor after the closing parenthesis. The text may be an
    /// <c>N'…'</c> literal.
    /// </summary>
    private static string ParseMaskedWithClause(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Function })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Literal { Value: { IsNull: false } text } || !SqlType.IsStringCategory(text.Type))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return text.AsString;
    }

    /// <summary>
    /// Applies <c>ALTER COLUMN … ADD MASKED WITH (…)</c> (a non-null
    /// <paramref name="functionText"/>) or <c>DROP MASKED</c>: a metadata
    /// change, the table's rows untouched. Probed 2026-09-27 against SQL
    /// Server 2025: a computed column is Msg 4928, a column a computed column
    /// reads is Msg 5074 then Msg 4922 whichever way, dropping where nothing is
    /// masked is Msg 16007, and adding over an existing mask replaces it.
    /// </summary>
    private static void AlterColumnMask(ParserContext context, HeapTable table, HeapColumn target, string? functionText)
    {
        if (target.Computed is not null)
            throw SimulatedSqlException.CannotAlterColumnOfKind(target.Name, "COMPUTED");

        List<(string Name, SimulatedSqlException.AlterColumnBlockerKind Kind)>? dependents = null;
        foreach (var column in table.Columns)
        {
            if (column.Computed is not { } computed)
                continue;
            var reads = false;
            computed.VisitColumnReferences(name => reads |= context.Batch.CurrentDatabase.Collation.Equals(name.Leaf, target.Name));
            if (reads)
                (dependents ??= []).Add((column.Name, SimulatedSqlException.AlterColumnBlockerKind.Column));
        }
        if (dependents is not null)
            throw SimulatedSqlException.ColumnHasDependencies("ALTER COLUMN", target.Name, dependents);

        var previous = target.MaskingFunction;
        MaskingFunction? next = null;
        if (functionText is null)
        {
            if (previous is null)
                throw SimulatedSqlException.ColumnHasNoMaskingFunction(target.Name);
        }
        else
        {
            next = MaskingFunction.Parse(functionText, target.Name, target.Type);
            context.Batch.Connection.Simulation.DeclaresDataMasks = true;
        }
        target.MaskingFunction = next;
        // A rolled-back ALTER puts the column's mask back (probed 2026-09-27).
        RecordDdlUndo(context, () => target.MaskingFunction = previous);
    }
}
