using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the two ALTER DATABASE forms the simulator currently models:
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c> (per-database
    /// compat) and
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// The simulator has a single database, so any database name (including
    /// <c>CURRENT</c>) is accepted and ignored.
    /// </summary>
    private static bool TryParseAlter(ParserContext context)
    {
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc }:
                // ALTER PROCEDURE is identical in shape to CREATE PROCEDURE —
                // same parameter grammar, same options, same body capture —
                // differing only in the existence-check direction (must exist
                // vs must not). Reuse the CREATE PROCEDURE parser with the
                // isAlter flag set.
                return TryParseCreateProcedure(context, isAlter: true, createOrAlter: false);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Sequence }:
                return TryParseAlterSequence(context);
            case ReservedKeyword { Keyword: Keyword.Database }:
                break;
            default:
                return false;
        }

        // Cursor is on DATABASE; advance to the token after it (a db name, the
        // CURRENT keyword, or the SCOPED contextual keyword routing to the
        // database-scoped-configuration path).
        var afterDatabase = context.GetNextRequired();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Scoped })
            return TryParseAlterDatabaseScopedConfiguration(context);

        // Otherwise a database name (or CURRENT). The simulator has one
        // database; accept anything that looks like an identifier.
        return afterDatabase is Name or ReservedKeyword { Keyword: Keyword.Current }
            && TryParseAlterDatabaseSet(context);
    }

    private static bool TryParseAlterDatabaseSet(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Compatibility_Level })
            return false;

        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
            return false;

        var requested = numericValue.AsInt32;
        if (context.Batch.IsSkipping)
            return true;
        if (!Enum.IsDefined((CompatibilityLevel)requested))
            throw SimulatedSqlException.InvalidCompatibilityLevel();

        context.CurrentDatabase.CompatibilityLevel = (CompatibilityLevel)requested;
        return true;
    }

    private static bool TryParseAlterDatabaseScopedConfiguration(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Configuration })
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Verbose_Truncation_Warnings })
            return false;

        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var on } || on is not (Keyword.On or Keyword.Off))
            return false;

        if (!context.Batch.IsSkipping)
            context.CurrentDatabase.VerboseTruncationWarnings = on == Keyword.On;
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SEQUENCE [schema.]name [RESTART [WITH n]] [INCREMENT BY n]
    /// [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE] [CYCLE | NO CYCLE]
    /// [CACHE n | NO CACHE]</c>. Entered with <see cref="ParserContext.Token"/>
    /// on the <c>SEQUENCE</c> contextual keyword. <c>RESTART</c> resets
    /// <see cref="Sequence.CurrentValue"/> to the explicit value or to
    /// <see cref="Sequence.StartValue"/>, and clears
    /// <see cref="Sequence.IsExhausted"/>. Other options replace the
    /// matching field. Probe-confirmed: ALTER SEQUENCE accepts the same
    /// option subset as CREATE SEQUENCE.
    /// </summary>
    private static bool TryParseAlterSequence(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var sequenceName = BatchContext.ParseObjectName(context);

        if (context.Batch.IsSkipping)
        {
            // Walk past any option tokens so the dispatch loop's lookahead
            // doesn't trip on them.
            while (context.MoveNext() && context.Token is not (Operator { Character: ';' } or ReservedKeyword))
            {
                // no-op
            }
            return true;
        }

        if (!context.Batch.TryResolveSequence(sequenceName, out var sequence))
            throw SimulatedSqlException.InvalidObjectName(sequenceName);

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.Restart }:
                    {
                        // RESTART [WITH n]: peek WITH; if present, read the
                        // value; otherwise reset to the original start value.
                        var afterRestart = context.SaveCheckpoint();
                        if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.With })
                        {
                            sequence.CurrentValue = ReadSignedIntegerLiteral(context);
                        }
                        else
                        {
                            context.RestoreCheckpoint(afterRestart);
                            sequence.CurrentValue = sequence.StartValue;
                        }
                        sequence.IsExhausted = false;
                        continue;
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Increment }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                        return false;
                    sequence.Increment = ReadSignedIntegerLiteral(context);
                    if (sequence.Increment == 0)
                        throw SimulatedSqlException.SequenceIncrementCannotBeZero(sequence.FullName);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                    sequence.MinValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                    sequence.MaxValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                    sequence.Cycle = true;
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.No }:
                    {
                        var afterNo = context.GetNextRequired();
                        switch (afterNo)
                        {
                            case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                                sequence.Cycle = false;
                                continue;
                            case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue or ContextualKeyword.MaxValue or ContextualKeyword.Cache }:
                                continue;
                            default:
                                return false;
                        }
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                    {
                        var afterCache = context.SaveCheckpoint();
                        if (!context.MoveNext() || context.Token is not (Numeric or Operator { Character: '-' or '+' }))
                        {
                            context.RestoreCheckpoint(afterCache);
                        }
                        else
                        {
                            context.RestoreCheckpoint(afterCache);
                            _ = ReadSignedIntegerLiteral(context);
                        }
                        continue;
                    }
                default:
                    return true;
            }
        }
        return true;
    }
}
