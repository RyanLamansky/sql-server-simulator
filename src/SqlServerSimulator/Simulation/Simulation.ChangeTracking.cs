using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The change tracking DDL: ALTER DATABASE … SET CHANGE_TRACKING and
// ALTER TABLE … { ENABLE | DISABLE } CHANGE_TRACKING.
partial class Simulation
{
    /// <summary>
    /// Whether the token after the cursor's <c>ENABLE</c> / <c>DISABLE</c> is
    /// <c>CHANGE_TRACKING</c>, which routes the ALTER TABLE action away from
    /// the trigger toggle.
    /// </summary>
    private static bool IsChangeTrackingAhead(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var ahead = context.GetNextOptional() is UnquotedString word && BuiltInToken.Equals(word.Value, "CHANGE_TRACKING");
        context.RestoreCheckpoint(checkpoint);
        return ahead;
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … ENABLE CHANGE_TRACKING [WITH (TRACK_COLUMNS_UPDATED
    /// = { ON | OFF })]</c> and <c>… DISABLE CHANGE_TRACKING</c>, cursor on
    /// <c>ENABLE</c> / <c>DISABLE</c> on entry and past the statement on
    /// return. Enabling needs the database's tracking on (Msg 1718) and a
    /// primary key (Msg 4997), checked in that order; the caller's table
    /// snapshot undoes either direction on rollback. All probed 2026-09-27
    /// against SQL Server 2025.
    /// </summary>
    private static bool TryParseAlterTableChangeTracking(ParserContext context, MultiPartName tableName, bool disable)
    {
        context.MoveNextRequired();
        var trackColumnsUpdated = false;
        context.MoveNextOptional();
        if (!disable)
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.With }:
                    if (context.GetNextRequired() is not Operator { Character: '(' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not UnquotedString option || !BuiltInToken.Equals(option.Value, "TRACK_COLUMNS_UPDATED")
                        || context.GetNextRequired() is not Operator { Character: '=' })
                    {
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    trackColumnsUpdated = context.GetNextRequired() switch
                    {
                        ReservedKeyword { Keyword: Keyword.On } => true,
                        ReservedKeyword { Keyword: Keyword.Off } => false,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                    break;
                case Operator { Character: '(' }:
                    // Real names the token inside the parenthesis.
                    context.MoveNextRequired();
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                default:
                    break;
            }
        }

        if (context.Batch.IsSkipping)
            return true;
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        if (disable)
        {
            if (table.ChangeTracking is null)
                throw SimulatedSqlException.ChangeTrackingNotEnabledOnTableForDisable(table.Name);
            table.ChangeTracking = null;
            return true;
        }

        var isTemp = table.IsTableVariable || BatchContext.IsLocalTempName(table.Name) || BatchContext.IsGlobalTempName(table.Name);
        var database = context.Batch.DatabaseFor(table);
        if (isTemp || database.ChangeTracking is null)
            throw SimulatedSqlException.ChangeTrackingNotEnabledOnDatabaseForTable(isTemp ? TempdbDatabaseName : database.Name, table.Name);
        if (table.ChangeTracking is not null)
            throw SimulatedSqlException.ChangeTrackingAlreadyEnabledOnTable(table.Name);
        if (!table.KeyConstraints.Exists(key => key.Kind == KeyConstraintKind.PrimaryKey))
            throw SimulatedSqlException.ChangeTrackingRequiresPrimaryKey(table.Name);
        table.ChangeTracking = new TableChangeTracking(trackColumnsUpdated, database.ChangeTrackingVersion);
        return true;
    }

    /// <summary>
    /// Parses <c>SET CHANGE_TRACKING = ON [( &lt;option&gt; [, …] )]</c>,
    /// <c>= OFF</c> and <c>CHANGE_TRACKING ( &lt;option&gt; [, …] )</c>, cursor on
    /// <c>CHANGE_TRACKING</c> on entry and on the statement's last token on
    /// return. The options are <c>CHANGE_RETENTION = n { DAYS | HOURS | MINUTES }</c>
    /// and <c>AUTO_CLEANUP = { ON | OFF }</c>. The grammar's own refusals
    /// (Msg 102 / 155 / 5091 / 5092) raise while parsing; the state's (Msg
    /// 5088 / 5089 / 5090 / 22115) when the statement runs. All probed
    /// 2026-09-27 against SQL Server 2025.
    /// </summary>
    private static bool TryParseAlterDatabaseSetChangeTracking(ParserContext context, Database target)
    {
        bool? turnOn = null;
        var pending = new DatabaseChangeTracking();
        var hasOptions = false;
        switch (context.GetNextRequired())
        {
            case Operator { Character: '=' }:
                switch (context.GetNextRequired())
                {
                    case ReservedKeyword { Keyword: Keyword.On }:
                        turnOn = true;
                        var afterOn = context.SaveCheckpoint();
                        if (context.GetNextOptional() is Operator { Character: '(' })
                            hasOptions = ParseChangeTrackingOptions(context, pending);
                        else
                            context.RestoreCheckpoint(afterOn);
                        break;
                    case ReservedKeyword { Keyword: Keyword.Off }:
                        turnOn = false;
                        var afterOff = context.SaveCheckpoint();
                        if (context.GetNextOptional() is Operator { Character: '(' })
                        {
                            // Real names the token inside the parenthesis.
                            context.MoveNextRequired();
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        }
                        context.RestoreCheckpoint(afterOff);
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                break;
            case Operator { Character: '(' }:
                if (target.ChangeTracking is { } current)
                {
                    pending.RetentionPeriod = current.RetentionPeriod;
                    pending.RetentionUnit = current.RetentionUnit;
                    pending.AutoCleanup = current.AutoCleanup;
                }
                hasOptions = ParseChangeTrackingOptions(context, pending);
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (context.Batch.IsSkipping)
            return true;
        if (BuiltInToken.EqualsAny(target.Name, MasterDatabaseName, TempdbDatabaseName, ModelDatabaseName, MsdbDatabaseName))
            throw SimulatedSqlException.ChangeTrackingOnSystemDatabase(target.Name);
        switch (turnOn)
        {
            case true:
                if (target.ChangeTracking is not null)
                    throw SimulatedSqlException.ChangeTrackingAlreadyEnabledOnDatabase(target.Name);
                target.ChangeTracking = pending;
                break;
            case false:
                if (target.ChangeTracking is null)
                    throw SimulatedSqlException.ChangeTrackingDisabledOnDatabase(target.Name, state: 1);
                foreach (var (_, schema) in target.Schemas)
                {
                    foreach (var (_, table) in schema.HeapTables)
                    {
                        if (table.ChangeTracking is not null)
                            throw SimulatedSqlException.ChangeTrackingTablesStillEnabled(target.Name);
                    }
                }
                target.ChangeTracking = null;
                break;
            default:
                if (target.ChangeTracking is null)
                    throw SimulatedSqlException.ChangeTrackingDisabledOnDatabase(target.Name, state: 2);
                if (hasOptions)
                    target.ChangeTracking = pending;
                break;
        }
        return true;
    }

    /// <summary>
    /// Parses a change tracking option block from just past its <c>(</c> to its
    /// <c>)</c>, which the cursor is left on, into <paramref name="pending"/>.
    /// A malformed option is Msg 102 at the token real names: the value when
    /// it is a word, else the option's own name.
    /// </summary>
    private static bool ParseChangeTrackingOptions(ParserContext context, DatabaseChangeTracking pending)
    {
        var seenRetention = false;
        var seenCleanup = false;
        while (true)
        {
            if (context.GetNextRequired() is not UnquotedString option)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var optionCheckpoint = context.SaveCheckpoint();
            SimulatedSqlException SyntaxErrorAtOption()
            {
                context.RestoreCheckpoint(optionCheckpoint);
                return SimulatedSqlException.SyntaxErrorNear(context);
            }

            if (BuiltInToken.Equals(option.Value, "AUTO_CLEANUP"))
            {
                if (seenCleanup)
                    throw SimulatedSqlException.ChangeTrackingOptionRepeated(option.Value);
                seenCleanup = true;
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SyntaxErrorAtOption();
                pending.AutoCleanup = context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.On } => true,
                    ReservedKeyword { Keyword: Keyword.Off } => false,
                    Name => throw SimulatedSqlException.SyntaxErrorNear(context),
                    _ => throw SyntaxErrorAtOption(),
                };
            }
            else if (BuiltInToken.Equals(option.Value, "CHANGE_RETENTION"))
            {
                if (seenRetention)
                    throw SimulatedSqlException.ChangeTrackingOptionRepeated(option.Value);
                seenRetention = true;
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SyntaxErrorAtOption();
                var amount = context.GetNextRequired() switch
                {
                    Numeric { Value: { IsNull: false, Type: var type } value } when type == SqlType.Int32 => value.AsInt32,
                    Operator { Character: '-' } => throw SimulatedSqlException.SyntaxErrorNear(context),
                    _ => throw SyntaxErrorAtOption(),
                };
                if (context.GetNextRequired() is not Name unit)
                    throw SyntaxErrorAtOption();
                var (retentionUnit, minutesPerUnit) = BuiltInToken.Equals(unit.Value, "DAYS") ? (ChangeRetentionUnit.Days, 1440L)
                    : BuiltInToken.Equals(unit.Value, "HOURS") ? (ChangeRetentionUnit.Hours, 60L)
                    : BuiltInToken.Equals(unit.Value, "MINUTES") ? (ChangeRetentionUnit.Minutes, 1L)
                    : throw SimulatedSqlException.UnrecognizedChangeRetentionUnit(unit.Value);
                if (amount < 1 || amount * minutesPerUnit > int.MaxValue)
                    throw SimulatedSqlException.ChangeRetentionOutOfRange(option.Value);
                pending.RetentionPeriod = amount;
                pending.RetentionUnit = retentionUnit;
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    continue;
                case Operator { Character: ')' }:
                    return true;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
    }
}
