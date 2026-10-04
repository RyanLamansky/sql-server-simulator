using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// One parenthesized file specification — <c>(NAME = …, FILENAME = …,
    /// SIZE = …, MAXSIZE = …, FILEGROWTH = …)</c> — as written, sizes already
    /// in 8 KB pages rounded up to a whole 64 KB extent.
    /// </summary>
    private sealed class FileSpecification
    {
        public string? Name;
        public string? NewName;
        public string? FileName;
        public int? SizePages;

        /// <summary>The ceiling in pages, -1 for <c>UNLIMITED</c>.</summary>
        public int? MaxSizePages;

        public int? Growth;
        public bool GrowthIsPercent;
        public bool Offline;

        /// <summary>Whether anything besides <c>NAME</c> was written.</summary>
        public bool HasProperty =>
            this.NewName is not null || this.FileName is not null || this.SizePages is not null
            || this.MaxSizePages is not null || this.Growth is not null || this.Offline;
    }

    /// <summary>Where a file specification appears, which decides the options it takes and the states its refusals carry.</summary>
    private enum FileSpecificationSite : byte
    {
        CreateDatabase,
        AddFile,
        ModifyFile,
    }

    /// <summary>
    /// Parses one file specification, entered with the cursor on its
    /// <c>(</c> and leaving it past the <c>)</c>. Everything checked here real
    /// checks compiling the batch (probed 2026-09-27 against SQL Server 2025):
    /// an option the site doesn't take, a repeated one, or an unknown unit is
    /// Msg 153 naming it as written; a size past <c>int</c> pages is Msg 1842;
    /// a missing <c>FILENAME</c> where a file is created is Msg 1036 at class
    /// 15, then a missing <c>NAME</c> Msg 1036 at class 16.
    /// </summary>
    private static FileSpecification ParseFileSpecification(ParserContext context, FileSpecificationSite site)
    {
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var spec = new FileSpecification();
        // Long enough for the longest option name; a longer word is no option.
        Span<char> optionBuffer = stackalloc char[16];
        while (true)
        {
            var optionToken = context.GetNextRequired();
            if (optionToken is not Name { Value: var option })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (BuiltInToken.Equals(option, "OFFLINE"))
            {
                if (site != FileSpecificationSite.ModifyFile || spec.Offline)
                    throw SimulatedSqlException.InvalidFileOptionUsage(option);
                spec.Offline = true;
                context.MoveNextRequired();
            }
            else
            {
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var valueToken = context.GetNextRequired();
                if (option.Length > optionBuffer.Length)
                    throw SimulatedSqlException.InvalidFileOptionUsage(option);
                var upper = optionBuffer[..option.Length];
                _ = option.AsSpan().ToUpperInvariant(upper);
                switch (upper)
                {
                    case "FILEGROWTH" when spec.Growth is null:
                        (spec.Growth, spec.GrowthIsPercent) = ParseFileSize(context, allowPercent: true, allowUnlimited: false);
                        break;
                    case "FILENAME" when spec.FileName is null:
                        spec.FileName = valueToken is Literal { Value: { IsNull: false, Type.Category: SqlTypeCategory.String } path }
                            ? path.AsString
                            : throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextRequired();
                        break;
                    case "MAXSIZE" when spec.MaxSizePages is null:
                        spec.MaxSizePages = ParseFileSize(context, allowPercent: false, allowUnlimited: true).Value;
                        break;
                    case "NAME" when spec.Name is null:
                        spec.Name = ParseFileName(context);
                        break;
                    case "NEWNAME" when spec.NewName is null && site == FileSpecificationSite.ModifyFile:
                        spec.NewName = ParseFileName(context);
                        break;
                    case "SIZE" when spec.SizePages is null:
                        spec.SizePages = ParseFileSize(context, allowPercent: false, allowUnlimited: false).Value;
                        break;
                    default:
                        throw SimulatedSqlException.InvalidFileOptionUsage(option);
                }
            }

            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();

        if (site != FileSpecificationSite.ModifyFile && spec.FileName is null)
            throw SimulatedSqlException.FileOptionRequired("FILENAME", 15, 1);
        return spec.Name is null
            ? throw SimulatedSqlException.FileOptionRequired("NAME", 16, site == FileSpecificationSite.CreateDatabase ? (byte)2 : (byte)3)
            : spec;
    }

    /// <summary>A logical file name — an identifier or a string literal — leaving the cursor past it.</summary>
    private static string ParseFileName(ParserContext context)
    {
        var name = context.Token switch
        {
            Name named => named.Value,
            Literal { Value: { IsNull: false, Type.Category: SqlTypeCategory.String } literal } => literal.AsString,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextRequired();
        return name;
    }

    /// <summary>
    /// A size, ceiling or growth, entered on its number and leaving the cursor
    /// past its unit: an integer literal in <c>KB</c> / <c>MB</c> (the default)
    /// / <c>GB</c> / <c>TB</c>, rounded up to a whole 64 KB extent and
    /// returned in pages; with <paramref name="allowPercent"/> a percentage;
    /// with <paramref name="allowUnlimited"/> <c>UNLIMITED</c>, returned as -1.
    /// </summary>
    private static (int Value, bool IsPercent) ParseFileSize(ParserContext context, bool allowPercent, bool allowUnlimited)
    {
        if (allowUnlimited && context.Token is Name { Value: var unlimited } && BuiltInToken.Equals(unlimited, "UNLIMITED"))
        {
            context.MoveNextRequired();
            return (-1, false);
        }
        if (context.Token is not Numeric { Value: var number } || number.Type != SqlType.Int32)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        long amount = number.AsInt32;

        long kilobytes;
        switch (context.GetNextRequired())
        {
            case Operator { Character: '%' }:
                if (!allowPercent)
                    throw SimulatedSqlException.InvalidFileOptionUsage("percent");
                context.MoveNextRequired();
                return ((int)amount, true);
            case Name { Value: var unit }:
                Span<char> upper = stackalloc char[unit.Length];
                _ = unit.AsSpan().ToUpperInvariant(upper);
                kilobytes = upper switch
                {
                    "GB" => amount * 1024 * 1024,
                    "KB" => amount,
                    "MB" => amount * 1024,
                    "TB" => amount * 1024 * 1024 * 1024,
                    _ => throw SimulatedSqlException.InvalidFileOptionUsage(unit),
                };
                context.MoveNextRequired();
                break;
            default:
                kilobytes = amount * 1024;
                break;
        }
        var pages = (kilobytes + 63) / 64 * 8;
        return pages > int.MaxValue ? throw SimulatedSqlException.FileSizeTooLarge() : ((int)pages, false);
    }

    /// <summary>The lowest <c>file_id</c> of 3 or more no file of <paramref name="database"/> holds — a removed file's id comes back (probed 2026-09-27).</summary>
    private static int NextFileId(Database database)
    {
        var id = 3;
        while (database.FindFile(id) is not null)
            id++;
        return id;
    }

    /// <summary>Whether any file of any database this simulation hosts sits at <paramref name="path"/>.</summary>
    private static bool PhysicalPathInUse(Simulation simulation, string path, DatabaseFile? except = null)
    {
        foreach (var (_, database) in simulation.Databases)
        {
            foreach (var file in database.FilesInOrder())
            {
                if (file != except && file.PhysicalName.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The creation-time refusals of a new file, checked in real's order
    /// against the size as written: a size under 512 KB (Msg 5174) — save for
    /// a new database's primary file, which <c>model</c>'s size floors — a
    /// ceiling under the size (Msg 5103), a growth past the ceiling (Msg 5169,
    /// state 2). Null when the file may be made.
    /// </summary>
    private static SimulatedSqlException? CreatedFileRefusal(FileSpecification spec, int sizePages, bool isPrimary = false)
    {
        if (sizePages < 64 && !isPrimary)
            return SimulatedSqlException.FileTooSmall();
        if (spec.MaxSizePages is int max and not -1)
        {
            if (max < sizePages)
                return SimulatedSqlException.MaxSizeBelowSize(spec.Name!);
            if (!spec.GrowthIsPercent && spec.Growth > max)
                return SimulatedSqlException.GrowthAboveMaxSize(spec.Name!, 2);
        }
        return null;
    }

    /// <summary>
    /// A container of the <c>MEMORY_OPTIMIZED_DATA</c> filegroup
    /// <paramref name="dataSpaceId"/>: the lowest free id from 65537, with no
    /// size, ceiling or growth (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static DatabaseFile NewContainer(Database database, FileSpecification spec, int dataSpaceId)
    {
        var id = 65537;
        while (database.FindFile(id) is not null)
            id++;
        return new(id, isLog: false, spec.Name!, spec.FileName!, dataSpaceId, sizePages: 0, maxSizePages: -1, growth: 0, isPercentGrowth: false, isContainer: true);
    }

    /// <summary>
    /// Refuses a container specification giving a size or growth (Msg 5509)
    /// or a ceiling other than <c>UNLIMITED</c> (Msg 41873, then Msg 5009),
    /// which a <c>MEMORY_OPTIMIZED_DATA</c> container can't have (probed
    /// 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static void RejectContainerSizes(List<FileSpecification> specs)
    {
        foreach (var spec in specs)
        {
            if (spec.SizePages is not null || spec.Growth is not null)
                throw SimulatedSqlException.FilestreamFileSizeOption(spec.Name!);
            if (spec.MaxSizePages is not (null or -1))
                throw SimulatedSqlException.FollowedByFilesNotInitialized(SimulatedSqlException.MemoryOptimizedContainerMaxSize());
        }
    }

    /// <summary>The file <paramref name="spec"/> describes, with the defaults a file written without <c>SIZE</c> / <c>MAXSIZE</c> / <c>FILEGROWTH</c> takes.</summary>
    private static DatabaseFile NewFile(int fileId, bool isLog, FileSpecification spec, int dataSpaceId, int sizePages) =>
        new(fileId, isLog, spec.Name!, spec.FileName!, isLog ? 0 : dataSpaceId, sizePages,
            spec.MaxSizePages ?? -1, spec.Growth ?? BuiltInResources.FileGrowthPages, spec.GrowthIsPercent);

    /// <summary>
    /// <c>ALTER DATABASE … ADD [LOG] FILE &lt;spec&gt; [, …] [TO FILEGROUP
    /// name]</c>, entered on the word after <c>ADD</c> — <c>FILE</c>, or
    /// <c>LOG</c> already stepped past. The files go to the named filegroup,
    /// else <c>PRIMARY</c> — not the default filegroup; the whole list is
    /// refused together. Real's
    /// refusals, in its order: a read-only database (Msg 5004 state 1), an
    /// unknown filegroup (Msg 1921), a log file with a filegroup other than
    /// <c>PRIMARY</c> (Msg 5087), a
    /// read-only filegroup (Msg 5048), then per file a taken logical name
    /// (Msg 1828, state 4 — 9 when the list repeats it), a creation refusal
    /// followed by Msg 5009, and a taken path (Msg 5009 then Msg 5170).
    /// </summary>
    private static bool TryParseAlterDatabaseAddFile(ParserContext context, Database target, bool isLog)
    {
        var specs = new List<FileSpecification>();
        context.MoveNextRequired();
        while (true)
        {
            specs.Add(ParseFileSpecification(context, FileSpecificationSite.AddFile));
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        string? filegroupName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.To })
        {
            if (context.GetNextRequired() is not Name { Value: var toWord } || !BuiltInToken.Equals(toWord, "FILEGROUP"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            filegroupName = context.GetNextRequired() switch
            {
                Name named => named.Value,
                ReservedKeyword { Keyword: Keyword.Default } => null,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextOptional();
        }
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;

        if (target.IsReadOnly)
            throw SimulatedSqlException.DatabaseNotWritableForFileChange(1);
        // Without TO FILEGROUP a file joins PRIMARY, whichever filegroup is the
        // default (probed 2026-09-27).
        var dataSpaceId = Database.PrimaryFilegroupId;
        if (filegroupName is not null)
        {
            if (!target.Filegroups.TryGetValue(filegroupName, out dataSpaceId))
                throw SimulatedSqlException.InvalidFilegroup(filegroupName);
            // A log file may name PRIMARY, which it then ignores.
            if (isLog && dataSpaceId != Database.PrimaryFilegroupId)
                throw SimulatedSqlException.FileContentTypeMismatch();
        }
        if (!isLog && target.IsFilegroupReadOnly(dataSpaceId))
            throw SimulatedSqlException.FilegroupIsReadOnly(FilegroupNameOf(target, dataSpaceId), 1);
        var container = !isLog && dataSpaceId == target.MemoryOptimizedFilegroupId;
        if (container)
            RejectContainerSizes(specs);

        var simulation = context.Connection.Simulation;
        lock (simulation.Databases)
        {
            for (var i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                if (target.FindFile(spec.Name!) is not null)
                    throw SimulatedSqlException.LogicalFileNameInUse(spec.Name!, 4);
                for (var j = 0; j < i; j++)
                {
                    if (target.Collation.Equals(specs[j].Name!, spec.Name!))
                        throw SimulatedSqlException.LogicalFileNameInUse(spec.Name!, 9);
                }
                if (!container && CreatedFileRefusal(spec, spec.SizePages ?? BuiltInResources.NewFileSizePages) is { } refusal)
                    throw SimulatedSqlException.FollowedByFilesNotInitialized(refusal);
                if (PhysicalPathInUse(simulation, spec.FileName!))
                    throw SimulatedSqlException.AddFilePathExists(spec.FileName!);
            }
            lock (target.Files)
            {
                foreach (var spec in specs)
                    target.Files.Add(container ? NewContainer(target, spec, dataSpaceId) : NewFile(NextFileId(target), isLog, spec, dataSpaceId, spec.SizePages ?? BuiltInResources.NewFileSizePages));
            }
        }
        return true;
    }

    /// <summary>
    /// <c>ALTER DATABASE … REMOVE FILE name</c>, entered on <c>FILE</c>, with
    /// real's refusals in its order: a read-only database (Msg 5004 state 3),
    /// no such file (Msg 5009 state 9), the primary data or log file
    /// (Msg 5020), a read-only filegroup's file (Msg 5055), the default
    /// filegroup's only file (Msg 5031). Every file but the primary data file
    /// is empty here, so real's Msg 5042 for a file holding data never arises.
    /// Reports the class-0 Msg 5044.
    /// </summary>
    private static bool TryParseAlterDatabaseRemoveFile(ParserContext context, Database target)
    {
        var name = context.GetNextRequired() switch
        {
            Name named => named.Value,
            Literal { Value: { IsNull: false, Type.Category: SqlTypeCategory.String } literal } => literal.AsString,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextOptional();
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;

        if (target.IsReadOnly)
            throw SimulatedSqlException.DatabaseNotWritableForFileChange(3);
        var file = target.FindFile(name) ?? throw SimulatedSqlException.FilesNotInitialized(9);
        if (file.IsPrimary)
            throw SimulatedSqlException.CannotRemovePrimaryFile();
        if (!file.IsLog)
        {
            if (target.IsFilegroupReadOnly(file.DataSpaceId))
                throw SimulatedSqlException.FileIsReadOnly(file.Name);
            if (file.DataSpaceId == target.DefaultFilegroupId && FileCount(target, file.DataSpaceId) == 1)
                throw SimulatedSqlException.CannotRemoveOnlyDefaultFile(file.Name);
            // Rows aren't kept per file, so any row on the filegroup counts
            // as the file's.
            if (file.DataSpaceId != Database.PrimaryFilegroupId && FilegroupHoldsObjects(target, file.DataSpaceId, withRowsOnly: true))
                throw SimulatedSqlException.FileNotEmpty(file.Name);
        }
        lock (target.Files)
            _ = target.Files.Remove(file);
        context.Batch.AppendInfoError(0, 1, 5044, $"The file '{file.Name}' has been removed.");
        return true;
    }

    /// <summary>How many data files the filegroup <paramref name="dataSpaceId"/> holds.</summary>
    internal static int FileCount(Database database, int dataSpaceId)
    {
        var count = 0;
        foreach (var file in database.FilesInOrder())
        {
            if (!file.IsLog && !file.IsContainer && file.DataSpaceId == dataSpaceId)
                count++;
        }
        return count;
    }

    /// <summary>The name <paramref name="database"/> knows the filegroup <paramref name="dataSpaceId"/> by.</summary>
    private static string FilegroupNameOf(Database database, int dataSpaceId) =>
        database.Filegroups.First(entry => entry.Value == dataSpaceId).Key;

    /// <summary>
    /// <c>ALTER DATABASE … MODIFY FILE &lt;spec&gt;</c>, entered on <c>FILE</c>;
    /// one specification only. Real's refusals, in its order (probed
    /// 2026-09-27 against SQL Server 2025): a read-only database (Msg 5004
    /// state 4), no such file (Msg 5041), a read-only filegroup (Msg 5048
    /// state 3), nothing to change (Msg 5038), <c>OFFLINE</c> on a log or
    /// <c>PRIMARY</c> file (Msg 5077), a taken new name — the file's own
    /// included (Msg 1828 state 3), a taken path (Msg 12106), a ceiling under
    /// the current size (Msg 5040) or under the current growth (Msg 5169
    /// state 3), a growth past the ceiling (Msg 5169 state 1), a size no
    /// larger than the current one (Msg 5039). A larger size lifts a ceiling it
    /// passes. Nothing changes unless everything passes; a new path reports
    /// the class-0 Msg 5018 and a new name Msg 5021, in that order.
    /// </summary>
    private static bool TryParseAlterDatabaseModifyFile(ParserContext context, Database target)
    {
        context.MoveNextRequired();
        var spec = ParseFileSpecification(context, FileSpecificationSite.ModifyFile);
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;

        if (target.IsReadOnly)
            throw SimulatedSqlException.DatabaseNotWritableForFileChange(4);
        var file = target.FindFile(spec.Name!) ?? throw SimulatedSqlException.ModifyFileDoesNotExist(spec.Name!);
        if (!file.IsLog && target.IsFilegroupReadOnly(file.DataSpaceId))
            throw SimulatedSqlException.FilegroupIsReadOnly(FilegroupNameOf(target, file.DataSpaceId), 3);
        if (!spec.HasProperty)
            throw SimulatedSqlException.ModifyFileNeedsProperty(file.Name);
        if (spec.Offline)
        {
            if (file.IsLog || file.DataSpaceId == Database.PrimaryFilegroupId)
                throw SimulatedSqlException.CannotTakeFileOffline();
            throw new NotSupportedException("ALTER DATABASE … MODIFY FILE … OFFLINE isn't modeled.");
        }
        if (spec.NewName is { } newName && target.FindFile(newName) is not null)
            throw SimulatedSqlException.LogicalFileNameInUse(newName, 3);
        var simulation = context.Connection.Simulation;
        lock (simulation.Databases)
        {
            if (spec.FileName is { } path && PhysicalPathInUse(simulation, path, except: file))
                throw SimulatedSqlException.PathInUse(path);

            var size = BuiltInResources.FileSizePages(target, file);
            var maxSize = file.MaxSizePages;
            if (spec.MaxSizePages is int newMax)
            {
                if (newMax != -1 && newMax < size)
                    throw SimulatedSqlException.ModifyFileMaxSizeBelowSize(target.Name, file.FileId, size * 8L, newMax * 8L);
                if (newMax != -1 && !file.IsPercentGrowth && file.Growth > newMax)
                    throw SimulatedSqlException.GrowthAboveMaxSize(file.Name, 3);
                maxSize = newMax;
            }
            if (spec.Growth is int growth && !spec.GrowthIsPercent && maxSize != -1 && growth > maxSize)
                throw SimulatedSqlException.GrowthAboveMaxSize(file.Name, 1);
            if (spec.SizePages is int newSize && newSize <= size)
                throw SimulatedSqlException.ModifyFileSizeNotLarger();

            var oldName = file.Name;
            lock (target.Files)
            {
                if (spec.SizePages is int grown)
                {
                    file.SizePages = grown;
                    if (maxSize != -1 && grown > maxSize)
                        maxSize = grown;
                }
                file.MaxSizePages = maxSize;
                if (spec.Growth is int newGrowth)
                {
                    file.Growth = newGrowth;
                    file.IsPercentGrowth = spec.GrowthIsPercent;
                }
                if (spec.FileName is { } newPath)
                    file.PhysicalName = newPath;
                if (spec.NewName is { } renamed)
                    file.Name = renamed;
            }
            if (spec.FileName is not null)
                context.Batch.AppendInfoError(0, 1, 5018, SimulatedSqlException.FileModifiedInCatalogMessage(oldName));
            if (spec.NewName is { } reportedName)
                context.Batch.AppendInfoError(0, 1, 5021, $"The file name '{reportedName}' has been set.");
        }
        return true;
    }

    /// <summary>
    /// <c>ALTER DATABASE … MODIFY FILEGROUP name { DEFAULT | READ_ONLY |
    /// READONLY | READ_WRITE | READWRITE | AUTOGROW_ALL_FILES |
    /// AUTOGROW_SINGLE_FILE | NAME = new }</c>, entered on <c>FILEGROUP</c>.
    /// Real's refusals (probed 2026-09-27 against SQL Server 2025): a
    /// read-only database (plain Msg 3906), no such filegroup (Msg 5014 state
    /// 2), renaming <c>PRIMARY</c> (Msg 5012) or onto another data space's
    /// name (Msg 5035 state 2), marking <c>PRIMARY</c> read-only or read-write
    /// (Msg 5047), a property change on a filegroup without files (Msg 5050), a
    /// property it already has (Msg 5045). Reports the class-0 Msg 5046 for a
    /// property, Msg 5021 for a name.
    /// </summary>
    private static bool TryParseAlterDatabaseModifyFilegroup(ParserContext context, Database target)
    {
        var name = context.GetNextRequired() switch
        {
            Name named => named.Value,
            ReservedKeyword { Keyword: Keyword.Primary } => "PRIMARY",
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        string property;
        string? newName = null;
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Default }:
                property = "DEFAULT";
                break;
            case Name { Value: var word } when BuiltInToken.Equals(word, "NAME"):
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                newName = context.GetNextRequired() is Name renamed ? renamed.Value : throw SimulatedSqlException.SyntaxErrorNear(context);
                property = "NAME";
                break;
            case Name { Value: var word }:
                Span<char> upper = stackalloc char[word.Length];
                _ = word.AsSpan().ToUpperInvariant(upper);
                property = upper switch
                {
                    "AUTOGROW_ALL_FILES" => "AUTOGROW_ALL_FILES",
                    "AUTOGROW_SINGLE_FILE" => "AUTOGROW_SINGLE_FILE",
                    "READ_ONLY" or "READONLY" => "READ_ONLY",
                    "READ_WRITE" or "READWRITE" => "READ_WRITE",
                    _ => throw SimulatedSqlException.UnknownFilegroupProperty(word),
                };
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;

        target.RejectWriteWhenReadOnly();
        if (!target.Filegroups.TryGetValue(name, out var dataSpaceId))
            throw SimulatedSqlException.ModifyFilegroupDoesNotExist(name, target.Name);
        var currentName = FilegroupNameOf(target, dataSpaceId);
        var batch = context.Batch;
        if (newName is not null)
        {
            if (dataSpaceId == Database.PrimaryFilegroupId)
                throw SimulatedSqlException.CannotRenamePrimaryFilegroup();
            lock (target.Filegroups)
            {
                if (target.Filegroups.TryGetValue(newName, out var holder) ? holder != dataSpaceId : target.PartitionSchemes.ContainsKey(newName))
                    throw SimulatedSqlException.RenamedFilegroupExists(newName);
                _ = target.Filegroups.TryRemove(currentName, out _);
                target.Filegroups[newName] = dataSpaceId;
            }
            batch.AppendInfoError(0, 3, 5021, $"The filegroup name '{newName}' has been set.");
            return true;
        }

        if (property is "READ_ONLY" or "READ_WRITE" && dataSpaceId == Database.PrimaryFilegroupId)
            throw SimulatedSqlException.PrimaryFilegroupReadOnlyChange();
        // The MEMORY_OPTIMIZED_DATA filegroup already reports itself default,
        // and its access mode is fixed (probed 2026-10-02 against SQL Server
        // 2025).
        if (dataSpaceId == target.MemoryOptimizedFilegroupId)
        {
            throw property is "READ_ONLY" or "READ_WRITE"
                ? SimulatedSqlException.MemoryOptimizedFilegroupAccessMode()
                : SimulatedSqlException.FilegroupPropertyAlreadySet(property, 3);
        }
        if (FileCount(target, dataSpaceId) == 0)
            throw SimulatedSqlException.EmptyFilegroupProperty(currentName);
        lock (target.Filegroups)
        {
            var changed = property switch
            {
                "AUTOGROW_ALL_FILES" => target.AutogrowAllFilesFilegroups.Add(dataSpaceId),
                "AUTOGROW_SINGLE_FILE" => target.AutogrowAllFilesFilegroups.Remove(dataSpaceId),
                "DEFAULT" => SetDefaultFilegroup(target, dataSpaceId),
                "READ_ONLY" => target.ReadOnlyFilegroups.Add(dataSpaceId),
                _ => target.ReadOnlyFilegroups.Remove(dataSpaceId),
            };
            if (!changed)
            {
                throw SimulatedSqlException.FilegroupPropertyAlreadySet(property, property switch
                {
                    "AUTOGROW_SINGLE_FILE" => 4,
                    "READ_ONLY" => 1,
                    "READ_WRITE" => 2,
                    _ => 3,
                });
            }
        }
        batch.AppendInfoError(0, 1, 5046, $"The filegroup property '{property}' has been set.");
        return true;
    }

    private static bool SetDefaultFilegroup(Database database, int dataSpaceId)
    {
        if (database.DefaultFilegroupId == dataSpaceId)
            return false;
        database.DefaultFilegroupId = dataSpaceId;
        return true;
    }
}
