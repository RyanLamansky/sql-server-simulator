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
    /// longer form ahead of a shorter one it extends (<c>SERVER AUDIT
    /// SPECIFICATION</c> before <c>SERVER AUDIT</c>).
    /// </summary>
    private static readonly UnbuiltStatementForm[] UnbuiltStatementForms =
    [
        new(null, "CRYPTOGRAPHIC PROVIDER", "Extensible key management (cryptographic providers)"),
        new(null, "COLUMN MASTER KEY", "Always Encrypted (column master keys)"),
        new(null, "COLUMN ENCRYPTION KEY", "Always Encrypted (column encryption keys)"),
        new(null, "DATABASE ENCRYPTION KEY", "Transparent data encryption (database encryption keys)"),
        new(null, "DATABASE AUDIT SPECIFICATION", "SQL Server Audit (database audit specifications)"),
        new(null, "DATABASE SCOPED CREDENTIAL", "Database scoped credentials"),
        new(null, "SERVER AUDIT SPECIFICATION", "SQL Server Audit (server audit specifications)"),
        new(null, "SERVER AUDIT", "SQL Server Audit"),
        new("ALTER", "SERVICE MASTER KEY", "the service master key"),
        new(null, "EVENT NOTIFICATION", "Event notifications"),
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
        new("", "RESTORE MASTER KEY", "Database master keys"),
        new("", "RESTORE SERVICE MASTER KEY", "The service master key"),
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
}
