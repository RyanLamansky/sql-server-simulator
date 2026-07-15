using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Pins the <see cref="Keyword"/> enum against the authoritative T-SQL
/// reserved keywords list at
/// <c>https://learn.microsoft.com/en-us/sql/t-sql/language-elements/reserved-keywords-transact-sql</c>.
/// Any change to <see cref="Keyword"/> must be verified against that page —
/// adding a contextual keyword (e.g. <c>THROW</c>, <c>TRY</c>, <c>CATCH</c>)
/// to this enum is a bug because the tokenizer relies on the enum to decide
/// what surfaces as <c>ReservedKeyword</c> vs. <c>UnquotedString</c>, and a
/// non-reserved word being treated as reserved breaks valid SQL (e.g.
/// blocking <c>select 1 as throw</c>). Contextual keywords belong in
/// <see cref="ContextualKeyword"/> instead — parser sites then read them via
/// <see cref="Tokens.UnquotedString.ContextualKeyword"/>.
/// </summary>
/// <remarks>
/// The test cross-checks both directions: every entry in <see cref="Keyword"/>
/// must appear in the canonical list, and every canonical entry must appear
/// in <see cref="Keyword"/>. The single exception is <c>WITHIN GROUP</c>,
/// which appears in the canonical list as a two-word entry but is omitted
/// from the enum — <see cref="Keyword"/>'s trailing comment notes that
/// <c>WITHIN</c> alone isn't enforced as a reserved word in real SQL Server
/// and <c>GROUP</c> is already covered, so a multi-word enum entry would be
/// functionally irrelevant. The omission is hardcoded into
/// <see cref="DocumentedOmissions"/> rather than parameterized — the
/// "reserved keyword list is frozen" rule has no anticipated exceptions
/// beyond this one structural quirk.
/// </remarks>
[TestClass]
public sealed class ReservedKeywordsTests
{
    /// <summary>
    /// The canonical reserved-keyword set, transcribed verbatim from
    /// <c>https://learn.microsoft.com/en-us/sql/t-sql/language-elements/reserved-keywords-transact-sql</c>.
    /// Updates here must be verified against that page; do not edit to
    /// match the enum without re-checking the source.
    /// </summary>
    private static readonly HashSet<string> CanonicalReservedKeywords =
    [
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION",
        "BACKUP", "BEGIN", "BETWEEN", "BREAK", "BROWSE", "BULK", "BY",
        "CASCADE", "CASE", "CHECK", "CHECKPOINT", "CLOSE", "CLUSTERED", "COALESCE",
        "COLLATE", "COLUMN", "COMMIT", "COMPUTE", "CONSTRAINT", "CONTAINS",
        "CONTAINSTABLE", "CONTINUE", "CONVERT", "CREATE", "CROSS", "CURRENT",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "CURSOR",
        "DATABASE", "DBCC", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DENY",
        "DESC", "DISK", "DISTINCT", "DISTRIBUTED", "DOUBLE", "DROP", "DUMP",
        "ELSE", "END", "ERRLVL", "ESCAPE", "EXCEPT", "EXEC", "EXECUTE", "EXISTS",
        "EXIT", "EXTERNAL",
        "FETCH", "FILE", "FILLFACTOR", "FOR", "FOREIGN", "FREETEXT", "FREETEXTTABLE",
        "FROM", "FULL", "FUNCTION",
        "GOTO", "GRANT", "GROUP",
        "HAVING", "HOLDLOCK",
        "IDENTITY", "IDENTITY_INSERT", "IDENTITYCOL", "IF", "IN", "INDEX", "INNER",
        "INSERT", "INTERSECT", "INTO", "IS",
        "JOIN",
        "KEY", "KILL",
        "LEFT", "LIKE", "LINENO", "LOAD",
        "MERGE",
        "NATIONAL", "NOCHECK", "NONCLUSTERED", "NOT", "NULL", "NULLIF",
        "OF", "OFF", "OFFSETS", "ON", "OPEN", "OPENDATASOURCE", "OPENQUERY",
        "OPENROWSET", "OPENXML", "OPTION", "OR", "ORDER", "OUTER", "OVER",
        "PERCENT", "PIVOT", "PLAN", "PRECISION", "PRIMARY", "PRINT", "PROC",
        "PROCEDURE", "PUBLIC",
        "RAISERROR", "READ", "READTEXT", "RECONFIGURE", "REFERENCES", "REPLICATION",
        "RESTORE", "RESTRICT", "RETURN", "REVERT", "REVOKE", "RIGHT", "ROLLBACK",
        "ROWCOUNT", "ROWGUIDCOL", "RULE",
        "SAVE", "SCHEMA", "SECURITYAUDIT", "SELECT", "SEMANTICKEYPHRASETABLE",
        "SEMANTICSIMILARITYDETAILSTABLE", "SEMANTICSIMILARITYTABLE", "SESSION_USER",
        "SET", "SETUSER", "SHUTDOWN", "SOME", "STATISTICS", "SYSTEM_USER",
        "TABLE", "TABLESAMPLE", "TEXTSIZE", "THEN", "TO", "TOP", "TRAN",
        "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY_CONVERT", "TSEQUAL",
        "UNION", "UNIQUE", "UNPIVOT", "UPDATE", "UPDATETEXT", "USE", "USER",
        "VALUES", "VARYING", "VIEW",
        "WAITFOR", "WHEN", "WHERE", "WHILE", "WITH", "WITHIN GROUP", "WRITETEXT",
    ];

    /// <summary>
    /// Canonical entries the enum intentionally omits because real SQL Server
    /// doesn't enforce them as reserved:
    /// <list type="bullet">
    /// <item><c>WITHIN GROUP</c> — a multi-word reserved keyword whose
    /// component words don't independently behave as reserved (see class
    /// doc).</item>
    /// <item><c>PRECISION</c> — appears on the documented list (it forms the
    /// <c>DOUBLE PRECISION</c> type name) but is probe-confirmed (SQL Server
    /// 2025, 2026-07-15) fully usable as an identifier in every position:
    /// dotted member (<c>clmns.precision</c>), bare projection, alias
    /// (<c>select 1 as precision</c>), table alias, and ORDER BY all succeed,
    /// where genuinely-reserved words such as <c>FROM</c> / <c>USER</c> raise
    /// Msg 156. SMO's SSMS column-node query reads <c>CAST(clmns.precision AS
    /// int)</c> off sys.all_columns, so treating it as reserved blocked the
    /// query. Reserving it would break valid SQL, so it stays out of the
    /// enum.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> DocumentedOmissions = ["WITHIN GROUP", "PRECISION"];

    [TestMethod]
    public void Keyword_Enum_MatchesCanonicalReservedList()
    {
        var enumNames = Enum.GetNames<Keyword>()
            .Where(n => n != "_")
            .Select(n => n.ToUpperInvariant())
            .ToHashSet();

        var unexpectedExtras = enumNames.Except(CanonicalReservedKeywords).Order().ToList();
        var unexpectedOmissions = CanonicalReservedKeywords.Except(enumNames).Except(DocumentedOmissions).Order().ToList();

        IsEmpty(
            unexpectedExtras,
            $"Keyword enum has entries not in the canonical reserved list at {SourceUrl}: {string.Join(", ", unexpectedExtras)}. The reserved-keyword list is frozen — remove the entry from Keyword. If the parser needs to recognize the identifier in specific positions, add it to ContextualKeyword instead; call sites read it via UnquotedString.ContextualKeyword (see the TRY / CATCH / THROW pattern in Simulation.TryCatch.cs / Simulation.Throw.cs).");

        IsEmpty(
            unexpectedOmissions,
            $"Keyword enum is missing canonical reserved keywords from {SourceUrl}: {string.Join(", ", unexpectedOmissions)}. Add them to the enum.");
    }

    private const string SourceUrl = "https://learn.microsoft.com/en-us/sql/t-sql/language-elements/reserved-keywords-transact-sql";
}
