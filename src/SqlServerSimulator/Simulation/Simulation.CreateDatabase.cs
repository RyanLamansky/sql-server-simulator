using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// One <c>FILEGROUP name [CONTAINS …] [DEFAULT]</c> group of a
    /// <c>CREATE DATABASE</c> file list, or the leading <c>PRIMARY</c> one
    /// (null <see cref="Name"/>), with the file specifications under it.
    /// </summary>
    private sealed class DeclaredFilegroup(string? name, bool isDefault, bool containsSpecialData, bool memoryOptimized = false)
    {
        public readonly string? Name = name;
        public readonly bool IsDefault = isDefault;

        /// <summary>
        /// A <c>CONTAINS FILESTREAM</c> / <c>MEMORY_OPTIMIZED_DATA</c> group,
        /// whose files aren't rows files: a FILESTREAM group's aren't recorded,
        /// a memory-optimized one's become its containers.
        /// </summary>
        public readonly bool ContainsSpecialData = containsSpecialData;

        /// <summary>The <c>CONTAINS MEMORY_OPTIMIZED_DATA</c> group.</summary>
        public readonly bool MemoryOptimized = memoryOptimized;

        public readonly List<FileSpecification> Files = [];
    }

    /// <summary>
    /// Parses <c>CREATE DATABASE &lt;name&gt; [CONTAINMENT = NONE | PARTIAL]
    /// [ON [PRIMARY] &lt;filespec&gt; [, …] [, FILEGROUP name [CONTAINS …]
    /// [DEFAULT] &lt;filespec&gt; [, …]] [LOG ON &lt;filespec&gt; [, …]]]</c>
    /// followed by either <c>FOR ATTACH</c> / <c>AS SNAPSHOT OF</c> or
    /// <c>[COLLATE &lt;collation&gt;] [WITH &lt;option&gt; [, …]]</c>, in that
    /// order. <c>FOR ATTACH</c> builds the database from detached files
    /// (<see cref="AttachDatabase"/>). The database name is a single identifier (bare or bracketed /
    /// quoted); <c>COLLATE</c> sets the new database's collation (defaulting
    /// to the server collation, mirroring <c>model.collation</c>), and
    /// <c>TRUSTWORTHY</c> / <c>DB_CHAINING</c> its two flags. The file list
    /// becomes the database's <see cref="Database.Files"/> and filegroups;
    /// without one the database gets the <c>&lt;db&gt;</c> / <c>&lt;db&gt;_log</c>
    /// pair. The new database is registered with the smallest free
    /// <c>database_id</c> ≥ 5 (see <see cref="RegisterUserDatabase"/>). Cursor
    /// on entry is the <c>DATABASE</c> keyword.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025: a duplicate name raises
    /// <strong>Msg 1801</strong> (Class 16, State 3). The file list (probed
    /// 2026-09-27): the first data file is file 1 on <c>PRIMARY</c> and never
    /// smaller than the 8 MB <c>model</c>'s is, the first log file is file 2,
    /// then the other data files and the other log files in order; a declared
    /// filegroup repeated or named <c>PRIMARY</c> is Msg 5035, a repeated
    /// logical name Msg 1828 (state 5), and a file too small, with a ceiling
    /// under its size or a growth past its ceiling, or on a taken path, is
    /// followed by Msg 1802.
    /// </remarks>
    private bool TryParseCreateDatabase(ParserContext context)
    {
        // Cursor on DATABASE; advance to the database name. A reserved keyword
        // where the name belongs is a syntax error (routes to Msg 102).
        if (context.GetNextRequired() is not Name nameToken)
            return false;
        var databaseName = nameToken.Value;

        // The clauses in real's order: CONTAINMENT, the file list, then either
        // FOR ATTACH / AS SNAPSHOT OF or COLLATE and WITH. Anything out of
        // place is a syntax error near it (probed 2026-10-15 against SQL
        // Server 2025).
        context.MoveNextOptional();
        var partial = false;
        var containmentWritten = IsBareWord(context.Token, "CONTAINMENT");
        if (containmentWritten)
        {
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            partial = IsBareWord(context.GetNextRequired(), "PARTIAL");
            if (!partial && !IsBareWord(context.Token, "NONE"))
                throw SimulatedSqlException.SyntaxErrorNear(context.Token, state: 6);
            context.MoveNextOptional();
        }
        List<DeclaredFilegroup>? groups = null;
        List<FileSpecification>? logFiles = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            groups = ParseCreateDatabaseDataFiles(context);
            if (IsLogOn(context))
                logFiles = ParseCreateDatabaseLogFiles(context);
        }
        else if (IsLogOn(context))
        {
            throw SimulatedSqlException.LogFileWithoutDataFile();
        }

        string? collationName = null;
        var options = new CreateDatabaseOptions();
        string? unbuiltForm = null;
        AttachOptions? attach = null;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.For }:
                var rebuildLog = IsBareWord(context.GetNextRequired(), "ATTACH_REBUILD_LOG");
                if (!rebuildLog && !IsBareWord(context.Token, "ATTACH"))
                    throw SimulatedSqlException.SyntaxErrorNear(context.Token, state: 23);
                attach = new AttachOptions(rebuildLog);
                context.MoveNextOptional();
                if (context.Token is ReservedKeyword { Keyword: Keyword.With })
                    ParseAttachOptions(context, attach);
                if (groups is null)
                    options.MissingPrimaryFile = true;
                break;
            case ReservedKeyword { Keyword: Keyword.As }:
                if (!IsBareWord(context.GetNextRequired(), "SNAPSHOT"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Of })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Name)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
                unbuiltForm = "A database snapshot (CREATE DATABASE … AS SNAPSHOT OF)";
                break;
            default:
                if (context.Token is ReservedKeyword { Keyword: Keyword.Collate })
                {
                    if (context.GetNextRequired() is not UnquotedString collationToken)
                        return false;
                    collationName = collationToken.Value;
                    context.MoveNextOptional();
                }
                if (context.Token is ReservedKeyword { Keyword: Keyword.With })
                {
                    do
                        ParseCreateDatabaseOption(context, options);
                    while (context.Token is Operator { Character: ',' });
                }
                break;
        }
        if (!IsStatementBoundary(context.Token) || context.Token is ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // A file to create needs its logical name, which the batch's compile
        // checks once the statement has read whole; an attach needs none
        // (probed 2026-10-16 against SQL Server 2025).
        if (attach is null
            && (groups?.Exists(static group => group.Files.Exists(static spec => spec.Name is null)) == true || logFiles?.Exists(static spec => spec.Name is null) == true))
        {
            throw SimulatedSqlException.FileOptionRequired("NAME", 16, 2);
        }

        // A skipped (un-taken IF branch) statement validates nothing and has no
        // schema effect — mirror ALTER DATABASE COLLATE, which defers collation
        // resolution past the skip check.
        if (context.Batch.IsSkipping)
            return true;

        // CREATE DATABASE answers at server scope: the CREATE ANY DATABASE
        // permission (or the ALTER ANY DATABASE that covers it), or dbcreator
        // membership. Real reports Msg 262 naming 'master' whatever the current
        // database is (probe-confirmed), and unlike the database-scope CREATE
        // gates' Msg 262 it ends only the statement (probed 2026-09-29 against
        // SQL Server 2025).
        if (!PermissionEnforcement.HasDatabaseDdlAuthority(context.Batch, Permission.CreateAnyDatabase))
            throw SimulatedSqlException.DatabasePermissionDenied("CREATE DATABASE", "master", aborts: false);

        if (options.MissingPrimaryFile)
            throw SimulatedSqlException.AttachNeedsPrimaryFile();
        if (attach is not null)
        {
            if (containmentWritten)
                throw SimulatedSqlException.ContainmentWithAttach();
            var attached = this.AttachDatabase(context.Batch, databaseName, [.. groups!.SelectMany(static group => group.Files), .. logFiles ?? []], attach);
            ExtendedEvents.XeObjectEvents.NoteDatabase(context.Batch, ExtendedEvents.XeEventKind.ObjectCreated, attached);
            RecordServerDdlEvent(context, "CREATE_DATABASE", databaseName, loginName: null);
            return true;
        }
        if (unbuiltForm is not null)
            throw new NotSupportedException($"{unbuiltForm} isn't modeled by the simulator.");
        if (partial)
        {
            if (ConfigurationInUse(ContainedDatabaseAuthenticationConfigurationId) == 0)
                throw SimulatedSqlException.ContainedDatabaseAuthenticationRequired("create");
            throw new NotSupportedException("The simulator does not model contained databases.");
        }
        if (options.ContainedOnlyOption is { } containedOnly)
            throw SimulatedSqlException.OptionNeedsContainment(containedOnly);
        if (options.CatalogCollation is { } catalogCollation)
        {
            throw Collation.Baseline.Equals(catalogCollation, "SQL_Latin1_General_CP1_CI_AS")
                ? new NotSupportedException("CREATE DATABASE … WITH CATALOG_COLLATION = SQL_Latin1_General_CP1_CI_AS isn't modeled; the simulator's catalog follows the database collation.")
                : SimulatedSqlException.InvalidCollation(catalogCollation, state: 5);
        }
        if (options.Ledger)
            throw new NotSupportedException("A ledger database (CREATE DATABASE … WITH LEDGER = ON) isn't modeled by the simulator.");
        if (options.PersistentLogBuffer)
            throw new NotSupportedException("A persistent log buffer (CREATE DATABASE … WITH PERSISTENT_LOG_BUFFER = ON) isn't modeled by the simulator, which has no log.");

        var collation = collationName is null
            ? this.ServerCollation
            : Collation.TryGet(collationName)
                ?? throw new NotSupportedException($"CREATE DATABASE COLLATE: collation '{collationName}' isn't on the simulator's recognized list.");

        Database database;
        lock (this.Databases)
        {
            if (this.Databases.ContainsKey(databaseName))
                throw SimulatedSqlException.DatabaseAlreadyExists(databaseName);
            database = new Database(databaseName, collation)
            {
                Trustworthy = options.Trustworthy,
                CrossDatabaseChaining = options.DbChaining,
            };
            // The creating login owns the new database (probed 2026-09-27
            // against SQL Server 2025 with a dbcreator login); a session with no
            // registered login — the in-process default — creates as sa.
            if (this.Logins.TryGetValue(context.Connection.Security.Effective.LoginName, out var creator))
                database.OwnerLoginName = creator.Name;
            if (groups is not null)
                this.ApplyCreateDatabaseFileList(context.Batch, database, groups, logFiles ?? []);
            else
                this.RejectTakenPaths(context.Batch, database);
            RegisterUserDatabaseLocked(database);
        }
        LogDatabaseStartup(context.Batch, database);
        ExtendedEvents.XeObjectEvents.NoteDatabase(context.Batch, ExtendedEvents.XeEventKind.ObjectCreated, database);
        RecordServerDdlEvent(context, "CREATE_DATABASE", databaseName, loginName: null);
        return true;
    }

    /// <summary>What a <c>CREATE DATABASE</c>'s <c>WITH</c> list and attach clause leave for the statement to apply or refuse once it runs.</summary>
    private sealed class CreateDatabaseOptions
    {
        public bool Trustworthy;
        public bool DbChaining;
        public bool Ledger;

        /// <summary><c>PERSISTENT_LOG_BUFFER = ON</c>, which places the log tail on a DAX volume.</summary>
        public bool PersistentLogBuffer;

        /// <summary><c>FOR ATTACH</c> with no file list, which is Msg 1829.</summary>
        public bool MissingPrimaryFile;

        /// <summary>The first option only a contained database takes, in lower case, which is Msg 12808 on any other.</summary>
        public string? ContainedOnlyOption;

        /// <summary>A <c>CATALOG_COLLATION</c> other than <c>DATABASE_DEFAULT</c>.</summary>
        public string? CatalogCollation;
    }

    /// <summary>
    /// One option of <c>CREATE DATABASE</c>'s <c>WITH</c> list, entered on
    /// <c>WITH</c> or the comma ahead of it and leaving the cursor past the
    /// option. A word that names no option, or an option written in another
    /// option's form, is Msg 102 near the word — state 6 when an <c>=</c>
    /// follows it, else 44 — where a malformed value of the two bare-valued
    /// options is the syntax error at that value (probed 2026-10-15 against
    /// SQL Server 2025).
    /// </summary>
    private static void ParseCreateDatabaseOption(ParserContext context, CreateDatabaseOptions options)
    {
        var optionToken = context.GetNextRequired();
        if (optionToken is not Name { Value: var option })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var hasEquals = context.GetNextOptional() is Operator { Character: '=' };
        var refused = SimulatedSqlException.SyntaxErrorNear(optionToken, state: hasEquals ? (byte)6 : (byte)44);
        Span<char> upper = stackalloc char[option.Length];
        _ = option.AsSpan().ToUpperInvariant(upper);
        switch (upper)
        {
            case "CATALOG_COLLATION":
                if (!hasEquals || context.GetNextOptional() is not Name { Value: var catalogCollation })
                    throw refused;
                if (!BuiltInToken.Equals(catalogCollation, "DATABASE_DEFAULT"))
                    options.CatalogCollation = catalogCollation;
                break;
            case "DB_CHAINING":
                options.DbChaining = ReadBareOnOff(context, hasEquals, refused);
                break;
            case "DEFAULT_FULLTEXT_LANGUAGE":
                ReadContainedOnlyLanguage(context, hasEquals, refused, options, "default_fulltext_language");
                break;
            case "DEFAULT_LANGUAGE":
                ReadContainedOnlyLanguage(context, hasEquals, refused, options, "default_language");
                break;
            case "FILESTREAM":
                if (context.Token is not Operator { Character: '(' })
                    throw refused;
                for (var depth = 1; depth > 0;)
                {
                    switch (context.GetNextRequired())
                    {
                        case Operator { Character: '(' }:
                            depth++;
                            break;
                        case Operator { Character: ')' }:
                            depth--;
                            break;
                    }
                }
                break;
            case "LEDGER":
                options.Ledger = ReadEqualsOnOff(context, hasEquals, refused);
                break;
            case "NESTED_TRIGGERS":
                _ = ReadEqualsOnOff(context, hasEquals, refused);
                options.ContainedOnlyOption ??= "nested_triggers";
                break;
            case "PERSISTENT_LOG_BUFFER":
                if (!hasEquals)
                    throw refused;
                switch (context.GetNextOptional())
                {
                    case ReservedKeyword { Keyword: Keyword.Off }:
                        break;
                    case ReservedKeyword { Keyword: Keyword.On }
                        when context.GetNextOptional() is Operator { Character: '(' }
                            && IsBareWord(context.GetNextOptional(), "DIRECTORY_NAME")
                            && context.GetNextOptional() is Operator { Character: '=' }
                            && context.GetNextOptional() is Literal
                            && context.GetNextOptional() is Operator { Character: ')' }:
                        options.PersistentLogBuffer = true;
                        break;
                    default:
                        throw refused;
                }
                break;
            case "TRANSFORM_NOISE_WORDS":
                _ = ReadEqualsOnOff(context, hasEquals, refused);
                options.ContainedOnlyOption ??= "transform_noise_words";
                break;
            case "TRUSTWORTHY":
                options.Trustworthy = ReadBareOnOff(context, hasEquals, refused);
                break;
            case "TWO_DIGIT_YEAR_CUTOFF":
                if (!hasEquals || context.GetNextOptional() is not Numeric)
                    throw refused;
                options.ContainedOnlyOption ??= "two_digit_year_cutoff";
                break;
            default:
                throw refused;
        }
        context.MoveNextOptional();
    }

    /// <summary><c>TRUSTWORTHY</c> / <c>DB_CHAINING</c>'s bare <c>ON | OFF</c>, whose malformed value is the syntax error at it.</summary>
    private static bool ReadBareOnOff(ParserContext context, bool hasEquals, SimulatedSqlException refused)
    {
        if (hasEquals || context.Token is null)
            throw refused;
        return context.Token is ReservedKeyword { Keyword: Keyword.On or Keyword.Off } onOff
            ? onOff.Keyword == Keyword.On
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>An option's <c>= ON | OFF</c>, whose malformed value is the option's own refusal.</summary>
    private static bool ReadEqualsOnOff(ParserContext context, bool hasEquals, SimulatedSqlException refused) =>
        hasEquals && context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off } onOff
            ? onOff.Keyword == Keyword.On
            : throw refused;

    /// <summary>A default language option's <c>= name | lcid</c>, which only a contained database takes.</summary>
    private static void ReadContainedOnlyLanguage(ParserContext context, bool hasEquals, SimulatedSqlException refused, CreateDatabaseOptions options, string option)
    {
        if (!hasEquals || context.GetNextOptional() is not (Name or Numeric))
            throw refused;
        options.ContainedOnlyOption ??= option;
    }

    /// <summary>Whether the cursor sits on <c>LOG ON</c>, which it then leaves on <c>ON</c>'s successor.</summary>
    private static bool IsLogOn(ParserContext context)
    {
        if (context.Token is not Name { Value: var log } || !BuiltInToken.Equals(log, "LOG"))
            return false;
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.On })
        {
            context.MoveNextRequired();
            return true;
        }
        context.RestoreCheckpoint(checkpoint);
        return false;
    }

    /// <summary>
    /// The data file list after <c>ON</c>, entered on <c>ON</c> and leaving the
    /// cursor on the first token past it: an optional <c>PRIMARY</c>, its file
    /// specifications, then each <c>FILEGROUP</c> with its own.
    /// </summary>
    private static List<DeclaredFilegroup> ParseCreateDatabaseDataFiles(ParserContext context)
    {
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Primary })
            context.MoveNextRequired();
        var groups = new List<DeclaredFilegroup> { new(null, isDefault: false, containsSpecialData: false) };
        groups[0].Files.Add(ParseFileSpecification(context, FileSpecificationSite.CreateDatabase));
        while (context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is Name { Value: var word } && BuiltInToken.Equals(word, "FILEGROUP"))
            {
                var name = context.GetNextRequired() is Name groupName ? groupName.Value : throw SimulatedSqlException.SyntaxErrorNear(context);
                var containsSpecialData = false;
                var memoryOptimized = false;
                if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Contains })
                {
                    containsSpecialData = true;
                    memoryOptimized = context.GetNextRequired() is Name { Value: var contentKind } && BuiltInToken.Equals(contentKind, "MEMORY_OPTIMIZED_DATA");
                    context.MoveNextRequired();
                }
                var isDefault = context.Token is ReservedKeyword { Keyword: Keyword.Default };
                if (isDefault)
                    context.MoveNextRequired();
                if (memoryOptimized && groups.Exists(static group => group.MemoryOptimized))
                    throw SimulatedSqlException.SecondMemoryOptimizedFilegroup();
                groups.Add(new(name, isDefault, containsSpecialData, memoryOptimized));
            }
            groups[^1].Files.Add(ParseFileSpecification(context, FileSpecificationSite.CreateDatabase));
        }
        return groups;
    }

    /// <summary>The log file list after <c>LOG ON</c>, entered on its first specification.</summary>
    private static List<FileSpecification> ParseCreateDatabaseLogFiles(ParserContext context)
    {
        var files = new List<FileSpecification> { ParseFileSpecification(context, FileSpecificationSite.CreateDatabase) };
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            files.Add(ParseFileSpecification(context, FileSpecificationSite.CreateDatabase));
        }
        return files;
    }

    /// <summary>
    /// Replaces a new database's two default files with the declared list and
    /// registers its filegroups, refusing it the way real refuses the
    /// <c>CREATE DATABASE</c>.
    /// </summary>
    private void ApplyCreateDatabaseFileList(BatchContext batch, Database database, List<DeclaredFilegroup> groups, List<FileSpecification> logFiles)
    {
        var declared = new HashSet<string>(database.Collation) { "PRIMARY" };
        foreach (var group in groups)
        {
            if (group.Name is { } name && !declared.Add(name))
                throw SimulatedSqlException.DeclaredFilegroupExists(name);
        }

        var names = new HashSet<string>(database.Collation);
        var dataFiles = groups.Where(group => !group.ContainsSpecialData).SelectMany(group => group.Files).ToList();
        foreach (var spec in groups.SelectMany(group => group.Files).Concat(logFiles))
        {
            if (!names.Add(spec.Name!))
                throw SimulatedSqlException.LogicalFileNameInUse(spec.Name!, 5);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primary = dataFiles[0];
        foreach (var spec in dataFiles.Concat(logFiles))
        {
            if (CreatedFileRefusal(spec, spec.SizePages ?? BuiltInResources.NewFileSizePages, isPrimary: spec == primary) is { } refusal)
                throw SimulatedSqlException.FollowedByCreateDatabaseFailed(refusal, 1);
            if (!paths.Add(spec.FileName!) || PhysicalPathInUse(this, spec.FileName!))
                throw TakenPathRefusal(batch, spec.FileName!);
        }

        database.Files.Clear();
        foreach (var group in groups)
        {
            var dataSpaceId = group.Name is null ? Database.PrimaryFilegroupId : database.RegisterFilegroup(group.Name);
            if (group.IsDefault)
                database.DefaultFilegroupId = dataSpaceId;
            if (group.MemoryOptimized)
            {
                RejectContainerSizes(group.Files);
                database.MemoryOptimizedFilegroupId = dataSpaceId;
                foreach (var spec in group.Files)
                    database.Files.Add(NewContainer(database, spec, dataSpaceId));
                continue;
            }
            if (group.ContainsSpecialData)
                continue;
            foreach (var spec in group.Files)
            {
                var isPrimary = spec == primary;
                var size = spec.SizePages ?? BuiltInResources.NewFileSizePages;
                if (isPrimary)
                    size = Math.Max(size, BuiltInResources.NewFileSizePages);
                var file = NewFile(isPrimary ? 1 : 0, isLog: false, spec, dataSpaceId, size);
                // The floor lifts a ceiling it passes (probed 2026-09-27).
                if (file.MaxSizePages != -1 && file.MaxSizePages < size)
                    file.MaxSizePages = size;
                database.Files.Add(file);
            }
        }
        if (logFiles.Count == 0)
        {
            database.Files.Add(new(2, isLog: true, BuiltInResources.LogicalFileName(database.Name, isLog: true), BuiltInResources.LogFilePath(database.Name), 0,
                BuiltInResources.NewFileSizePages, maxSizePages: -1, BuiltInResources.FileGrowthPages, isPercentGrowth: false));
        }
        for (var i = 0; i < logFiles.Count; i++)
            database.Files.Add(NewFile(i == 0 ? 2 : 0, isLog: true, logFiles[i], 0, logFiles[i].SizePages ?? BuiltInResources.NewFileSizePages));

        // The ids past the two primary files go to the other data files, then
        // the other log files, in declaration order.
        var ordered = database.Files.OrderBy(file => file.FileId == 0 ? 1 : 0).ThenBy(file => file.IsLog ? 1 : 0).ToList();
        database.Files.Clear();
        var nextId = 3;
        foreach (var file in ordered)
        {
            database.Files.Add(file.FileId != 0 || file.IsContainer ? file
                : new(nextId++, file.IsLog, file.Name, file.PhysicalName, file.DataSpaceId, file.SizePages, file.MaxSizePages, file.Growth, file.IsPercentGrowth));
        }
    }

    /// <summary>
    /// A database made without a file list takes the default paths, which a
    /// renamed database's files may still hold: Msg 5170, then Msg 1802.
    /// </summary>
    private void RejectTakenPaths(BatchContext batch, Database database)
    {
        foreach (var file in database.FilesInOrder())
        {
            if (PhysicalPathInUse(this, file.PhysicalName))
                throw TakenPathRefusal(batch, file.PhysicalName);
        }
    }

    /// <summary>
    /// Msg 5170 then Msg 1802 for a new database's file on a taken path,
    /// which writes Msg 5170 to the error log at state 1 and then at state 4
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException TakenPathRefusal(BatchContext batch, string path)
    {
        var message = SimulatedSqlException.PhysicalFileExists(path, 4).Message;
        LogRaisedError(batch, 5170, 16, 1, message);
        LogRaisedError(batch, 5170, 16, 4, message);
        return SimulatedSqlException.FollowedByCreateDatabaseFailed(SimulatedSqlException.PhysicalFileExists(path, 4), 4);
    }
}
