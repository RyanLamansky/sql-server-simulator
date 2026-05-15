using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE SPATIAL INDEX name ON table(col) [USING <i>scheme</i>]
    /// [WITH (BOUNDING_BOX = (xmin, ymin, xmax, ymax) | GRIDS = (level [, …]) |
    /// CELLS_PER_OBJECT = n | <i>any other index option</i>)]</c>. Cursor on
    /// entry is the <c>SPATIAL</c> contextual keyword. See
    /// <see cref="SpatialIndex"/> for the no-enforcement rationale.
    /// </summary>
    /// <remarks>
    /// The catalog-row shape comes from probing SQL Server 2025 on 2026-05-15.
    /// Default tessellation when no USING clause is provided is
    /// <c>GEOMETRY_AUTO_GRID</c> for geometry-typed columns and
    /// <c>GEOGRAPHY_AUTO_GRID</c> for geography-typed columns.
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

        string? tessellationScheme = null;
        context.MoveNextOptional();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Using })
        {
            if (context.GetNextRequired() is not Name schemeToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            tessellationScheme = schemeToken.Value;
            context.MoveNextOptional();
        }

        double? bboxXmin = null;
        double? bboxYmin = null;
        double? bboxXmax = null;
        double? bboxYmax = null;
        short? level1Grid = null;
        short? level2Grid = null;
        short? level3Grid = null;
        short? level4Grid = null;
        int? cellsPerObject = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ParseSpatialIndexOptions(context, ref bboxXmin, ref bboxYmin, ref bboxXmax, ref bboxYmax,
                ref level1Grid, ref level2Grid, ref level3Grid, ref level4Grid, ref cellsPerObject);
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(targetTableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());

        var colOrdinal = -1;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (Collation.Default.Equals(table.Columns[i].Name, columnName))
            {
                colOrdinal = i;
                break;
            }
        }
        if (colOrdinal < 0)
            throw SimulatedSqlException.InvalidColumnName(columnName);

        var col = table.Columns[colOrdinal];
        var kind = col.Type == SqlType.Geography ? SpatialIndexKind.Geography
            : col.Type == SqlType.Geometry ? SpatialIndexKind.Geometry
            : throw new NotSupportedException($"CREATE SPATIAL INDEX requires a spatial column; '{columnName}' is {col.Type}.");

        tessellationScheme ??= kind == SpatialIndexKind.Geography ? "GEOGRAPHY_AUTO_GRID" : "GEOMETRY_AUTO_GRID";

        // Real SQL Server requires BOUNDING_BOX for geometry indexes when using
        // a non-AUTO grid; the simulator skips that validation (parse-and-store)
        // since the index never affects execution.

        foreach (var existing in table.SpatialIndexes)
        {
            if (Collation.Default.Equals(existing.Name, indexName))
                throw SimulatedSqlException.ThereIsAlreadyAnObject(indexName);
        }

        var objectId = context.CurrentDatabase.AllocateObjectId();
        // Real SQL Server uses index_id values in the 384000+ range for
        // spatial indexes (probed: index_id = 384000). Synthesize a stable
        // value from the SpatialIndexes count so two indexes on the same
        // table don't collide.
        var indexId = 384000 + table.SpatialIndexes.Count;
        table.SpatialIndexes.Add(new SpatialIndex(
            objectId, indexName, indexId, colOrdinal + 1, kind, tessellationScheme,
            bboxXmin, bboxYmin, bboxXmax, bboxYmax,
            level1Grid, level2Grid, level3Grid, level4Grid,
            cellsPerObject));
        return true;
    }

    /// <summary>
    /// Parses the body of <c>WITH ( … )</c> for a CREATE SPATIAL INDEX
    /// statement. Cursor on entry: <c>(</c>. On return: <c>)</c>. Captures
    /// the recognized options (BOUNDING_BOX / GRIDS / CELLS_PER_OBJECT) into
    /// the ref parameters; every other option (FILLFACTOR, PAD_INDEX,
    /// IGNORE_DUP_KEY, ONLINE, etc.) parse-and-discards via balanced-paren
    /// skipping.
    /// </summary>
    private static void ParseSpatialIndexOptions(ParserContext context,
        ref double? bboxXmin, ref double? bboxYmin, ref double? bboxXmax, ref double? bboxYmax,
        ref short? level1, ref short? level2, ref short? level3, ref short? level4,
        ref int? cellsPerObject)
    {
        while (true)
        {
            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
                return;
            var optionName = context.Token is Name n ? n.Value
                : context.Token is UnquotedString us ? us.ToString()
                : throw SimulatedSqlException.SyntaxErrorNear(context);

            if (Collation.Default.Equals(optionName, "BOUNDING_BOX"))
            {
                ConsumeEqualsThen(context, '(');
                bboxXmin = ConsumeSignedDoubleValue(context);
                ConsumeChar(context, ',');
                bboxYmin = ConsumeSignedDoubleValue(context);
                ConsumeChar(context, ',');
                bboxXmax = ConsumeSignedDoubleValue(context);
                ConsumeChar(context, ',');
                bboxYmax = ConsumeSignedDoubleValue(context);
                ConsumeChar(context, ')');
            }
            else if (Collation.Default.Equals(optionName, "GRIDS"))
            {
                ConsumeEqualsThen(context, '(');
                level1 = ConsumeGridLevel(context);
                if (PeekChar(context) == ',')
                {
                    ConsumeChar(context, ',');
                    level2 = ConsumeGridLevel(context);
                }
                if (PeekChar(context) == ',')
                {
                    ConsumeChar(context, ',');
                    level3 = ConsumeGridLevel(context);
                }
                if (PeekChar(context) == ',')
                {
                    ConsumeChar(context, ',');
                    level4 = ConsumeGridLevel(context);
                }
                ConsumeChar(context, ')');
            }
            else if (Collation.Default.Equals(optionName, "CELLS_PER_OBJECT"))
            {
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Numeric numeric)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                cellsPerObject = numeric.Value.AsInt32;
            }
            else
            {
                // Unknown / non-load-bearing option (FILLFACTOR, PAD_INDEX,
                // IGNORE_DUP_KEY, ONLINE, etc.). Skip until the next ',' or ')'
                // at the top level.
                var depth = 0;
                var done = false;
                while (!done)
                {
                    switch (context.GetNextRequired())
                    {
                        case Operator { Character: '(' }:
                            depth++;
                            break;
                        case Operator { Character: ')' } when depth == 0:
                            return;
                        case Operator { Character: ')' }:
                            depth--;
                            break;
                        case Operator { Character: ',' } when depth == 0:
                            done = true;
                            break;
                    }
                }
                continue;
            }

            if (context.GetNextOptional() is Operator { Character: ',' })
                continue;
            if (context.Token is Operator { Character: ')' })
                return;
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    private static void ConsumeEqualsThen(ParserContext context, char expected)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator op || op.Character != expected)
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    private static void ConsumeChar(ParserContext context, char expected)
    {
        if (context.GetNextRequired() is not Operator op || op.Character != expected)
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    private static char PeekChar(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next is Operator op ? op.Character : '\0';
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
        if (next is not Numeric numeric)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var raw = numeric.Value.Type switch
        {
            _ when numeric.Value.Type == SqlType.Float => numeric.Value.AsDouble,
            _ when numeric.Value.Type == SqlType.Int32 => numeric.Value.AsInt32,
            _ when numeric.Value.Type == SqlType.BigInt => numeric.Value.AsInt64,
            _ => (double)numeric.Value.AsDecimal,
        };
        return negate ? -raw : raw;
    }

    private static short ConsumeGridLevel(ParserContext context) =>
        context.GetNextRequired() switch
        {
            Numeric numeric => (short)numeric.Value.AsInt32,
            Name name when Collation.Default.Equals(name.Value, "LOW") => 1,
            Name name when Collation.Default.Equals(name.Value, "MEDIUM") => 2,
            Name name when Collation.Default.Equals(name.Value, "HIGH") => 3,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
}
