using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// A scalar function body's value or condition as its first parse built it —
/// a <c>SET</c>'s or <c>DECLARE</c>'s value, a <c>RETURN</c>'s, an
/// <c>IF</c>'s or <c>WHILE</c>'s condition — with where its text ends and
/// what it parsed under, which a later call takes again in place of reading
/// the tokens (see <c>Simulation.ParseBodyExpression</c>). The statement
/// around it still runs as written: only the expression's parse is skipped.
/// </summary>
internal sealed class BodyExpressionPlan(
    ExpressionNode parsed, ParserContext.Checkpoint end, long schemaVersion, Database database, ParserContext context, bool blocksSimpleParameterization)
{
    public readonly ExpressionNode Parsed = parsed;
    public readonly ParserContext.Checkpoint End = end;

    /// <summary>The <see cref="Simulation.SchemaVersion"/> the expression parsed under.</summary>
    public readonly long SchemaVersion = schemaVersion;

    /// <summary>The database the expression parsed in, and its compatibility level and collation then.</summary>
    public readonly Database Database = database;

    /// <inheritdoc cref="Database"/>
    public readonly CompatibilityLevel CompatibilityLevel = database.CompatibilityLevel;

    /// <inheritdoc cref="Database"/>
    public readonly string CollationName = database.CollationName;

    /// <summary>The session settings a parse reads, as they stood.</summary>
    public readonly bool QuotedIdentifiers = context.QuotedIdentifiers, AnsiNulls = context.Connection.AnsiNulls, ConcatNullYieldsNull = context.Connection.ConcatNullYieldsNull;

    /// <inheritdoc cref="QuotedIdentifiers"/>
    public readonly DateOrder DateFormat = context.Connection.DateFormat;

    /// <summary>Whether the parse set <see cref="ParserContext.SimpleParameterizationBlocked"/>, which taking it again repeats.</summary>
    public readonly bool BlocksSimpleParameterization = blocksSimpleParameterization;
}
