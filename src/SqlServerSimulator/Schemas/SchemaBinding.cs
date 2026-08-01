using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The <c>WITH SCHEMABINDING</c> dependency gate. Answers two questions from a
/// schema-bound module's stored body text: <em>which schema-bound modules
/// reference this object (or this column)</em>, which is what turns a
/// <c>DROP</c> / <c>ALTER</c> of the referenced object into
/// <strong>Msg 3729</strong> / <strong>Msg 5074</strong> / <strong>Msg
/// 15336</strong> / <strong>Msg 15348</strong>; and <em>does this body's own
/// reference set satisfy schema binding's rules</em>, which is
/// <strong>Msg 4512</strong> / <strong>Msg 4513</strong> at CREATE.
/// </summary>
/// <remarks>
/// <para>
/// <b>No stored dependency record.</b> The reference set is recomputed from
/// the body on every gate check rather than recorded at CREATE. A module's
/// dependencies die with the module and travel with a replacement body for
/// free that way, so there is no registry to invalidate on ALTER / DROP /
/// <c>ALTER SCHEMA TRANSFER</c>. The walk is gated on
/// <see cref="View.IsSchemaBound"/> / <see cref="UserDefinedFunction.IsSchemaBound"/>,
/// so a database with no schema-bound modules pays only a dictionary sweep,
/// and DDL is the only caller.
/// </para>
/// <para>
/// <b>Body walk.</b> Scalar-function and multi-statement-TVF bodies are stored
/// as source text and re-parsed per call, so there is no expression tree to
/// visit; the analysis re-tokenizes the body and lifts every dotted name out
/// of the token stream, which reaches all four module kinds through one
/// mechanism. <see cref="ModuleDeterminism"/> keeps its own walk of the same
/// token stream: it bails at the first nondeterministic built-in and needs
/// neither column names nor FROM-clause positions, so the two answer different
/// questions from the same shape.
/// </para>
/// <para>
/// <b>Column granularity is name-based.</b> Real tracks the exact columns a
/// schema-bound body binds (dropping an unreferenced column of a referenced
/// table succeeds — probe-confirmed). Column references resolve per row
/// through a name-keyed resolver rather than a parse-time (table, ordinal)
/// binding, so there is no column-binding record to consult; a module counts
/// as depending on column <c>C</c> of table <c>T</c> when it references
/// <c>T</c> <i>and</i> its body mentions the identifier <c>C</c> somewhere.
/// That is exact for the single-table bodies schema binding is used for, and
/// over-restrictive only when a body joins two referenced tables that share a
/// column name and touches just one of them.
/// </para>
/// </remarks>
internal static class SchemaBinding
{
    /// <summary>
    /// One name lifted out of a module body's token stream:
    /// <c>leaf</c>, <c>qualifier.leaf</c>, or a longer dotted chain.
    /// </summary>
    private readonly struct BodyName(
        string? qualifier, string leaf, int segmentCount, string text, bool isCall, bool inSourcePosition)
    {
        /// <summary>The chain's immediate qualifier, or null when the name is one part.</summary>
        public readonly string? Qualifier = qualifier;

        /// <summary>The chain's last segment.</summary>
        public readonly string Leaf = leaf;

        /// <summary>Number of dot-separated segments — 1 for <c>t</c>, 2 for <c>dbo.t</c>, 3 for <c>db.dbo.t</c>.</summary>
        public readonly int SegmentCount = segmentCount;

        /// <summary>The chain as written, for the Msg 4512 message body.</summary>
        public readonly string Text = text;

        /// <summary>True when an open paren follows the leaf — a function call or a hint list.</summary>
        public readonly bool IsCall = isCall;

        /// <summary>True when the chain sits directly after <c>FROM</c> / <c>JOIN</c> / <c>APPLY</c>.</summary>
        public readonly bool InSourcePosition = inSourcePosition;
    }

    /// <summary>
    /// The schema-bound module with the lowest object id that references
    /// <paramref name="target"/>, or null when nothing does. Real names one
    /// blocker even when several qualify, and picks the oldest
    /// (probe-confirmed against two views over one table).
    /// </summary>
    internal static SchemaObject? FindReferencingModule(Database database, SchemaObject target)
    {
        var matches = ReferencingModules(database, target, columnName: null);
        return matches.Count == 0 ? null : matches[0];
    }

    /// <summary>
    /// Leaf names of every schema-bound module that references
    /// <paramref name="table"/> and mentions <paramref name="columnName"/>,
    /// ordered by object id — the Msg 5074 blocker lines <c>ALTER TABLE DROP
    /// COLUMN</c> / <c>ALTER COLUMN</c> add to their own constraint and index
    /// blockers.
    /// </summary>
    internal static List<string> ColumnReferencingModuleNames(Database database, HeapTable table, string columnName)
    {
        var matches = ReferencingModules(database, table, columnName);
        var names = new List<string>(matches.Count);
        foreach (var match in matches)
            names.Add(match.Name);
        return names;
    }

    /// <summary>
    /// Applies the rules real puts on a schema-bound body's own references:
    /// <strong>Msg 4512</strong> when a FROM-clause name isn't two-part, and
    /// <strong>Msg 4513</strong> when a referenced view or function isn't
    /// itself schema bound. <paramref name="moduleKind"/> is the word real
    /// echoes (<c>view</c> / <c>function</c>).
    /// </summary>
    /// <remarks>
    /// The one-part leg only fires for a name that resolves to an object in
    /// the default schema, which keeps a derived-table alias — which the token
    /// stream doesn't distinguish from a table — out of the message. A name a
    /// leading <c>WITH</c> prefix declares is excluded outright: real reads a
    /// one-part CTE reference as the CTE even when the default schema holds a
    /// table of that name (probe-confirmed — a schema-bound body whose CTE
    /// shadows <c>dbo.t</c> creates).
    /// </remarks>
    internal static void EnforceBody(Database database, string moduleKind, string qualifiedModuleName, string bodyText)
    {
        var tokens = Tokenize(bodyText);
        var cteNames = DeclaredCteNames(tokens);
        foreach (var name in ScanNames(tokens))
        {
            var resolved = name.SegmentCount == 2 ? Resolve(database, name.Qualifier!, name.Leaf) : null;
            if (name.InSourcePosition
                && !name.IsCall
                && (name.SegmentCount >= 3
                    || (name.SegmentCount == 1
                        && !cteNames.Contains(name.Leaf)
                        && Resolve(database, Database.DefaultSchemaName, name.Leaf) is not null)))
            {
                throw SimulatedSqlException.CannotSchemaBindInvalidName(moduleKind, qualifiedModuleName, name.Text);
            }
            switch (resolved)
            {
                case View { IsSchemaBound: false } view:
                    throw SimulatedSqlException.CannotSchemaBindNotSchemaBound(
                        moduleKind, qualifiedModuleName, $"{view.Schema.Name}.{view.Name}");
                case UserDefinedFunction { IsSchemaBound: false } function:
                    throw SimulatedSqlException.CannotSchemaBindNotSchemaBound(
                        moduleKind, qualifiedModuleName, $"{function.Schema.Name}.{function.Name}");
            }
        }
    }

    /// <summary>
    /// Every schema-bound module referencing <paramref name="target"/> —
    /// and, when <paramref name="columnName"/> is non-null, also mentioning
    /// that identifier — ordered by object id. A module never blocks itself.
    /// </summary>
    private static List<SchemaObject> ReferencingModules(Database database, SchemaObject target, string? columnName)
    {
        List<SchemaObject> matches = [];
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var view in schema.Views.Values)
            {
                if (view.IsSchemaBound)
                    AddWhenReferencing(database, view, view.BodyText, target, columnName, matches);
            }
            foreach (var function in schema.Functions.Values)
            {
                if (function.IsSchemaBound)
                    AddWhenReferencing(database, function, function.BodyText, target, columnName, matches);
            }
        }
        matches.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));
        return matches;
    }

    private static void AddWhenReferencing(
        Database database, SchemaObject module, string bodyText,
        SchemaObject target, string? columnName, List<SchemaObject> matches)
    {
        if (ReferenceEquals(module, target))
            return;
        var referencesTarget = false;
        var mentionsColumn = columnName is null;
        foreach (var name in ScanNames(Tokenize(bodyText)))
        {
            if (name.SegmentCount == 2 && Resolve(database, name.Qualifier!, name.Leaf) is { } resolved)
            {
                referencesTarget |= ReferenceEquals(resolved, target);
            }
            else if (columnName is not null && !name.IsCall && database.Collation.Equals(name.Leaf, columnName))
            {
                mentionsColumn = true;
            }
        }
        if (referencesTarget && mentionsColumn)
            matches.Add(module);
    }

    /// <summary>
    /// Resolves <c>schema.leaf</c> against the shared object namespace —
    /// tables, views and functions, the three kinds a schema-bound body can
    /// reference. A name that resolves to nothing (a table alias in
    /// <c>t.col</c>, an unknown schema) yields null.
    /// </summary>
    private static SchemaObject? Resolve(Database database, string qualifier, string leaf) =>
        !database.Schemas.TryGetValue(qualifier, out var schema) ? null
        : schema.HeapTables.TryGetValue(leaf, out var table) ? table
        : schema.Views.TryGetValue(leaf, out var view) ? view
        : schema.Functions.TryGetValue(leaf, out var function) ? function
        : null;

    /// <summary>
    /// Re-tokenizes <paramref name="body"/> into the significant-token list the
    /// name walk and the CTE-name walk both read. The body always tokenizes —
    /// the CREATE-time parser walked the same text to find its end.
    /// </summary>
    private static List<Token> Tokenize(string body)
    {
        List<Token> tokens = [];
        var index = 0;
        while (Tokenizer.NextToken(body, ref index, Collation.Baseline) is { } token)
        {
            if (token is not (Whitespace or Comment))
                tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>
    /// The names a body's leading <c>WITH cte [(col, …)] AS (…) [, …]</c>
    /// prefix declares — the one position a CTE can occupy, since real rejects
    /// a WITH anywhere else a body's query could start. Empty for a body that
    /// doesn't open with WITH.
    /// </summary>
    private static HashSet<string> DeclaredCteNames(List<Token> tokens)
    {
        if (tokens.Count == 0 || tokens[0] is not ReservedKeyword { Keyword: Keyword.With })
            return [];

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 1;
        while (i < tokens.Count && tokens[i] is Name cteName)
        {
            _ = names.Add(cteName.Value);
            i++;
            if (i < tokens.Count && tokens[i] is Operator { Character: '(' })
                i = PastParenGroup(tokens, i);
            if (i >= tokens.Count || tokens[i] is not ReservedKeyword { Keyword: Keyword.As })
                break;
            i++;
            if (i >= tokens.Count || tokens[i] is not Operator { Character: '(' })
                break;
            i = PastParenGroup(tokens, i);
            if (i >= tokens.Count || tokens[i] is not Operator { Character: ',' })
                break;
            i++;
        }
        return names;
    }

    /// <summary>
    /// The index just past the <c>)</c> matching the <c>(</c> at
    /// <paramref name="openIndex"/>, or the end of the list when the body's
    /// parens don't balance (which the CREATE-time parse already ruled out).
    /// </summary>
    private static int PastParenGroup(List<Token> tokens, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    if (depth == 0)
                        return i + 1;
                    break;
            }
        }
        return tokens.Count;
    }

    /// <summary>
    /// Lifts every dotted name chain out of <paramref name="tokens"/>.
    /// Reserved keywords aren't <see cref="Name"/> tokens, so <c>SELECT</c> /
    /// <c>FROM</c> / <c>JOIN</c> never appear as names; contextual keywords do,
    /// and fall out as unresolvable one-part names.
    /// </summary>
    private static List<BodyName> ScanNames(List<Token> tokens)
    {
        List<BodyName> names = [];
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is not Name)
                continue;
            var leafIndex = i;
            var segments = 1;
            while (leafIndex + 2 < tokens.Count
                && tokens[leafIndex + 1] is Operator { Character: '.' }
                && tokens[leafIndex + 2] is Name)
            {
                leafIndex += 2;
                segments++;
            }
            names.Add(new BodyName(
                qualifier: segments >= 2 ? ((Name)tokens[leafIndex - 2]).Value : null,
                leaf: ((Name)tokens[leafIndex]).Value,
                segmentCount: segments,
                text: DottedText(tokens, i, leafIndex),
                isCall: leafIndex + 1 < tokens.Count && tokens[leafIndex + 1] is Operator { Character: '(' },
                inSourcePosition: i > 0 && IntroducesSource(tokens[i - 1])));
            i = leafIndex;
        }
        return names;
    }

    /// <summary>Renders a name chain's segments joined by dots, dropping any delimiters they were written with.</summary>
    private static string DottedText(List<Token> tokens, int firstIndex, int leafIndex)
    {
        if (firstIndex == leafIndex)
            return ((Name)tokens[firstIndex]).Value;
        var text = new System.Text.StringBuilder();
        for (var i = firstIndex; i <= leafIndex; i += 2)
        {
            if (i > firstIndex)
                _ = text.Append('.');
            _ = text.Append(((Name)tokens[i]).Value);
        }
        return text.ToString();
    }

    /// <summary>
    /// Whether <paramref name="token"/> puts the name that follows it in a
    /// FROM-clause source position — the only position whose name shape
    /// Msg 4512 governs.
    /// </summary>
    private static bool IntroducesSource(Token token) => token
        is ReservedKeyword { Keyword: Keyword.From or Keyword.Join }
        or UnquotedString { ContextualKeyword: ContextualKeyword.Apply };
}
