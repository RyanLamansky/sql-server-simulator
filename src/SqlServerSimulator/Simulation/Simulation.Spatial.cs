using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE SPATIAL INDEX name ON table(col) [USING <i>scheme</i>]
    /// [WITH (BOUNDING_BOX = (…) | GRIDS = (…) | CELLS_PER_OBJECT = n |
    /// <i>relational option</i> [, …])] [ON filegroup]</c>. Cursor on entry is
    /// the <c>SPATIAL</c> contextual keyword. See <see cref="SpatialIndex"/>
    /// for the no-enforcement rationale.
    /// </summary>
    /// <remarks>
    /// Real's checks, probed 2026-10-05 against SQL Server 2025, fall in four
    /// tiers that this keeps apart. The option grammar raises its severity-15
    /// errors while the batch compiles (Msg 153 / 155 / 129, a Msg 102 for a
    /// malformed list). Running, the statement resolves the table (Msg 6334
    /// for a view), the column (Msg 1911, 12002, 6342 for a computed one) and
    /// the table's clustered primary key (Msg 12008, 12016), then the name
    /// (Msg 1913) and the session's SET options (Msg 1934); only then are the
    /// tessellation parameters judged — the scheme against the column's type
    /// (Msg 12003), the bounding box geometry needs and geography refuses
    /// (Msg 12007 / 12005), the grids an AUTO scheme refuses (Msg 12005), a
    /// part list left incomplete (Msg 12014), an inverted box (Msg 12013),
    /// and <c>CELLS_PER_OBJECT</c>'s range (Msg 12012 / 12011). GRIDS without
    /// a USING picks the plain GRID scheme; an omitted level is MEDIUM; an AUTO
    /// scheme records no levels; <c>CELLS_PER_OBJECT</c> defaults to 8 for
    /// <c>GEOMETRY_AUTO_GRID</c>, 12 for <c>GEOGRAPHY_AUTO_GRID</c> and 16 for
    /// either GRID.
    /// </remarks>
    internal static bool TryParseCreateSpatial(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Index })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Name indexNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var indexName = indexNameToken.Value;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var targetTableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name colNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnName = colNameToken.Value;
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        string? writtenScheme = null;
        context.MoveNextOptional();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Using })
        {
            if (context.GetNextRequired() is not Name schemeToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            writtenScheme = schemeToken.Value;
            context.MoveNextOptional();
        }

        var options = new SpatialIndexOptions();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ParseSpatialIndexOptions(context, options);
            context.MoveNextOptional();
        }

        // ON filegroup — parse-and-discard, as every data space is PRIMARY.
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            if (context.GetNextRequired() is not (Name or ReservedKeyword { Keyword: Keyword.Default }))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        // A spatial index takes no filter, INCLUDE list or trailing USING.
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Where } where:
                throw SimulatedSqlException.SyntaxErrorNearKeyword(where);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Using }:
                throw SimulatedSqlException.SyntaxErrorNear(context);
            case Name include when include.Value.Equals("INCLUDE", StringComparison.OrdinalIgnoreCase):
                var beforeInclude = context.SaveCheckpoint();
                if (context.MoveNext() && context.Token is Operator { Character: '(' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.RestoreCheckpoint(beforeInclude);
                break;
        }

        if (context.Batch.IsSkipping)
            return true;

        var written = targetTableName.ToString();
        if (!context.Batch.TryResolveTable(targetTableName, out var table))
        {
            throw context.Batch.TryResolveView(targetTableName, out _)
                ? SimulatedSqlException.XmlOrSpatialIndexOnNonTable(written)
                : SimulatedSqlException.CannotFindObjectForCreateIndex(written, state: 202);
        }

        var colOrdinal = -1;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, columnName))
            {
                colOrdinal = i;
                break;
            }
        }
        if (colOrdinal < 0)
            throw SimulatedSqlException.IndexColumnMissing(columnName, state: 103);

        var col = table.Columns[colOrdinal];
        var kind = col.Type == SqlType.Geography ? SpatialIndexKind.Geography
            : col.Type == SqlType.Geometry ? SpatialIndexKind.Geometry
            : throw SimulatedSqlException.SpatialIndexRequiresSpatialColumn(columnName, written);
        if (col.Computed is not null && !col.IsPersisted)
            throw SimulatedSqlException.XmlOrSpatialIndexOnComputedColumn(indexName, written, col.Name);

        // The table's clustered primary key keys the index's rows: up to 31
        // columns of at most 895 bytes together.
        var primaryKey = table.KeyConstraints.Find(key => key.Kind == KeyConstraintKind.PrimaryKey);
        if (primaryKey is not { IsClustered: true })
            throw SimulatedSqlException.SpatialIndexNeedsClusteredPrimaryKey(written);
        var keyBytes = 0;
        foreach (var ordinal in primaryKey.FullOrdinals)
            keyBytes += Math.Max(0, (int)BuiltInResources.GetSysColumnMetadata(table.Columns[ordinal]).MaxLength);
        if (primaryKey.FullOrdinals.Length > 31 || keyBytes > 895)
            throw SimulatedSqlException.SpatialIndexPrimaryKeyTooWide(written, primaryKey.FullOrdinals.Length, keyBytes);

        var collation = context.Batch.CurrentDatabase.Collation;
        var replaced = table.SpatialIndexes.Find(existing => collation.Equals(existing.Name, indexName));
        if ((replaced is not null && !options.DropExisting)
            || table.Indexes.Exists(existing => collation.Equals(existing.Name, indexName))
            || table.KeyConstraints.Exists(existing => collation.Equals(existing.Name, indexName))
            || table.XmlIndexes.Exists(existing => collation.Equals(existing.Name, indexName)))
        {
            throw SimulatedSqlException.IndexAlreadyExists(indexName, written, state: 211);
        }

        // Msg 1934, but with real's spatial-only verify clause and the bare
        // CREATE INDEX verb rather than CREATE SPATIAL INDEX (probe-confirmed).
        if (IncorrectSetOptionNames(context) is { } setOptions)
            throw SimulatedSqlException.IncorrectSetOptionsForSpatialIndex(setOptions);

        var typeName = kind == SpatialIndexKind.Geography ? "GEOGRAPHY" : "GEOMETRY";
        string scheme;
        if (writtenScheme is null)
        {
            scheme = options.GridsName is null ? typeName + "_AUTO_GRID" : typeName + "_GRID";
        }
        else
        {
            scheme = writtenScheme.ToUpperInvariant();
            if (scheme != typeName + "_GRID" && scheme != typeName + "_AUTO_GRID")
                throw SimulatedSqlException.SpatialTessellationSchemeNotFound(writtenScheme, kind == SpatialIndexKind.Geography ? "geography" : "geometry");
        }
        var autoGrid = scheme.EndsWith("_AUTO_GRID", StringComparison.Ordinal);

        if (kind == SpatialIndexKind.Geometry && options.BoundingBoxName is null)
            throw SimulatedSqlException.SpatialIndexBoundingBoxRequired();
        if (kind == SpatialIndexKind.Geography && options.BoundingBoxName is { } refusedBox)
            throw SimulatedSqlException.SpatialIndexParametersIncorrect(refusedBox, state: 1);
        if (options.GridsIncomplete && options.GridsName is { } incompleteGrids)
            throw SimulatedSqlException.SpatialIndexParameterIncomplete(incompleteGrids, state: 1);
        if (autoGrid && options.GridsName is { } refusedGrids)
            throw SimulatedSqlException.SpatialIndexParametersIncorrect(refusedGrids, state: 1);
        if (options.BoundingBoxIncompleteState is byte boxState)
            throw SimulatedSqlException.SpatialIndexParameterIncomplete(boxState == 4 ? "BOUNDING_BOX" : options.BoundingBoxName!, boxState);
        if (options.BoundingBox is { } box)
        {
            if (box[2] <= box[0])
                throw SimulatedSqlException.SpatialIndexBoundInverted("xmax", "xmin");
            if (box[3] <= box[1])
                throw SimulatedSqlException.SpatialIndexBoundInverted("ymax", "ymin");
        }
        if (options.CellsPerObject is int cells)
        {
            if (cells <= 0)
                throw SimulatedSqlException.SpatialIndexCellsTooFew();
            if (cells > 8192)
                throw SimulatedSqlException.SpatialIndexCellsTooMany();
        }

        var levels = autoGrid ? null : options.Grids ?? [2, 2, 2, 2];
        var cellsPerObject = options.CellsPerObject ?? (!autoGrid ? 16 : kind == SpatialIndexKind.Geography ? 12 : 8);

        var objectId = replaced?.ObjectId ?? context.CurrentDatabase.AllocateObjectId();
        // One past the table's highest spatial id (probed 2026-09-26).
        var indexId = 384000;
        foreach (var existing in table.SpatialIndexes)
            indexId = Math.Max(indexId, existing.IndexId + 1);
        var created = new SpatialIndex(
            objectId, indexName, replaced?.IndexId ?? indexId, colOrdinal + 1, kind, scheme,
            options.BoundingBox?[0], options.BoundingBox?[1], options.BoundingBox?[2], options.BoundingBox?[3],
            levels?[0], levels?[1], levels?[2], levels?[3],
            cellsPerObject);
        var position = replaced is null ? table.SpatialIndexes.Count : table.SpatialIndexes.IndexOf(replaced);
        if (replaced is null)
            table.SpatialIndexes.Add(created);
        else
            table.SpatialIndexes[position] = created;
        RecordDdlUndo(context, () =>
        {
            if (replaced is null)
                _ = table.SpatialIndexes.Remove(created);
            else if (table.SpatialIndexes.IndexOf(created) is var slot and >= 0)
                table.SpatialIndexes[slot] = replaced;
        });
        return true;
    }

    /// <summary>What a <c>CREATE SPATIAL INDEX</c>'s <c>WITH</c> list asked for, as written.</summary>
    private sealed class SpatialIndexOptions
    {
        /// <summary>The <c>BOUNDING_BOX</c> keyword as written, null when absent.</summary>
        public string? BoundingBoxName;

        /// <summary><c>xmin, ymin, xmax, ymax</c> once all four are known.</summary>
        public double[]? BoundingBox;

        /// <summary>Msg 12014's state for a box missing parts: 1 positional, 4 named.</summary>
        public byte? BoundingBoxIncompleteState;

        /// <summary>The <c>GRIDS</c> keyword as written, null when absent.</summary>
        public string? GridsName;

        /// <summary>The four level codes (1 LOW, 2 MEDIUM, 3 HIGH) once complete.</summary>
        public short[]? Grids;

        public bool GridsIncomplete;

        public int? CellsPerObject;

        public bool DropExisting;
    }

    /// <summary>
    /// Parses the body of <c>WITH ( … )</c>. Cursor on entry: <c>(</c>; on
    /// return: <c>)</c>. The tessellation parameters are recorded for the
    /// statement to judge; the relational options real accepts are checked
    /// for what the grammar refuses and otherwise discarded.
    /// </summary>
    private static void ParseSpatialIndexOptions(ParserContext context, SpatialIndexOptions options)
    {
        var seenRelational = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            context.MoveNextRequired();
            var optionName = context.Token switch
            {
                Name n => n.Value,
                ReservedKeyword keyword => keyword.Keyword.ToString(),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            var upper = optionName.ToUpperInvariant();

            switch (upper)
            {
                case "BOUNDING_BOX":
                    if (options.BoundingBoxName is not null)
                        throw SimulatedSqlException.SpatialIndexDuplicateParameter();
                    options.BoundingBoxName = optionName;
                    ParseSpatialBoundingBox(context, options);
                    break;
                case "CELLS_PER_OBJECT":
                    if (options.CellsPerObject is not null)
                        throw SimulatedSqlException.SpatialIndexDuplicateParameter();
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    var negative = context.GetNextRequired() is Operator { Character: '-' };
                    if (negative)
                        context.MoveNextRequired();
                    if (context.Token is not Numeric { Value.Type: Int32SqlType } cells)
                    {
                        if (context.Token is not (Numeric or Literal))
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        throw SimulatedSqlException.SpatialIndexParametersIncorrect("CELLS_PER_OBJECT", state: 40);
                    }
                    options.CellsPerObject = negative ? -cells.Value.AsInt32 : cells.Value.AsInt32;
                    break;
                case "GRIDS":
                    if (options.GridsName is not null)
                        throw SimulatedSqlException.SpatialIndexDuplicateParameter();
                    options.GridsName = optionName;
                    ParseSpatialGrids(context, options);
                    break;
                case "IGNORE_DUP_KEY":
                    throw SimulatedSqlException.InvalidSpatialIndexOptionUsage(optionName, state: 2);
                case "ONLINE":
                case "RESUMABLE":
                    if (ReadSpatialRelationalValue(context) is "ON")
                        throw SimulatedSqlException.InvalidSpatialIndexOptionUsage(upper, state: 3);
                    break;
                case "FILLFACTOR":
                    if (context.GetNextRequired() is not Operator { Character: '=' } || context.GetNextRequired() is not Numeric fill)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (fill.Value.AsInt32 is < 1 or > 100)
                        throw SimulatedSqlException.FillFactorOutOfRange(fill.Value.AsInt32);
                    break;
                case "DROP_EXISTING":
                    options.DropExisting = ReadSpatialRelationalValue(context) is "ON";
                    break;
                case "ALLOW_PAGE_LOCKS" or "ALLOW_ROW_LOCKS" or "DATA_COMPRESSION" or "MAXDOP" or "PAD_INDEX"
                    or "SORT_IN_TEMPDB" or "STATISTICS_INCREMENTAL" or "STATISTICS_NORECOMPUTE":
                    if (!seenRelational.Add(upper))
                        throw SimulatedSqlException.SpatialIndexDuplicateParameter();
                    _ = ReadSpatialRelationalValue(context);
                    break;
                default:
                    throw SimulatedSqlException.UnrecognizedIndexOption(optionName, "CREATE SPATIAL INDEX");
            }

            if (context.GetNextOptional() is Operator { Character: ',' })
                continue;
            if (context.Token is Operator { Character: ')' })
                return;
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// Reads a relational option's <c>= value</c>, cursor on the option name,
    /// leaving it on the value's last token; returns the value's upper-cased
    /// text.
    /// </summary>
    private static string ReadSpatialRelationalValue(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return context.GetNextRequired() switch
        {
            Name name => name.Value.ToUpperInvariant(),
            ReservedKeyword keyword => keyword.Keyword.ToString().ToUpperInvariant(),
            Numeric numeric => numeric.Value.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    /// <summary>
    /// <c>= (xmin, ymin, xmax, ymax)</c> positionally or <c>= (xmin = v, …)</c>
    /// by name in any order; a value is a signed number or a string that reads
    /// as one. Cursor on the keyword; leaves it on the closing <c>)</c>.
    /// </summary>
    private static void ParseSpatialBoundingBox(ParserContext context, SpatialIndexOptions options)
    {
        ConsumeEqualsThen(context, '(');
        var values = new double?[4];
        var positional = 0;
        var named = false;
        while (true)
        {
            var checkpoint = context.SaveCheckpoint();
            var next = context.GetNextRequired();
            if (next is Name partName && context.GetNextRequired() is Operator { Character: '=' })
            {
                Span<char> upperPart = stackalloc char[partName.Value.Length];
                _ = partName.Value.AsSpan().ToUpperInvariant(upperPart);
                var slot = upperPart switch
                {
                    "XMAX" => 2,
                    "XMIN" => 0,
                    "YMAX" => 3,
                    "YMIN" => 1,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(partName),
                };
                if (values[slot] is not null)
                    throw SimulatedSqlException.SpatialIndexDuplicateParameter();
                values[slot] = ConsumeSignedDoubleValue(context);
                named = true;
            }
            else
            {
                context.RestoreCheckpoint(checkpoint);
                if (named)
                    throw SimulatedSqlException.SyntaxErrorNear(context.GetNextRequired());
                var value = ConsumeSignedDoubleValue(context);
                if (positional < 4)
                    values[positional] = value;
                positional++;
            }
            if (context.GetNextRequired() is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            break;
        }
        if (named ? Array.Exists(values, v => v is null) : positional != 4)
            options.BoundingBoxIncompleteState = named ? (byte)4 : (byte)1;
        else
            options.BoundingBox = [values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value];
    }

    /// <summary>
    /// <c>= (level, level, level, level)</c> positionally, or <c>= (LEVEL_n =
    /// level, …)</c> by name with the rest MEDIUM; a level is LOW, MEDIUM or
    /// HIGH. A positional list short of four, or a word that names no level,
    /// is incomplete (Msg 12014); a number is Msg 12005 state 29. Cursor on
    /// the keyword; leaves it on the closing <c>)</c>.
    /// </summary>
    private static void ParseSpatialGrids(ParserContext context, SpatialIndexOptions options)
    {
        ConsumeEqualsThen(context, '(');
        var levels = new short?[4];
        var positional = 0;
        var named = false;
        var incomplete = false;
        while (true)
        {
            var checkpoint = context.SaveCheckpoint();
            var next = context.GetNextRequired();
            if (next is Name levelName && levelName.Value.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase)
                && context.GetNextRequired() is Operator { Character: '=' })
            {
                if (positional > 0)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                Span<char> upperLevel = stackalloc char[levelName.Value.Length];
                _ = levelName.Value.AsSpan().ToUpperInvariant(upperLevel);
                var slot = upperLevel switch
                {
                    "LEVEL_1" => 0,
                    "LEVEL_2" => 1,
                    "LEVEL_3" => 2,
                    "LEVEL_4" => 3,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(levelName),
                };
                if (levels[slot] is not null)
                    throw SimulatedSqlException.SpatialIndexDuplicateParameter();
                levels[slot] = ReadGridLevel(context.GetNextRequired(), ref incomplete);
                named = true;
            }
            else
            {
                context.RestoreCheckpoint(checkpoint);
                var level = context.GetNextRequired();
                if (named)
                {
                    // A positional level after a named one is a syntax error
                    // at the list's close (probed 2026-10-05).
                    while (context.Token is not Operator { Character: ')' })
                        context.MoveNextRequired();
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                var code = ReadGridLevel(level, ref incomplete);
                if (positional < 4)
                    levels[positional] = code;
                positional++;
            }
            if (context.GetNextRequired() is Operator { Character: ',' })
                continue;
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            break;
        }
        if (Array.Exists(levels, level => level == 0))
            throw SimulatedSqlException.SpatialIndexParametersIncorrect("GRIDS", state: 29);
        if (incomplete || (!named && positional != 4))
        {
            options.GridsIncomplete = true;
            return;
        }
        options.Grids = [levels[0] ?? 2, levels[1] ?? 2, levels[2] ?? 2, levels[3] ?? 2];
    }

    /// <summary>
    /// A grid level's code: 1 LOW, 2 MEDIUM, 3 HIGH; 0 marks a number, which
    /// real refuses outright; any other word leaves the list incomplete.
    /// </summary>
    private static short ReadGridLevel(Token token, ref bool incomplete)
    {
        switch (token)
        {
            case Numeric:
                return 0;
            case Name name:
                Span<char> upper = stackalloc char[name.Value.Length];
                _ = name.Value.AsSpan().ToUpperInvariant(upper);
                switch (upper)
                {
                    case "HIGH":
                        return 3;
                    case "LOW":
                        return 1;
                    case "MEDIUM":
                        return 2;
                }
                incomplete = true;
                return 2;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(token);
        }
    }

    private static void ConsumeEqualsThen(ParserContext context, char expected)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator op || op.Character != expected)
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    private static double ConsumeSignedDoubleValue(ParserContext context)
    {
        var next = context.GetNextRequired();
        var negate = false;
        if (next is Operator { Character: '-' })
        {
            negate = true;
            next = context.GetNextRequired();
        }
        double raw;
        if (next is Numeric numeric)
        {
            raw = numeric.Value.CoerceTo(SqlType.Float).AsDouble;
        }
        else if (next is Literal { Value: var literal } && SqlType.IsStringCategory(literal.Type)
            && double.TryParse(literal.AsString, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            // A string that reads as a number is taken as one (probed
            // 2026-10-05: `BOUNDING_BOX = (0, 0, '10', 10)`).
            raw = parsed;
        }
        else
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        return negate ? -raw : raw;
    }
}
