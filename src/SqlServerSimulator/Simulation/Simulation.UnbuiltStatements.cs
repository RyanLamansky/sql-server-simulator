using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// A statement form real SQL Server runs that the simulator hasn't built:
    /// the words that open it, after the verb when one leads, and the feature
    /// a <see cref="NotSupportedException"/> names for it.
    /// </summary>
    private readonly struct UnbuiltStatementForm(string? verb, string words, string feature)
    {
        /// <summary>
        /// <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c> ahead of <see cref="Words"/>,
        /// any of the three when null, <c>WAITFOR</c> ahead of a parenthesized
        /// statement; empty when <see cref="Words"/> opens the statement itself.
        /// </summary>
        public readonly string? Verb = verb;

        public readonly string[] Words = words.Split(' ');

        public readonly string Feature = feature;
    }

    /// <summary>
    /// The statement forms <see cref="RefuseUnbuiltStatement"/> recognizes, a
    /// longer form ahead of a shorter one it extends, since a form matches
    /// the statement's leading words.
    /// </summary>
    private static readonly UnbuiltStatementForm[] UnbuiltStatementForms =
    [
        new(null, "CRYPTOGRAPHIC PROVIDER", "Extensible key management (cryptographic providers)"),
        new(null, "COLUMN MASTER KEY", "Always Encrypted (column master keys)"),
        new(null, "COLUMN ENCRYPTION KEY", "Always Encrypted (column encryption keys)"),
        new(null, "DATABASE ENCRYPTION KEY", "Transparent data encryption (database encryption keys)"),
        new("ALTER", "SERVICE MASTER KEY", "the service master key"),
        new(null, "ENDPOINT", "Endpoints"),
        new(null, "RESOURCE POOL", "Resource Governor (resource pools)"),
        new(null, "EXTERNAL RESOURCE POOL", "Resource Governor (external resource pools)"),
        new(null, "WORKLOAD GROUP", "Resource Governor (workload groups)"),
        new(null, "EXTERNAL DATA SOURCE", "External data sources (PolyBase)"),
        new(null, "EXTERNAL TABLE", "External tables (PolyBase)"),
        new(null, "EXTERNAL FILE FORMAT", "External file formats (PolyBase)"),
        new(null, "EXTERNAL LANGUAGE", "External languages"),
        new(null, "EXTERNAL LIBRARY", "External libraries"),
        new(null, "EXTERNAL MODEL", "External models"),
        new(null, "AVAILABILITY GROUP", "Always On availability groups"),
        new(null, "SEARCH PROPERTY LIST", "Full-text search property lists"),
        new("CREATE", "SELECTIVE XML INDEX", "Selective XML indexes"),
        new("ALTER", "RESOURCE GOVERNOR", "Resource Governor"),
        new("ALTER", "SERVER CONFIGURATION", "ALTER SERVER CONFIGURATION"),
        new("DROP", "SIGNATURE", "Module signing (signatures)"),
        new("DROP", "COUNTER SIGNATURE", "Module signing (counter signatures)"),
        new("", "ADD SIGNATURE", "Module signing (signatures)"),
        new("", "ADD COUNTER SIGNATURE", "Module signing (counter signatures)"),
        new("", "BACKUP CERTIFICATE", "Certificates"),
        new("", "BACKUP MASTER KEY", "Database master keys"),
        new("", "BACKUP SERVICE MASTER KEY", "The service master key"),
        new("", "BACKUP SYMMETRIC KEY", "Symmetric keys"),
        new("", "RESTORE MASTER KEY", "Database master keys"),
        new("", "RESTORE SERVICE MASTER KEY", "The service master key"),
        new("", "RESTORE SYMMETRIC KEY", "Symmetric keys"),
    ];

    /// <summary>
    /// Raises <see cref="NotSupportedException"/> naming the feature when the
    /// statement at the cursor is one of <see cref="UnbuiltStatementForms"/>,
    /// where it would otherwise be a syntax error real doesn't raise — or, for
    /// <c>CREATE DATABASE ENCRYPTION KEY</c> and its neighbours, a database
    /// created under the second word's name. The cursor sits on the verb for
    /// <c>CREATE</c> / <c>ALTER</c> / <c>DROP</c> and on the statement's first
    /// word otherwise, and is left there when nothing matches.
    /// </summary>
    private static void RefuseUnbuiltStatement(ParserContext context, string? verb)
    {
        var checkpoint = context.SaveCheckpoint();
        var words = new string?[4];
        var count = 0;
        if (verb is null)
            words[count++] = context.Token?.ErrorText;
        else if (verb == "WAITFOR")
            words[count++] = "(";
        while (count < words.Length && context.GetNextOptional() is UnquotedString or ReservedKeyword)
            words[count++] = context.Token!.ErrorText;
        context.RestoreCheckpoint(checkpoint);

        // Real refuses a workload classifier, a Synapse surface, as it parses
        // the statement — whatever follows, and in an untaken branch too
        // (probed 2026-10-15 against SQL Server 2025).
        if (verb is "ALTER" or "CREATE" or "DROP" && BuiltInToken.Equals(words[0], "WORKLOAD") && BuiltInToken.Equals(words[1], "CLASSIFIER"))
            throw SimulatedSqlException.SynapseOnlyStatement(verb + " WORKLOAD CLASSIFIER");

        foreach (var form in UnbuiltStatementForms)
        {
            var applies = verb switch
            {
                null => form.Verb is "",
                "ALTER" or "CREATE" or "DROP" => form.Verb is null || form.Verb == verb,
                _ => form.Verb == verb,
            };
            if (!applies)
                continue;
            if (form.Words.Length > count)
                continue;
            var matched = true;
            for (var i = 0; i < form.Words.Length && matched; i++)
                matched = BuiltInToken.Equals(words[i], form.Words[i]);
            if (matched)
            {
                var statement = string.Join(' ', form.Words).ToUpperInvariant().Replace("( ", "(", StringComparison.Ordinal);
                throw new NotSupportedException($"{(verb is null ? statement : verb + " " + statement)} isn't modeled by the simulator ({form.Feature}).");
            }
        }
    }

    /// <summary>
    /// The kinds of object no statement here creates, by the words <c>DROP</c>
    /// takes for them, with the error real raises dropping one that doesn't
    /// exist and the names real ships of that kind, which stay unbuilt
    /// (probed 2026-10-21 against SQL Server 2025).
    /// </summary>
    private static readonly (string[] Words, Func<ParserContext, string, SimulatedSqlException> Missing, string[] BuiltIn)[] UnbuiltDropKinds =
    [
        (["AVAILABILITY", "GROUP"], static (context, _) => context.CurrentDatabase.Name == MasterDatabaseName ? SimulatedSqlException.AvailabilityGroupsNotEnabled() : SimulatedSqlException.AvailabilityGroupDdlOutsideMaster(), []),
        (["COLUMN", "ENCRYPTION", "KEY"], static (_, name) => SimulatedSqlException.ColumnKeyNotFound("column encryption key", name), []),
        (["COLUMN", "MASTER", "KEY"], static (_, name) => SimulatedSqlException.ColumnKeyNotFound("column master key", name), []),
        (["CRYPTOGRAPHIC", "PROVIDER"], static (_, name) => SimulatedSqlException.CannotDropSecurable("cryptographic provider", name, 1), []),
        (["DATABASE", "ENCRYPTION", "KEY"], static (_, _) => SimulatedSqlException.DatabaseEncryptionKeyMissing(), []),
        (["ENDPOINT"], static (_, name) => SimulatedSqlException.EndpointNotFound(name), ["Dedicated Admin Connection", "TSQL Default TCP", "TSQL Default VIA", "TSQL Local Machine", "TSQL Named Pipes"]),
        (["EXTERNAL", "DATA", "SOURCE"], static (_, name) => SimulatedSqlException.CannotDropSecurable("object", name, 1), []),
        (["EXTERNAL", "FILE", "FORMAT"], static (_, name) => SimulatedSqlException.CannotDropSecurable("object", name, 1), []),
        (["EXTERNAL", "LANGUAGE"], static (_, name) => SimulatedSqlException.CannotDropSecurable("external language", name, 2), []),
        (["EXTERNAL", "LIBRARY"], static (_, name) => SimulatedSqlException.CannotDropSecurable("external library", name, 2), []),
        (["EXTERNAL", "MODEL"], static (_, name) => SimulatedSqlException.CannotDropSecurable("object", name, 202), []),
        (["EXTERNAL", "RESOURCE", "POOL"], static (_, name) => SimulatedSqlException.CannotDropSecurable("resource pool", name, 2), ["default"]),
        (["EXTERNAL", "TABLE"], static (_, name) => SimulatedSqlException.CannotDropSecurable("EXTERNAL TABLE", name, 1), []),
        (["RESOURCE", "POOL"], static (_, name) => SimulatedSqlException.CannotDropSecurable("resource pool", name, 2), ["default", "internal"]),
        (["SEARCH", "PROPERTY", "LIST"], static (_, name) => SimulatedSqlException.SearchPropertyListNotFound(name, state: 13), []),
        (["WORKLOAD", "GROUP"], static (_, name) => SimulatedSqlException.CannotDropSecurable("workload group", name, 4), ["default", "internal"]),
    ];

    /// <summary>
    /// Parses a <c>DROP</c> of one of <see cref="UnbuiltDropKinds"/>, cursor on
    /// <c>DROP</c>: as no object of the kind can exist here, every one-part
    /// name is the missing object real reports as the statement runs, and
    /// <c>IF EXISTS</c>, which none of them takes, is Msg 156 near <c>IF</c>
    /// as the batch compiles. False — the cursor left on <c>DROP</c> — for any
    /// other statement, a name real ships, or a shape not read here.
    /// </summary>
    private static bool TryParseDropOfUnbuiltKind(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        foreach (var (words, missing, builtIn) in UnbuiltDropKinds)
        {
            context.RestoreCheckpoint(checkpoint);
            var matched = true;
            foreach (var word in words)
            {
                if (context.GetNextOptional() is not (UnquotedString or ReservedKeyword) || !BuiltInToken.Equals(context.Token!.ErrorText, word))
                {
                    matched = false;
                    break;
                }
            }
            if (!matched)
                continue;
            if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.If } ifKeyword)
                throw SimulatedSqlException.SyntaxErrorNearKeyword(ifKeyword);
            // A database's encryption key takes no name.
            if (words is ["DATABASE", "ENCRYPTION", "KEY"] && IsStatementBoundary(context.Token))
                return context.Batch.IsSkipping ? true : throw missing(context, string.Empty);
            if (context.Token is not Name nameToken || Array.Exists(builtIn, shipped => BuiltInToken.Equals(shipped, nameToken.Value)))
                break;
            context.MoveNextOptional();
            if (context.Token is Operator { Character: '.' } || !IsStatementBoundary(context.Token))
                break;
            if (context.Batch.IsSkipping)
                return true;
            throw missing(context, nameToken.Value);
        }
        context.RestoreCheckpoint(checkpoint);
        return false;
    }
}
