using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE SEQUENCE [schema.]name [AS &lt;type&gt;] [START WITH n]
    /// [INCREMENT BY n] [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE]
    /// [CYCLE | NO CYCLE] [CACHE n | NO CACHE]</c>. Entered with
    /// <see cref="ParserContext.Token"/> on the <c>SEQUENCE</c> contextual
    /// keyword token. Validates type / start / increment / range invariants
    /// at parse time (Msg 11700, Msg 11702, Msg 11703); duplicate-name
    /// collisions across the object namespace raise Msg 2714.
    /// </summary>
    /// <remarks>
    /// Options can appear in any order and any subset. Defaults:
    /// type = <c>bigint</c>; start = <c>minvalue</c> for asc increment,
    /// <c>maxvalue</c> for desc; increment = 1; min/max = type-natural
    /// bounds; no cycle; cache enabled (parse-and-ignore — the simulator
    /// doesn't model the batched-allocation optimization).
    /// </remarks>
    private static bool TryParseCreateSequence(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var sequenceName = BatchContext.ParseObjectName(context);

        // Defaults: declared type bigint, increment 1, cycle off. Min/max/start
        // resolve after the AS clause picks the type (since the type's natural
        // bounds drive the defaults).
        SqlType declaredType = SqlType.BigInt;
        long? startValue = null;
        long increment = 1;
        long? minValue = null;
        long? maxValue = null;
        var cycle = false;

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.As }:
                    context.MoveNextRequired();
                    if (context.Token is not Name typeName)
                        return false;
                    declaredType = ResolveSequenceType(context, typeName, sequenceName.ToString());
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Start }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.With })
                        return false;
                    startValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Increment }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                        return false;
                    increment = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                    minValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                    maxValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.No }:
                    {
                        // NO MIN/MAX/CYCLE/CACHE: parsed and treated as the
                        // default. NO CYCLE is explicit-default (sequence
                        // stays no-cycle); NO CACHE is accepted (caching
                        // isn't modeled anyway).
                        var afterNo = context.GetNextRequired();
                        if (afterNo is not UnquotedString
                            {
                                ContextualKeyword:
                                    ContextualKeyword.MinValue or ContextualKeyword.MaxValue
                                    or ContextualKeyword.Cycle or ContextualKeyword.Cache
                            })
                        {
                            return false;
                        }
                        continue;
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                    cycle = true;
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                    {
                        // Optional explicit cache size: CACHE n. Peek for a
                        // signed-integer literal; if absent, restore and let
                        // the loop's MoveNext pick up the next option keyword.
                        var afterCache = context.SaveCheckpoint();
                        if (!context.MoveNext()
                            || context.Token is not (Numeric or Operator { Character: '-' or '+' }))
                        {
                            context.RestoreCheckpoint(afterCache);
                        }
                        else
                        {
                            // Already advanced past CACHE; the literal-read
                            // helper expects to advance from the keyword it
                            // followed. Restore and re-read so the helper
                            // sees CACHE as its anchor.
                            context.RestoreCheckpoint(afterCache);
                            _ = ReadSignedIntegerLiteral(context);
                        }
                        continue;
                    }
                default:
                    goto exitOptionLoop;
            }
        }
    exitOptionLoop:

        // Type-natural bounds for default min/max.
        var (typeMin, typeMax) = SequenceTypeBounds(declaredType);
        var resolvedMin = minValue ?? typeMin;
        var resolvedMax = maxValue ?? typeMax;
        var ascending = increment > 0;
        var resolvedStart = startValue ?? (ascending ? resolvedMin : resolvedMax);

        if (increment == 0)
            throw SimulatedSqlException.SequenceIncrementCannotBeZero(sequenceName.ToString());
        if (resolvedStart < resolvedMin || resolvedStart > resolvedMax)
            throw SimulatedSqlException.SequenceStartOutOfRange(sequenceName.ToString());

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveSchema(sequenceName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(sequenceName.Count >= 2 ? sequenceName.ImmediateQualifier! : Database.DefaultSchemaName);

        var sequence = new Sequence(
            schema,
            sequenceName.Leaf,
            context.CurrentDatabase.AllocateObjectId(),
            context.Batch.CurrentStatement.UtcNow,
            declaredType,
            resolvedStart,
            increment,
            resolvedMin,
            resolvedMax,
            cycle);

        // The object namespace is shared with tables / views / functions / procs;
        // duplicate names across kinds raise Msg 2714. Check cross-kind before
        // the sequence-specific insert.
        return schema.HasNameInSharedNamespace(sequence.Name)
            || !schema.Sequences.TryAdd(sequence.Name, sequence)
            ? throw SimulatedSqlException.ThereIsAlreadyAnObject(sequence.Name)
            : true;
    }

    /// <summary>
    /// Resolves the <c>AS &lt;type&gt;</c> clause to a concrete
    /// <see cref="SqlType"/>. Accepts the integer family plus
    /// <c>decimal(p, 0)</c> / <c>numeric(p, 0)</c>. Length / scale tokens
    /// after the type name are consumed via the same parens path the
    /// column-declaration parser uses; non-zero scale raises Msg 11702.
    /// </summary>
    private static SqlType ResolveSequenceType(ParserContext context, Name typeName, string fullName)
    {
        int? declaredMaxLength = null;
        int? declaredScale = null;
        var afterTypeName = context.SaveCheckpoint();
        if (context.MoveNext() && context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            if (lengthToken is not Numeric { Value: { IsNull: false } numericValue })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            declaredMaxLength = numericValue.AsInt32;
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
        }
        else
        {
            // No parens — type name is bare. Restore so Token sits at the
            // type name and the option loop's MoveNext picks up the next
            // option keyword.
            context.RestoreCheckpoint(afterTypeName);
        }

        var (resolved, _) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, 1, typeName.Value);
        return resolved switch
        {
            TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType => resolved,
            DecimalSqlType d when d.scale == 0 => resolved,
            _ => throw SimulatedSqlException.SequenceInvalidType(fullName),
        };
    }

    /// <summary>
    /// Reads a <c>[+|-] &lt;numeric&gt;</c> from the token stream, anchored at
    /// the keyword preceding the value (e.g. <c>WITH</c>, <c>BY</c>,
    /// <c>MINVALUE</c>, <c>MAXVALUE</c>). Advances via <see cref="ParserContext.GetNextRequired"/>
    /// — leaves <see cref="ParserContext.Token"/> at the consumed numeric
    /// literal, suitable for the option loop's top-of-iteration
    /// <see cref="ParserContext.MoveNext"/> to step forward to the next
    /// option keyword.
    /// </summary>
    private static long ReadSignedIntegerLiteral(ParserContext context)
    {
        var first = context.GetNextRequired();
        var negative = false;
        Numeric numericToken;
        switch (first)
        {
            case Operator { Character: '-' }:
                negative = true;
                numericToken = context.GetNextRequired() as Numeric
                    ?? throw SimulatedSqlException.SyntaxErrorNear(context);
                break;
            case Operator { Character: '+' }:
                numericToken = context.GetNextRequired() as Numeric
                    ?? throw SimulatedSqlException.SyntaxErrorNear(context);
                break;
            case Numeric n:
                numericToken = n;
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        var v = numericToken.Value.CoerceTo(SqlType.BigInt).AsInt64;
        return negative ? -v : v;
    }

    /// <summary>
    /// Natural numeric bounds for a sequence's declared type. Used as the
    /// default <c>MINVALUE</c> / <c>MAXVALUE</c> when the user omits them.
    /// </summary>
    private static (long Min, long Max) SequenceTypeBounds(SqlType type) => type switch
    {
        TinyIntSqlType => (0L, 255L),
        SmallIntSqlType => (-32768L, 32767L),
        Int32SqlType => (int.MinValue, int.MaxValue),
        BigIntSqlType => (long.MinValue, long.MaxValue),
        DecimalSqlType d => DecimalBounds(d.precision),
        _ => throw new InvalidOperationException($"Unexpected sequence type {type}."),
    };

    /// <summary>
    /// Computes 10^precision - 1 for decimal sequences, capped at
    /// <see cref="long.MaxValue"/> (precision &gt;= 19 saturates because the
    /// simulator tracks sequence state in long). Symmetric negative bound.
    /// </summary>
    private static (long Min, long Max) DecimalBounds(byte precision)
    {
        if (precision >= 19)
            return (long.MinValue, long.MaxValue);
        long max = 1;
        for (var i = 0; i < precision; i++)
            max *= 10;
        max -= 1;
        return (-max, max);
    }
}
