using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SqlServerSimulator;

[TestClass]
public partial class QualityTests
{
    [TestMethod]
    [Description("Prevents unintentional expansion of the public API.")]
    public void PublicApiWhitelist()
    {
        var publicTypes = typeof(Simulation)
            .Assembly
            .GetTypes()
            .Where(type => type.IsPublic)
            .ToArray();

        // Per-type whitelist of member names declared directly on the type.
        // Property / event / operator accessors and compiler-generated members
        // (record <Clone>$, the C# 14 extension's <G>$... nested type) are
        // filtered before comparison, so the entries below read as the
        // human-meaningful API surface.
        Dictionary<Type, HashSet<string>> allowedMembers = new()
        {
            [typeof(Simulation)] = [
                ".ctor",
                nameof(Simulation.CreateDbConnection),
                nameof(Simulation.ImportBacpac),
                nameof(Simulation.AddRemoteSimulation),
                nameof(Simulation.ServerCollationName),
                nameof(Simulation.EnableClr),
                nameof(Simulation.ListenLocalAsync),
                nameof(Simulation.ListenNetworkAsync),
            ],
            [typeof(SimulatedNetworkListener)] = [
                nameof(SimulatedNetworkListener.Port),
                nameof(SimulatedNetworkListener.ServerCertificate),
                nameof(SimulatedNetworkListener.Dispose),
                nameof(SimulatedNetworkListener.DisposeAsync),
            ],
            [typeof(SimulatedNetworkListenerOptions)] = [
                ".ctor",
                nameof(SimulatedNetworkListenerOptions.Port),
                nameof(SimulatedNetworkListenerOptions.BindAddress),
                nameof(SimulatedNetworkListenerOptions.ServerCertificate),
            ],
            [typeof(SimulatedDbConnection)] = [
                nameof(SimulatedDbConnection.InfoMessage),
                nameof(SimulatedDbConnection.ConnectionString),
                nameof(SimulatedDbConnection.Database),
                nameof(SimulatedDbConnection.DataSource),
                nameof(SimulatedDbConnection.ServerVersion),
                nameof(SimulatedDbConnection.State),
                nameof(SimulatedDbConnection.ChangeDatabase),
                nameof(SimulatedDbConnection.Close),
                nameof(SimulatedDbConnection.Open),
                nameof(SimulatedDbConnection.CreateCommand),
                nameof(SimulatedDbConnection.BeginTransaction),
            ],
            [typeof(SimulatedDbCommand)] = [
                nameof(SimulatedDbCommand.CommandText),
                nameof(SimulatedDbCommand.CommandTimeout),
                nameof(SimulatedDbCommand.CommandType),
                nameof(SimulatedDbCommand.DesignTimeVisible),
                nameof(SimulatedDbCommand.UpdatedRowSource),
                nameof(SimulatedDbCommand.Cancel),
                nameof(SimulatedDbCommand.ExecuteNonQuery),
                nameof(SimulatedDbCommand.ExecuteScalar),
                nameof(SimulatedDbCommand.Prepare),
                nameof(SimulatedDbCommand.CreateParameter),
                nameof(SimulatedDbCommand.Parameters),
                nameof(SimulatedDbCommand.Connection),
                nameof(SimulatedDbCommand.Transaction),
                nameof(SimulatedDbCommand.ExecuteReader),
            ],
            [typeof(SimulatedDbTransaction)] = [
                nameof(SimulatedDbTransaction.IsolationLevel),
                nameof(SimulatedDbTransaction.Commit),
                nameof(SimulatedDbTransaction.Rollback),
                nameof(SimulatedDbTransaction.Connection),
            ],
            [typeof(SimulatedDbDataReader)] = [
                "Item",
                nameof(SimulatedDbDataReader.Depth),
                nameof(SimulatedDbDataReader.FieldCount),
                nameof(SimulatedDbDataReader.HasRows),
                nameof(SimulatedDbDataReader.IsClosed),
                nameof(SimulatedDbDataReader.RecordsAffected),
                nameof(SimulatedDbDataReader.GetBoolean),
                nameof(SimulatedDbDataReader.GetByte),
                nameof(SimulatedDbDataReader.GetBytes),
                nameof(SimulatedDbDataReader.GetChar),
                nameof(SimulatedDbDataReader.GetChars),
                nameof(SimulatedDbDataReader.GetDataTypeName),
                nameof(SimulatedDbDataReader.GetDateTime),
                nameof(SimulatedDbDataReader.GetDecimal),
                nameof(SimulatedDbDataReader.GetDouble),
                nameof(SimulatedDbDataReader.GetEnumerator),
                nameof(SimulatedDbDataReader.GetFieldType),
                nameof(SimulatedDbDataReader.GetFloat),
                nameof(SimulatedDbDataReader.GetGuid),
                nameof(SimulatedDbDataReader.GetInt16),
                nameof(SimulatedDbDataReader.GetInt32),
                nameof(SimulatedDbDataReader.GetInt64),
                nameof(SimulatedDbDataReader.GetName),
                nameof(SimulatedDbDataReader.GetOrdinal),
                nameof(SimulatedDbDataReader.GetString),
                nameof(SimulatedDbDataReader.GetValue),
                nameof(SimulatedDbDataReader.GetFieldValue),
                nameof(SimulatedDbDataReader.GetValues),
                nameof(SimulatedDbDataReader.IsDBNull),
                nameof(SimulatedDbDataReader.NextResult),
                nameof(SimulatedDbDataReader.Read),
                // Overridden rather than inherited: DbDataReader's base Close
                // is a no-op, and closing has to run the batch's remaining
                // statements so RecordsAffected is final afterward.
                nameof(SimulatedDbDataReader.Close),
            ],
            [typeof(SimulatedSqlException)] = [
                nameof(SimulatedSqlException.ErrorCode),
                nameof(SimulatedSqlException.IsTransient),
                nameof(SimulatedSqlException.Number),
                nameof(SimulatedSqlException.Class),
                nameof(SimulatedSqlException.State),
                nameof(SimulatedSqlException.Errors),
                nameof(SimulatedSqlException.LineNumber),
                nameof(SimulatedSqlException.Procedure),
                nameof(SimulatedSqlException.Server),
            ],
            [typeof(SimulatedError)] = [
                nameof(SimulatedError.Class),
                nameof(SimulatedError.LineNumber),
                nameof(SimulatedError.Message),
                nameof(SimulatedError.Number),
                nameof(SimulatedError.Procedure),
                nameof(SimulatedError.Server),
                nameof(SimulatedError.Source),
                nameof(SimulatedError.State),
                nameof(SimulatedError.ToString),
            ],
            [typeof(SimulatedErrorCollection)] = [
                "Item",
                nameof(SimulatedErrorCollection.Count),
                nameof(SimulatedErrorCollection.CopyTo),
                nameof(SimulatedErrorCollection.GetEnumerator),
            ],
            [typeof(SimulatedInfoMessageEventArgs)] = [
                nameof(SimulatedInfoMessageEventArgs.Errors),
                nameof(SimulatedInfoMessageEventArgs.LineNumber),
                nameof(SimulatedInfoMessageEventArgs.Message),
                nameof(SimulatedInfoMessageEventArgs.Source),
            ],
            [typeof(SimulatedDbParameter)] = [
                ".ctor",
                nameof(SimulatedDbParameter.DbType),
                nameof(SimulatedDbParameter.Direction),
                nameof(SimulatedDbParameter.IsNullable),
                nameof(SimulatedDbParameter.ParameterName),
                nameof(SimulatedDbParameter.Size),
                nameof(SimulatedDbParameter.SourceColumn),
                nameof(SimulatedDbParameter.SourceColumnNullMapping),
                nameof(SimulatedDbParameter.Value),
                nameof(SimulatedDbParameter.TypeName),
                nameof(SimulatedDbParameter.ResetDbType),
            ],
            [typeof(SimulatedDbParameterCollection)] = [
                ".ctor",
                "Item",
                nameof(SimulatedDbParameterCollection.Count),
                nameof(SimulatedDbParameterCollection.SyncRoot),
                nameof(SimulatedDbParameterCollection.Add),
                nameof(SimulatedDbParameterCollection.AddRange),
                nameof(SimulatedDbParameterCollection.Clear),
                nameof(SimulatedDbParameterCollection.Contains),
                nameof(SimulatedDbParameterCollection.CopyTo),
                nameof(SimulatedDbParameterCollection.GetEnumerator),
                nameof(SimulatedDbParameterCollection.IndexOf),
                nameof(SimulatedDbParameterCollection.Insert),
                nameof(SimulatedDbParameterCollection.Remove),
                nameof(SimulatedDbParameterCollection.RemoveAt),
            ],
            [typeof(BacpacImportOptions)] = [
                ".ctor",
                nameof(BacpacImportOptions.DatabaseName),
                nameof(BacpacImportOptions.MaxDegreeOfParallelism),
                nameof(Equals),
                nameof(GetHashCode),
                nameof(ToString),
            ],
            [typeof(BacpacImportResult)] = [
                ".ctor",
                nameof(BacpacImportResult.ElementCounts),
                nameof(BacpacImportResult.Skipped),
                nameof(BacpacImportResult.Warnings),
            ],
            [typeof(BacpacSkipped)] = [
                ".ctor",
                "Deconstruct",
                nameof(BacpacSkipped.ElementName),
                nameof(BacpacSkipped.ElementType),
                nameof(BacpacSkipped.Reason),
                nameof(Equals),
                nameof(GetHashCode),
                nameof(ToString),
            ],
        };

        Assert.HasCount(allowedMembers.Count, publicTypes);
        foreach (var type in publicTypes)
            Assert.Contains(type, allowedMembers.Keys);

        // One namespace is the whole contract, so `using SqlServerSimulator;`
        // reaches everything and no consumer is sent into a namespace named
        // after an implementation layer. The bacpac trio shipped in
        // SqlServerSimulator.Storage.Bacpac through 1.0.0 and moved here for
        // 2.0.0 — a nested namespace can't be refactored afterward without
        // breaking consumers, which is the cost this pins.
        foreach (var type in publicTypes)
            Assert.AreEqual(nameof(SqlServerSimulator), type.Namespace, $"{type.Name} is public outside the root namespace.");

        foreach (var (type, allowedNames) in allowedMembers)
        {
            var memberNames = type
                .GetMembers()
                .Where(member => member.DeclaringType == type)
                .Where(member => member.Name[0] != '<')
                .Where(member => member is not MethodInfo mi || !mi.IsSpecialName)
                .Select(member => member.Name)
                .ToHashSet();

            Assert.HasCount(allowedNames.Count, memberNames, $"Member count mismatch on {type.FullName}");
            foreach (var name in memberNames)
                Assert.Contains(name, allowedNames, $"Unexpected public member '{name}' on {type.FullName}");
        }
    }

    /// <summary>
    /// A public type's documentation must carry exactly one <c>summary</c> and
    /// at most one <c>remarks</c>.
    /// </summary>
    /// <remarks>
    /// The compiler concatenates the doc comment of <em>every</em> partial
    /// declaration into the single entry a consumer reads in IntelliSense, in
    /// compilation order — so a note describing one partial file's contents,
    /// written as <c>///</c> on the type it happens to sit above, silently
    /// prepends itself to the type's summary and can outrank the sentence
    /// saying what the type is. It shipped that way once: the summary of
    /// <see cref="Simulation"/> opened on application-lock internals. A note
    /// about a file's contents is a <c>//</c> comment.
    /// </remarks>
    [TestMethod]
    [Description("Keeps a partial file's own notes out of the type summary consumers read.")]
    public void PublicTypeDocsHaveOneSummary()
    {
        var documented = PublicApiDocumentation();
        Assert.IsGreaterThan(20, documented.Count, "Documentation extraction found implausibly few public entries.");

        var concatenated = documented
            .Where(entry => entry.Key[0] == 'T')
            .Where(entry => entry.Value.Elements("summary").Count() != 1 || entry.Value.Elements("remarks").Count() > 1)
            .Select(entry => $"{entry.Key[2..]}: {entry.Value.Elements("summary").Count()} summaries, "
                + $"{entry.Value.Elements("remarks").Count()} remarks")
            .Order()
            .ToArray();

        Assert.IsEmpty(
            concatenated,
            "Public types whose documentation is the concatenation of several partials' doc comments. "
            + $"Demote all but the canonical file's to a // comment.{Environment.NewLine}"
            + string.Join(Environment.NewLine, concatenated));
    }

    /// <summary>
    /// No <c>see cref</c> in a public member's documentation may point at a
    /// non-public one.
    /// </summary>
    /// <remarks>
    /// Such a cref dangles in consumer IntelliSense and implies stability for a
    /// name we're free to rename; state the contract in prose instead. The
    /// compiler's own CS1574 can't see this — the target resolves fine, it's
    /// just invisible outside the assembly. Watch for the shadowing case, where
    /// the cref reads like a public type but binds to a same-named member of
    /// the enclosing type: <c>cref="Simulation"</c> inside
    /// <see cref="SimulatedDbConnection"/> is its internal field until it's
    /// written namespace-qualified.
    /// </remarks>
    [TestMethod]
    [Description("Keeps internal names out of the documentation consumers read.")]
    public void PublicApiDocsAvoidInternalCrefs()
    {
        var visible = PublicApiNames();
        Assert.IsGreaterThan(100, visible.Count, "Public-surface enumeration found implausibly few names.");

        var dangling = PublicApiDocumentation()
            .SelectMany(entry => entry.Value
                .Descendants()
                .Select(element => (string?)element.Attribute("cref"))
                .Where(cref => cref is not null)
                .Select(cref => (Cref: cref!, Target: DocumentationTarget(cref!)))
                .Where(cref => cref.Target.StartsWith("SqlServerSimulator", StringComparison.Ordinal)
                    && !visible.Contains(cref.Target))
                .Select(cref => $"{entry.Key[2..]} -> {cref.Cref}"))
            .Distinct()
            .Order()
            .ToArray();

        Assert.IsEmpty(
            dangling,
            "Public-API documentation naming internals. Replace the cref with prose, or namespace-qualify "
            + $"one that a same-named member of the enclosing type is shadowing.{Environment.NewLine}"
            + string.Join(Environment.NewLine, dangling));
    }

    /// <summary>
    /// The documentation entries belonging to the public surface, keyed by
    /// their documentation id. The generated XML carries every documented
    /// member whatever its accessibility, so the internal ones are dropped
    /// here — the rules above are about what a consumer sees.
    /// </summary>
    private static Dictionary<string, XElement> PublicApiDocumentation()
    {
        var xml = Path.ChangeExtension(typeof(Simulation).Assembly.Location, ".xml");
        Assert.IsTrue(File.Exists(xml), $"No documentation file at {xml}; GenerateDocumentationFile may be off.");

        var visible = PublicApiNames();
        return XDocument
            .Load(xml)
            .Descendants("member")
            .Select(member => (Id: (string)member.Attribute("name")!, Element: member))
            .Where(entry => visible.Contains(DocumentationTarget(entry.Id)))
            .ToDictionary(entry => entry.Id, entry => entry.Element, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every public type's full name, plus <c>Type.Member</c> for each member
    /// of one a consumer can reach — which is what a documentation id reduces
    /// to once <see cref="DocumentationTarget"/> has taken the signature off.
    /// Protected members count: a consumer can't derive from these sealed
    /// types, but the accessibility is public-surface metadata either way.
    /// </summary>
    private static HashSet<string> PublicApiNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var type in typeof(Simulation).Assembly.GetTypes().Where(type => type.IsPublic))
        {
            _ = names.Add(type.FullName!);
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (IsConsumerVisible(member))
                    _ = names.Add($"{type.FullName}.{(member is ConstructorInfo ? "#ctor" : member.Name)}");
            }
        }

        return names;
    }

    private static bool IsConsumerVisible(MemberInfo member) => member switch
    {
        FieldInfo field => field.IsPublic || field.IsFamily,
        MethodBase method => method.IsPublic || method.IsFamily,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(IsConsumerVisible),
        EventInfo @event => @event.GetAddMethod(nonPublic: true) is { } add && IsConsumerVisible(add),
        Type nested => nested.IsNestedPublic || nested.IsNestedFamily,
        _ => false,
    };

    /// <summary>
    /// Reduces a documentation id — <c>M:Ns.Type.Method(System.Int32)</c> — to
    /// the <c>Ns.Type.Method</c> that <see cref="PublicApiNames"/> is keyed by,
    /// dropping the one-letter kind prefix, the parameter list, a conversion
    /// operator's trailing return type and any generic arity.
    /// </summary>
    private static string DocumentationTarget(string documentationId)
    {
        var target = documentationId.Length > 2 && documentationId[1] == ':' ? documentationId[2..] : documentationId;
        var parameters = target.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0)
            target = target[..parameters];

        return DocumentationAritySuffix.Replace(target, "");
    }

    /// <summary>The <c>`1</c> / <c>``1</c> a generic type or method carries in a documentation id.</summary>
    [GeneratedRegex(@"``?\d+")]
    private static partial Regex DocumentationAritySuffix { get; }

    /// <summary>
    /// Every SQL Server message number a Markdown file cites must be one the
    /// simulator actually raises — meaning a numeric literal in an
    /// <c>Errors/SimulatedSqlException.*.cs</c> factory — or an acknowledged
    /// entry in <see cref="CitedButNotRaised"/>.
    /// </summary>
    /// <remarks>
    /// Prose restating the code is the documentation that drifts, and a message
    /// number is the most-restated thing in this repo's docs. This pins them: a
    /// renumbered, deleted or mistyped citation fails the build the way an SSS
    /// analyzer violation does, rather than waiting to be noticed.
    /// <para>The gap list is checked in both directions, so it can't rot either
    /// — an entry that starts being raised has to move out of it, which is what
    /// makes closing one of these gaps update its own documentation.</para>
    /// </remarks>
    [TestMethod]
    [Description("Pins every message number cited in Markdown to a factory that raises it.")]
    public void DocumentedMessageNumbersAreRaised()
    {
        var repoRoot = FindRepoRoot();
        var errorSources = string.Concat(Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src", "SqlServerSimulator", "Errors"), "*.cs")
            .Select(File.ReadAllText));
        var raised = NumberPattern.Matches(errorSources).Select(m => int.Parse(m.Value)).ToHashSet();

        // Sanity-check the extraction itself: a regex that stopped matching
        // would make every assertion below vacuously pass.
        Assert.IsGreaterThan(600, raised.Count, "Message-number extraction found implausibly few factories.");

        foreach (var stillMissing in CitedButNotRaised.Where(raised.Contains).Order())
        {
            Assert.Fail(
                $"Msg {stillMissing} is now raised by a factory but is still listed in "
                + $"{nameof(CitedButNotRaised)}. Remove it, and check the docs describing it as unbuilt.");
        }

        var markdown = Directory
            .EnumerateFiles(repoRoot, "*.md", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal));

        List<string> unknown = [];
        foreach (var path in markdown)
        {
            foreach (Match match in CitationPattern.Matches(File.ReadAllText(path)))
            {
                var number = int.Parse(match.Groups[1].Value);
                if (!raised.Contains(number) && !CitedButNotRaised.Contains(number))
                    unknown.Add($"{Path.GetRelativePath(repoRoot, path)}: Msg {number}");
            }
        }

        Assert.IsEmpty(
            unknown.Distinct().Order(),
            "Markdown cites message numbers no error factory raises. Either the citation is stale, or "
            + $"the message is one real raises and the simulator doesn't — in which case add it to {nameof(CitedButNotRaised)}.");
    }

    /// <summary>
    /// Every <c>SomeFile.cs</c> a Markdown file names must exist, and every
    /// cross-document link must resolve to a real file and a real heading.
    /// </summary>
    /// <remarks>
    /// A code pointer that no longer resolves is the same failure as a stale
    /// message number — the doc reads as authoritative and sends the reader
    /// nowhere. The shapes this catches: a file folded into its caller, a
    /// shorthand name that omits the type's own prefix, a renamed member, and a
    /// heading whose slug nobody could have predicted.
    /// </remarks>
    [TestMethod]
    [Description("Pins every file reference and cross-document link in Markdown to something that exists.")]
    public void DocumentedReferencesResolve()
    {
        var repoRoot = FindRepoRoot();
        var sourceNames = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repoRoot, "tests"), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsGreaterThan(300, sourceNames.Count, "Source-file enumeration found implausibly few files.");

        var docsDirectory = Path.Combine(repoRoot, "docs", "claude");
        List<string> broken = [];
        foreach (var path in Directory.EnumerateFiles(docsDirectory, "*.md").Append(Path.Combine(repoRoot, "CLAUDE.md")))
        {
            var text = File.ReadAllText(path);
            var name = Path.GetFileName(path);

            foreach (Match match in SourceFilePattern.Matches(text))
            {
                if (!sourceNames.Contains(Path.GetFileName(match.Groups[1].Value)))
                    broken.Add($"{name}: no such source file `{match.Groups[1].Value}`");
            }

            foreach (Match match in DocLinkPattern.Matches(text))
            {
                var targetPath = Path.Combine(docsDirectory, match.Groups[1].Value);
                if (!File.Exists(targetPath))
                {
                    broken.Add($"{name}: no such document `{match.Groups[1].Value}`");
                    continue;
                }

                var anchor = match.Groups[2].Value;
                if (anchor.Length != 0 && !HeadingSlugs(File.ReadAllText(targetPath)).Contains(anchor[1..]))
                    broken.Add($"{name}: no heading `{anchor}` in `{match.Groups[1].Value}`");
            }
        }

        Assert.IsEmpty(broken.Distinct().Order(), "Markdown references that no longer resolve.");
    }

    /// <summary>
    /// GitHub's heading-anchor slug: lowercase, drop everything that isn't
    /// alphanumeric / space / hyphen, then hyphenate the spaces. A heading whose
    /// slug is hard to predict — one carrying code spans, ellipses and slashes —
    /// is better rewritten than linked to carefully.
    /// </summary>
    private static HashSet<string> HeadingSlugs(string markdown) => HeadingPattern
        .Matches(markdown)
        .Select(match => SlugStripPattern.Replace(match.Groups[1].Value.ToLowerInvariant(), "").Replace(' ', '-'))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>A backticked source-file name, the way the docs point at code.</summary>
    [GeneratedRegex(@"`([A-Za-z0-9_./]+\.cs)`")]
    private static partial Regex SourceFilePattern { get; }

    /// <summary>A relative link to a sibling document, with an optional heading anchor.</summary>
    [GeneratedRegex(@"\]\(([a-z0-9-]+\.md)(#[a-z0-9-]+)?\)")]
    private static partial Regex DocLinkPattern { get; }

    [GeneratedRegex(@"^#+ (.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern { get; }

    [GeneratedRegex("[^a-z0-9 -]")]
    private static partial Regex SlugStripPattern { get; }

    /// <summary>
    /// Message numbers the docs name but no factory raises: every one is a case
    /// real SQL Server reports and the simulator doesn't yet, which is exactly
    /// the kind of claim worth writing down because no amount of grepping this
    /// repo would find it. Most carry their reasoning in
    /// <c>docs/claude/backlog.md</c>'s over-permissive register or in the
    /// owning feature's <c>Not modeled yet</c> section.
    /// </summary>
    private static readonly int[] CitedButNotRaised = [
        107, 147, 159, 185, 263, 557, 596, 1784, 1789, 1909,
        2247, 2390, 2396, 3604, 3609, 3920, 3997, 4124, 4624, 4860,
        4919, 4922, 5592, 6947, 7222, 8105, 8622, 8628, 8711, 8729,
        9341, 9803, 9828, 10343, 13519, 15249, 15457, 15459, 15460, 15461,
        15469, 15470, 15471, 15472, 15477, 15574, 15575, 15622, 15647, 16002,
        16003, 16007, 16902, 16909, 16930, 16945, 16956, 16961,
    ];

    /// <summary>Any 3-to-5-digit literal in an error-factory source; message numbers are the only ones that shape occurs in there.</summary>
    [GeneratedRegex(@"\b\d{3,5}\b")]
    private static partial Regex NumberPattern { get; }

    /// <summary>The <c>Msg NNNN</c> spelling the docs use, tolerating the surrounding bold markers.</summary>
    [GeneratedRegex(@"\bMsg \*{0,2}(\d{3,5})\b")]
    private static partial Regex CitationPattern { get; }

    /// <summary>
    /// Walks up from the test assembly to the directory holding the solution,
    /// so the check runs the same from a `dotnet test` of the csproj and from a
    /// direct invocation of the built DLL.
    /// </summary>
    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.sln").Any())
                return directory.FullName;
        }

        throw new InvalidOperationException($"No solution file above {AppContext.BaseDirectory}.");
    }
}
