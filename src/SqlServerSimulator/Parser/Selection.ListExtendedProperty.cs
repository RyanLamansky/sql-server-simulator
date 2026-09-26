using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Built-in system TVF <c>fn_listextendedproperty</c>: seven arguments —
    /// the property name, then three <c>(type, name)</c> level pairs — each
    /// a value or the <c>DEFAULT</c> keyword, which is NULL. Returns
    /// <c>(objtype varchar(128), objname sysname, name sysname, value
    /// sql_variant)</c>, one row per live extended property at the addressed
    /// level (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// The level types given, from level 0 down, fix how deep the listing
    /// reaches; every name above the deepest must be given, and the deepest's
    /// name may be NULL for all of that kind beneath the rest. A name or type
    /// past that depth, a missing name above it, a level type the procedures
    /// don't know, or a target that doesn't exist all list nothing rather than
    /// raise — and the string <c>'default'</c> is a name like any other.
    /// With no level types at all, the listing is the database's own
    /// properties, whose <c>objtype</c> and <c>objname</c> are NULL.
    /// </remarks>
    public static Selection ParseListExtendedProperty(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var arguments = new Expression?[7];
        context.MoveNextRequired();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
            {
                if (context.Token is Operator { Character: ')' })
                    throw SimulatedSqlException.InsufficientArgumentsToFunction("fn_listextendedproperty", state: 3);
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
                context.MoveNextRequired();
            else
                arguments[i] = Expression.Parse(context);
        }
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.TooManyArgumentsToFunction("fn_listextendedproperty");
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var catalogCollation = Collation.Get("Latin1_General_CI_AI");
        SqlType[] schema =
        [
            VarcharSqlType.Get(128, catalogCollation, Coercibility.Implicit),
            NVarcharSqlType.Get(128, catalogCollation, Coercibility.Implicit),
            NVarcharSqlType.Get(128, catalogCollation, Coercibility.Implicit),
            SqlType.SqlVariant,
        ];
        string[] columnNames = ["objtype", "objname", "name", "value"];

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateListExtendedPropertyRows(schema, arguments, batch, outerResolver))
        {
            ColumnNullability = [true, true, false, true],
        };
    }

    private static IEnumerable<byte[]> EnumerateListExtendedPropertyRows(
        SqlType[] schema,
        Expression?[] arguments,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);
        var values = new string?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i]?.Run(runtime) is { IsNull: false } value)
                values[i] = value.CoerceTo(SqlType.NVarchar).AsString;
        }

        // The listing's depth is the run of level types given from level 0;
        // every name above the deepest must be given, and nothing may follow.
        var depth = 0;
        while (depth < 3 && values[1 + (depth * 2)] is not null)
            depth++;
        for (var level = 0; level < 3; level++)
        {
            var type = values[1 + (level * 2)];
            var name = values[2 + (level * 2)];
            if (level >= depth ? type is not null || name is not null : level < depth - 1 && name is null)
                yield break;
        }

        var database = batch.CurrentDatabase;
        var targets = new ExtendedPropertyTargets(database);
        var nameFilter = values[0];
        foreach (var (key, value) in database.ExtendedProperties)
        {
            if ((nameFilter is not null && !BuiltInToken.Equals(key.Name, nameFilter))
                || !targets.TryDescribe(key, out var chain)
                || chain.Length != depth)
            {
                continue;
            }

            var matches = true;
            for (var level = 0; level < depth && matches; level++)
            {
                var name = values[2 + (level * 2)];
                matches = BuiltInToken.Equals(chain[level].Type, values[1 + (level * 2)])
                    && (name is null || database.Collation.Equals(chain[level].Name, name));
            }
            if (!matches)
                continue;

            yield return RowEncoder.EncodeRow(schema, [
                depth == 0 ? SqlValue.Null(schema[0]) : SqlValue.FromVarchar((VarcharSqlType)schema[0], chain[depth - 1].Type),
                depth == 0 ? SqlValue.Null(schema[1]) : SqlValue.FromNVarchar((NVarcharSqlType)schema[1], chain[depth - 1].Name),
                SqlValue.FromNVarchar((NVarcharSqlType)schema[2], key.Name),
                value.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(value),
            ]);
        }
    }
}
