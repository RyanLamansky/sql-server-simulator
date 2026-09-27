using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>ALTER DATABASE SCOPED CONFIGURATION [FOR SECONDARY] SET
    /// &lt;option&gt; = &lt;value&gt;</c> and <c>ALTER DATABASE SCOPED
    /// CONFIGURATION CLEAR PROCEDURE_CACHE [plan_handle]</c>, entered on
    /// <c>SCOPED</c>, against the session's database. The option list and
    /// each option's value grammar are <see cref="DatabaseScopedConfiguration.Options"/>.
    /// </summary>
    /// <remarks>
    /// Probed 2026-09-27 against SQL Server 2025. A value the grammar rejects is
    /// a syntax error at it; an out-of-range number (Msg 12108 / 12121 /
    /// 31207), <c>PRIMARY</c> for the primary (Msg 12109) and <c>FOR
    /// SECONDARY</c> on a primary-only option (Msg 12110) are raised compiling
    /// the batch, so nothing in it runs. The run-time refusals — permission
    /// (Msg 15247), a read-only database (Msg 3906), a plan handle (Msg 12117),
    /// a ledger endpoint (Msg 12136 / 37531) — end the batch. Real accepts a
    /// <c>FOR SECONDARY</c> <c>FULLTEXT_INDEX_VERSION</c> by setting the
    /// primary's value, and a <c>FOR SECONDARY</c>
    /// <c>LEDGER_DIGEST_STORAGE_ENDPOINT = OFF</c> by changing nothing, and so
    /// does this. <c>CLEAR PROCEDURE_CACHE</c> without a handle has nothing
    /// observable to clear: the plan cache is shared across databases and
    /// invisible to queries. Each success raises the
    /// <c>ALTER_DATABASE_SCOPED_CONFIGURATION</c> DDL event.
    /// </remarks>
    private static bool TryParseAlterDatabaseScopedConfiguration(ParserContext context)
    {
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Configuration })
            return false;

        var forSecondary = false;
        var word = context.GetNextRequired();
        if (word is ReservedKeyword { Keyword: Keyword.For })
        {
            if (context.GetNextRequired() is not Name { Value: var replica } || !BuiltInToken.Equals(replica, "SECONDARY"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            forSecondary = true;
            word = context.GetNextRequired();
            if (word is not ReservedKeyword { Keyword: Keyword.Set })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (word is Name { Value: var clear } && BuiltInToken.Equals(clear, "CLEAR"))
            return TryParseClearProcedureCache(context);
        if (word is not ReservedKeyword { Keyword: Keyword.Set })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // The option name is an unquoted identifier: a bracketed name, and a
        // name real doesn't list, are syntax errors at the name. The Synapse
        // DW_COMPATIBILITY_LEVEL parses — real's grammar knows it — and is
        // refused running, as a class-16 syntax error ending the batch.
        var nameToken = context.GetNextRequired();
        var index = nameToken is UnquotedString { Value: var optionName } ? DatabaseScopedConfiguration.IndexOf(optionName) : -1;
        if (index < 0)
        {
            if (nameToken is not UnquotedString { Value: var dw } || !BuiltInToken.Equals(dw, "DW_COMPATIBILITY_LEVEL"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            _ = context.GetNextRequired();
            context.MoveNextOptional();
            context.RejectTrailingToken();
            if (!context.Batch.IsSkipping)
                throw SimulatedSqlException.DataWarehouseCompatibilityLevel();
            return true;
        }
        var option = DatabaseScopedConfiguration.Options[index];
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var valueToken = context.GetNextRequired();
        string? value;
        var isPrimaryKeyword = valueToken is ReservedKeyword { Keyword: Keyword.Primary };
        if (isPrimaryKeyword && option.Kind is ScopedConfigurationKind.Bit or ScopedConfigurationKind.DegreeOfParallelism)
        {
            if (!forSecondary)
                throw SimulatedSqlException.ScopedConfigurationPrimaryOnPrimary();
            value = null;
            context.MoveNextOptional();
        }
        else
        {
            value = ParseScopedConfigurationValue(context, option);
        }

        if (forSecondary && option.PrimaryOnlyState != 0)
            throw SimulatedSqlException.ScopedConfigurationPrimaryOnly(option.PrimaryOnlyName, option.PrimaryOnlyState);
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        RejectScopedConfigurationWithoutPermission(context, database);
        if (database.IsReadOnly)
            throw SimulatedSqlException.ScopedConfigurationReadOnly(database.Name);
        switch (option.Kind)
        {
            case ScopedConfigurationKind.LedgerEndpoint when value != "OFF":
                throw ValidateLedgerDigestEndpoint(value!);
            case ScopedConfigurationKind.LedgerEndpoint when forSecondary:
                break;
            case ScopedConfigurationKind.FullTextIndexVersion:
                database.ScopedConfiguration.Set(index, forSecondary: false, value);
                break;
            default:
                database.ScopedConfiguration.Set(index, forSecondary, value);
                break;
        }
        RecordDdlEvent(context, "ALTER_DATABASE_SCOPED_CONFIGURATION", schemaName: null, objectName: null, objectType: null);
        return true;
    }

    /// <summary>
    /// Reads the value after <c>=</c> for <paramref name="option"/>, leaving
    /// the cursor past it, and returns it as the catalog's text. Entered with
    /// the cursor on the value.
    /// </summary>
    private static string ParseScopedConfigurationValue(ParserContext context, ScopedConfigurationOption option)
    {
        var token = context.Token;
        string value;
        switch (option.Kind)
        {
            case ScopedConfigurationKind.Bit:
                value = token switch
                {
                    ReservedKeyword { Keyword: Keyword.On } => "1",
                    ReservedKeyword { Keyword: Keyword.Off } => "0",
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextOptional();
                return value;
            case ScopedConfigurationKind.Elevate:
                value = token switch
                {
                    ReservedKeyword { Keyword: Keyword.Off } => "OFF",
                    UnquotedString { Value: var word } when BuiltInToken.Equals(word, "WHEN_SUPPORTED") => "WHEN_SUPPORTED",
                    UnquotedString { Value: var word } when BuiltInToken.Equals(word, "FAIL_UNSUPPORTED") => "FAIL_UNSUPPORTED",
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextOptional();
                return value;
            case ScopedConfigurationKind.LedgerEndpoint:
                value = token switch
                {
                    ReservedKeyword { Keyword: Keyword.Off } => "OFF",
                    Literal { Value: { IsNull: false, Type.Category: SqlTypeCategory.String } literal } => literal.AsString,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextOptional();
                return value;
        }

        // The numeric options take an integer literal, optionally negated.
        var negative = token is Operator { Character: '-' };
        if (negative)
            token = context.GetNextRequired();
        if (token is not Numeric numeric)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (numeric.Value.Type != SqlType.Int32)
            throw SimulatedSqlException.IntegerValueOutOfRange(numeric.Source.ToString());
        long number = numeric.Value.AsInt32;
        if (negative)
            number = -number;
        context.MoveNextOptional();
        return option.Kind switch
        {
            ScopedConfigurationKind.DegreeOfParallelism when number is < 0 or > 32767 => throw SimulatedSqlException.ScopedConfigurationOutOfRange(number, option.Name),
            ScopedConfigurationKind.Minutes when number is < 0 or > 71582 => throw SimulatedSqlException.PausedIndexAbortDurationOutOfRange(number),
            ScopedConfigurationKind.FullTextIndexVersion when number is not (1 or 2) => throw SimulatedSqlException.InvalidFullTextIndexVersion(),
            _ => number.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// <c>CLEAR PROCEDURE_CACHE [plan_handle]</c>, entered on <c>CLEAR</c>. A
    /// plan handle is a binary literal, and none matches a cached plan here
    /// (Msg 12117); without one the statement succeeds, on a read-only database
    /// too (probed 2026-09-27).
    /// </summary>
    private static bool TryParseClearProcedureCache(ParserContext context)
    {
        if (context.GetNextRequired() is not Name { Value: var what } || !BuiltInToken.Equals(what, "PROCEDURE_CACHE"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var withHandle = false;
        if (context.GetNextOptional() is Literal { Value.Type: VarbinarySqlType })
        {
            withHandle = true;
            context.MoveNextOptional();
        }
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;
        RejectScopedConfigurationWithoutPermission(context, context.CurrentDatabase);
        if (withHandle)
            throw SimulatedSqlException.PlanHandleNotFound();
        RecordDdlEvent(context, "ALTER_DATABASE_SCOPED_CONFIGURATION", schemaName: null, objectName: null, objectType: null);
        return true;
    }

    /// <summary>
    /// The scoped-configuration permission check: <c>ALTER</c> on the
    /// database, which <c>db_owner</c> and dbo hold. Real's own permission,
    /// <c>ALTER ANY DATABASE SCOPED CONFIGURATION</c>, isn't separately
    /// grantable here.
    /// </summary>
    private static void RejectScopedConfigurationWithoutPermission(ParserContext context, Database database)
    {
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, database, Permission.Alter))
            throw SimulatedSqlException.ScopedConfigurationPermissionDenied();
    }

    /// <summary>
    /// The refusal a ledger digest endpoint meets: anything but an
    /// <c>https://</c> Azure blob storage URL is Msg 12136, and a blob storage
    /// host Msg 37531, since no credential can reach it here (probed
    /// 2026-09-27; the state split between a foreign host and a blob host in
    /// another case is inferred from one probe of each).
    /// </summary>
    private static SimulatedSqlException ValidateLedgerDigestEndpoint(string endpoint)
    {
        const string scheme = "https://";
        const string blobHost = ".blob.core.windows.net";
        if (!endpoint.StartsWith(scheme, StringComparison.Ordinal))
            return SimulatedSqlException.InvalidLedgerDigestEndpoint(1);
        // A trailing slash is dropped; any other path fails as a foreign host.
        var trimmed = endpoint.TrimEnd('/');
        var host = trimmed[scheme.Length..];
        return host.EndsWith(blobHost, StringComparison.Ordinal) ? SimulatedSqlException.LedgerDigestEndpointUnreachable(trimmed)
            : host.EndsWith(blobHost, StringComparison.OrdinalIgnoreCase) ? SimulatedSqlException.InvalidLedgerDigestEndpoint(3)
            : SimulatedSqlException.InvalidLedgerDigestEndpoint(2);
    }
}
