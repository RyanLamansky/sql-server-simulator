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
    /// Parses <c>CREATE DATABASE &lt;name&gt; [ON [PRIMARY] &lt;filespec&gt; [, …]
    /// [, FILEGROUP name [CONTAINS …] [DEFAULT] &lt;filespec&gt; [, …]] [LOG ON
    /// &lt;filespec&gt; [, …]]] [COLLATE &lt;collation&gt;] [&lt;option clauses&gt;]</c>.
    /// The database name is a single identifier (bare or bracketed / quoted);
    /// an optional <c>COLLATE</c> clause sets the new database's collation
    /// (defaulting to the server collation, mirroring <c>model.collation</c>),
    /// and every other clause (<c>WITH …</c>, <c>CONTAINMENT = …</c>,
    /// <c>FOR ATTACH</c>, …) is parse-and-discarded. The file list becomes
    /// the database's <see cref="Database.Files"/> and filegroups; without
    /// one the database gets the <c>&lt;db&gt;</c> / <c>&lt;db&gt;_log</c>
    /// pair. The new database is registered with the smallest free
    /// <c>database_id</c> ≥ 5 (see <see cref="RegisterUserDatabase"/>). Cursor
    /// on entry is the
    /// <c>DATABASE</c> keyword.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025: a duplicate name raises
    /// <strong>Msg 1801</strong> (Class 16, State 3). <c>COLLATE</c> is
    /// detected anywhere in the trailing-clause scan. The file list (probed
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

        // Walk the trailing clauses: parse the file list, capture a COLLATE
        // clause anywhere at the top level, discard everything else (tracking
        // nested parens) until a statement boundary. WITH is a boundary keyword
        // but legitimately continues CREATE DATABASE, so it's consumed rather
        // than stopping the scan.
        string? collationName = null;
        List<DeclaredFilegroup>? groups = null;
        List<FileSpecification>? logFiles = null;
        var depth = 0;
        var seenWith = false;
        context.MoveNextOptional();
        while (context.Token is { } token)
        {
            if (depth == 0 && !seenWith && groups is null && token is ReservedKeyword { Keyword: Keyword.On })
            {
                groups = ParseCreateDatabaseDataFiles(context);
                if (IsLogOn(context))
                    logFiles = ParseCreateDatabaseLogFiles(context);
                continue;
            }
            if (depth == 0 && !seenWith && groups is null && IsLogOn(context))
                throw SimulatedSqlException.LogFileWithoutDataFile();
            if (depth == 0 && token is ReservedKeyword { Keyword: Keyword.Collate })
            {
                if (context.GetNextRequired() is not UnquotedString collationToken)
                    return false;
                collationName = collationToken.Value;
                context.MoveNextOptional();
                continue;
            }
            if (depth == 0 && IsStatementBoundary(token) && token is not ReservedKeyword { Keyword: Keyword.With })
                break;
            switch (token)
            {
                case ReservedKeyword { Keyword: Keyword.With } when depth == 0:
                    seenWith = true;
                    break;
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' } when depth > 0:
                    depth--;
                    break;
            }
            context.MoveNextOptional();
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

        var collation = collationName is null
            ? this.ServerCollation
            : Collation.TryGet(collationName)
                ?? throw new NotSupportedException($"CREATE DATABASE COLLATE: collation '{collationName}' isn't on the simulator's recognized list.");

        lock (this.Databases)
        {
            if (this.Databases.ContainsKey(databaseName))
                throw SimulatedSqlException.DatabaseAlreadyExists(databaseName);
            var database = new Database(databaseName, collation);
            // The creating login owns the new database (probed 2026-09-27
            // against SQL Server 2025 with a dbcreator login); a session with no
            // registered login — the in-process default — creates as sa.
            if (this.Logins.TryGetValue(context.Connection.Security.Effective.LoginName, out var creator))
                database.OwnerLoginName = creator.Name;
            if (groups is not null)
                this.ApplyCreateDatabaseFileList(database, groups, logFiles ?? []);
            else
                this.RejectTakenPaths(database);
            RegisterUserDatabaseLocked(database);
        }
        RecordServerDdlEvent(context, "CREATE_DATABASE", databaseName, loginName: null);
        return true;
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
    private void ApplyCreateDatabaseFileList(Database database, List<DeclaredFilegroup> groups, List<FileSpecification> logFiles)
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
                throw SimulatedSqlException.FollowedByCreateDatabaseFailed(SimulatedSqlException.PhysicalFileExists(spec.FileName!, 4), 4);
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
    private void RejectTakenPaths(Database database)
    {
        foreach (var file in database.FilesInOrder())
        {
            if (PhysicalPathInUse(this, file.PhysicalName))
                throw SimulatedSqlException.FollowedByCreateDatabaseFailed(SimulatedSqlException.PhysicalFileExists(file.PhysicalName, 4), 4);
        }
    }
}
