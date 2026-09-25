using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>IDENTITY(type [, seed, increment])</c> in a <c>SELECT … INTO</c> select
/// list: the new table's identity column. It has no value of its own —
/// <see cref="Run"/> is a typed NULL the copy into the destination replaces
/// with the column's next identity value, row by row in the query's order.
/// </summary>
/// <remarks>
/// Real takes it only as a whole select-list item of a statement-level query,
/// and only with a column alias: anywhere else the <c>IDENTITY</c> keyword
/// is a syntax error, and a query without <c>INTO</c> is Msg 177 (probed
/// 2026-09-24 against SQL Server 2025).
/// </remarks>
internal sealed class IdentityFunction : Expression
{
    public readonly SqlType Type;

    private readonly IdentitySpec spec = IdentitySpec.Default;

    /// <summary>
    /// The seed and increment, judged against <see cref="Type"/> by
    /// <see cref="Resolve"/> once the alias their errors name is read.
    /// </summary>
    public IdentityState? Identity;

    /// <summary>
    /// Parses the function. Enters on the <c>IDENTITY</c> keyword and leaves
    /// on the token after the closing paren.
    /// </summary>
    public IdentityFunction(ParserContext context)
    {
        var keyword = (ReservedKeyword)context.Token!;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);
        context.MoveNextRequired();
        var typeName = TypeNameSynonyms.TryFoldMultiWordType(context)
            ?? context.Token as Name
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        // The type is a bare system type name: a schema-qualified one is a
        // syntax error at its dot (probed 2026-09-24 against SQL Server 2025).
        var afterName = context.SaveCheckpoint();
        if (context.GetNextOptional() is Operator { Character: '.' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.RestoreCheckpoint(afterName);
        (this.Type, _) = Cast.ParseTargetTypeSpec(context, typeName);
        // The type is judged before the arguments are read, and its error
        // names the type as written where a column declaration names the column.
        if (!IdentityState.IsIdentityType(this.Type))
            throw SimulatedSqlException.IdentityInvalidType(typeName.Value, state: 1);
        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            this.spec = IdentitySpec.ReadArguments(context);
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
    }

    /// <summary>Judges the seed and increment for the column named <paramref name="alias"/>.</summary>
    public void Resolve(string alias) => this.Identity = this.spec.Resolve(this.Type, alias);

    public override SqlValue Run(RuntimeContext runtime) => SqlValue.Null(this.Type);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Type;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => $"IDENTITY({this.Type}, {this.Identity?.Seed}, {this.Identity?.Increment})";

    internal override void Describe(NodeShape shape) => shape.Local(this.Type).Local(this.Identity!.Seed).Local(this.Identity.Increment);
}
