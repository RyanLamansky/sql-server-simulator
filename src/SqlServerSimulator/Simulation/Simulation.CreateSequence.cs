using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
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
        if (BatchContext.IsLocalTempName(sequenceName.Leaf) || BatchContext.IsGlobalTempName(sequenceName.Leaf))
            throw SimulatedSqlException.InvalidSequenceName(sequenceName.Leaf);

        // Defaults: declared type bigint, increment 1, cycle off. Min/max/start
        // resolve after the AS clause picks the type (since the type's natural
        // bounds drive the defaults).
        SqlType declaredType = SqlType.BigInt;
        var spelledNumeric = false;
        Int128? startValue = null;
        Int128 increment = 1;
        Int128? minValue = null;
        Int128? maxValue = null;
        var cycle = false;
        long? cacheSize = null;
        var seen = new SequenceOptionsSeen();
        // A fractional literal is out of every sequence type's domain, which
        // real reports with the range refusals, in their order.
        bool startFractional = false, incrementFractional = false, minFractional = false, maxFractional = false;

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.As }:
                    context.MoveNextRequired();
                    if (context.Token is not Name)
                        return false;
                    var qualifiedTypeName = BatchContext.ParseObjectName(context);
                    var typeName = (Name)context.Token;
                    declaredType = ResolveSequenceType(context, qualifiedTypeName, typeName, sequenceName.ToString());
                    spelledNumeric = declaredType is DecimalSqlType && qualifiedTypeName.Count == 1 && typeName.Value.Equals("numeric", StringComparison.OrdinalIgnoreCase);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Start }:
                    NoteSequenceOption(ref seen.Start, "START WITH");
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.With })
                        return false;
                    startValue = ReadSignedIntegerLiteral(context, out startFractional);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Increment }:
                    NoteSequenceOption(ref seen.Increment, "INCREMENT BY");
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                        return false;
                    increment = ReadSignedIntegerLiteral(context, out incrementFractional);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                    NoteSequenceOption(ref seen.MinValue, "MINVALUE");
                    minValue = ReadSignedIntegerLiteral(context, out minFractional);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                    NoteSequenceOption(ref seen.MaxValue, "MAXVALUE");
                    maxValue = ReadSignedIntegerLiteral(context, out maxFractional);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.No }:
                    {
                        // NO MIN/MAX/CYCLE/CACHE: parsed and treated as the
                        // default. NO CYCLE is explicit-default (sequence
                        // stays no-cycle); NO CACHE is kept for Msg 11729.
                        switch (context.GetNextRequired())
                        {
                            case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                                NoteSequenceOption(ref seen.Cache, "CACHE");
                                seen.NoCache = true;
                                cacheSize = 0;
                                continue;
                            case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                                NoteSequenceOption(ref seen.Cycle, "CYCLE");
                                continue;
                            case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                                NoteSequenceOption(ref seen.MinValue, "MINVALUE");
                                minValue = null;
                                continue;
                            case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                                NoteSequenceOption(ref seen.MaxValue, "MAXVALUE");
                                maxValue = null;
                                continue;
                            default:
                                return false;
                        }
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                    NoteSequenceOption(ref seen.Cycle, "CYCLE");
                    cycle = true;
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                    NoteSequenceOption(ref seen.Cache, "CACHE");
                    cacheSize = ReadOptionalCacheSize(context);
                    continue;
                default:
                    goto exitOptionLoop;
            }
        }
    exitOptionLoop:
        // Options are separated by nothing (probed 2026-10-04 against SQL
        // Server 2025: a comma is a syntax error at itself).
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Type-natural bounds for default min/max; a written value outside
        // them is Msg 11708, the increment checked first, then the minimum,
        // the maximum and the start (probed 2026-10-02 against SQL Server 2025).
        var (typeMin, typeMax) = SequenceTypeBounds(declaredType);
        bool OutOfType(Int128? value, bool fractional) => fractional || value < typeMin || value > typeMax;
        var displayName = sequenceName.ToString();
        if (increment == 0 && !incrementFractional)
            throw SimulatedSqlException.SequenceIncrementCannotBeZero(displayName);
        if (OutOfType(increment, incrementFractional))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("INCREMENT BY");
        if (OutOfType(minValue, minFractional))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("MINVALUE");
        if (OutOfType(maxValue, maxFractional))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("MAXVALUE");
        if (OutOfType(startValue, startFractional))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("START WITH");
        var resolvedMin = minValue ?? typeMin;
        var resolvedMax = maxValue ?? typeMax;
        var ascending = increment > 0;
        var resolvedStart = startValue ?? (ascending ? resolvedMin : resolvedMax);

        // Equal bounds are refused too, ahead of the start's range (probed
        // 2026-10-04 against SQL Server 2025).
        if (resolvedMin >= resolvedMax)
            throw SimulatedSqlException.SequenceMinNotBelowMax(displayName);
        if (resolvedStart < resolvedMin || resolvedStart > resolvedMax)
            throw SimulatedSqlException.SequenceStartOutOfRange(displayName);
        if (cacheSize == 0 && seen.Cache && !seen.NoCache)
            throw SimulatedSqlException.SequenceCacheMustBePositive(displayName);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveCreateSchema(sequenceName, out var schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(sequenceName.ImmediateQualifier ?? Database.DefaultSchemaName);

        // CREATE SEQUENCE is a permission of the target schema, which its
        // ALTER covers (sys.fn_builtin_permissions) — Msg 15247 without it.
        if (!PermissionEnforcement.HoldsPermission(context.Batch, schema.Database, Permission.CreateSequence, PermissionChecker.ClassSchema, schema.SchemaId, 0))
            throw SimulatedSqlException.UserDoesNotHavePermission();

        schema.Database.RejectWriteWhenReadOnly();

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
            cycle)
        {
            CacheSize = cacheSize,
            SpelledNumeric = spelledNumeric,
        };

        // The object namespace is shared with tables / views / functions / procs;
        // duplicate names across kinds raise Msg 2714. Check cross-kind before
        // the sequence-specific insert.
        if (schema.HasNameInSharedNamespace(sequence.Name) || !schema.Sequences.TryAdd(sequence.Name, sequence))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(sequenceName.ToString(), state: 8);
        RecordSlotUndo<Sequence>(context, schema.Sequences, sequence.Name, null);
        RecordDdlEvent(context, "CREATE_SEQUENCE", schema.Name, sequence.Name, "SEQUENCE");
        SendSequenceCacheMessages(context.Batch, sequence);
        return true;
    }

    /// <summary>
    /// The informational messages a <c>CREATE</c> or <c>ALTER SEQUENCE</c>
    /// sends about the cache it leaves: Msg 11707 for a cache of 1, and Msg
    /// 11729 when the cache is longer than the values left.
    /// </summary>
    private static void SendSequenceCacheMessages(BatchContext batch, Sequence sequence)
    {
        if (sequence.CacheSize == 1)
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SequenceCacheSetToNoCacheMessage(batch, sequence.Name));
        if (sequence.CacheExceedsAvailableValues())
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SequenceCacheExceedsRangeMessage(batch, sequence.Name));
    }

    /// <summary>
    /// Which options a <c>CREATE</c> or <c>ALTER SEQUENCE</c> has written, for
    /// Msg 11712 on the second; a <c>NO</c> form and its positive are one
    /// option (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private struct SequenceOptionsSeen
    {
        public bool Start, Increment, MinValue, MaxValue, Cycle, Cache, NoCache, Restart;
    }

    /// <summary>Marks an option of <see cref="SequenceOptionsSeen"/> written, Msg 11712 when it already was.</summary>
    private static void NoteSequenceOption(ref bool flag, string argument)
    {
        if (flag)
            throw SimulatedSqlException.SequenceArgumentRepeated(argument);
        flag = true;
    }

    /// <summary>
    /// Reads the optional size after <c>CACHE</c>, the cursor on the keyword
    /// and left on the last token read; null for a bare <c>CACHE</c>. A sign is
    /// a syntax error at itself, and a size wider than <c>int</c> one at the
    /// number (probed 2026-10-02 and 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static long? ReadOptionalCacheSize(ParserContext context)
    {
        var afterCache = context.SaveCheckpoint();
        if (!context.MoveNext() || context.Token is not (Numeric or Operator { Character: '-' or '+' }))
        {
            context.RestoreCheckpoint(afterCache);
            return null;
        }
        if (context.Token is Operator { Character: '-' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.RestoreCheckpoint(afterCache);
        return ReadCacheSize(context);
    }

    /// <summary>
    /// Resolves the <c>AS &lt;type&gt;</c> clause to a concrete
    /// <see cref="SqlType"/>. Accepts the integer family plus
    /// <c>decimal(p, 0)</c> / <c>numeric(p, 0)</c>. Length / scale tokens
    /// after the type name are consumed via the same parens path the
    /// column-declaration parser uses; non-zero scale raises Msg 11702.
    /// </summary>
    private static SqlType ResolveSequenceType(ParserContext context, MultiPartName qualifiedTypeName, Name typeName, string fullName)
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
                    _ = context.GetNextRequired();
                    declaredScale = TypeNameSynonyms.ReadSecondTypeArgument(context, typeName);
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

        var (resolved, _, _, _) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: 1, TypeSpecSite.Scalar, columnName: typeName.Value);
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
    private static Int128 ReadSignedIntegerLiteral(ParserContext context) => ReadSignedIntegerLiteral(context, out _);

    /// <summary>
    /// <see cref="ReadSignedIntegerLiteral(ParserContext)"/>, reporting through
    /// <paramref name="fractional"/> a literal written with a decimal point,
    /// which no sequence type takes (Msg 11708); a float literal is a syntax
    /// error at itself (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static Int128 ReadSignedIntegerLiteral(ParserContext context, out bool fractional)
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
        if (numericToken.Value.Type is not (Int32SqlType or DecimalSqlType))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        fractional = numericToken.Value.Type is DecimalSqlType { scale: > 0 };
        if (fractional)
            return 0;
        var literal = numericToken.Value.CoerceTo(DecimalSqlType.Get(38, 0)).AsDecimal38;
        var v = (Int128)literal.Magnitude;
        return negative != literal.IsNegative ? -v : v;
    }

    /// <summary>
    /// Reads a <c>CACHE</c> size, which real's grammar takes as an <c>int</c>
    /// literal: a wider one is a syntax error at the number (probed 2026-10-02
    /// against SQL Server 2025).
    /// </summary>
    private static long ReadCacheSize(ParserContext context)
    {
        var value = ReadSignedIntegerLiteral(context);
        return value > int.MaxValue || value < int.MinValue ? throw SimulatedSqlException.SyntaxErrorNear(context) : (long)value;
    }

    /// <summary>Whether <paramref name="value"/> falls outside a sequence of <paramref name="type"/>'s range.</summary>
    private static bool IsOutsideSequenceType(SqlType type, Int128 value)
    {
        var (min, max) = SequenceTypeBounds(type);
        return value < min || value > max;
    }

    /// <summary>
    /// Natural numeric bounds for a sequence's declared type. Used as the
    /// default <c>MINVALUE</c> / <c>MAXVALUE</c> when the user omits them.
    /// </summary>
    private static (Int128 Min, Int128 Max) SequenceTypeBounds(SqlType type) => type switch
    {
        TinyIntSqlType => (0, 255),
        SmallIntSqlType => (-32768, 32767),
        Int32SqlType => (int.MinValue, int.MaxValue),
        BigIntSqlType => (long.MinValue, long.MaxValue),
        DecimalSqlType d => DecimalBounds(d.precision),
        _ => throw new InvalidOperationException($"Unexpected sequence type {type}."),
    };

    /// <summary>
    /// <c>10^precision - 1</c> for a decimal sequence, the negative of it its
    /// lower bound.
    /// </summary>
    private static (Int128 Min, Int128 Max) DecimalBounds(byte precision)
    {
        Int128 max = 1;
        for (var i = 0; i < precision; i++)
            max *= 10;
        max -= 1;
        return (-max, max);
    }
}
