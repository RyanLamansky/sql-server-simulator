using SqlServerSimulator.Parser.FullText;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    private static readonly SqlType[] FtsParserSchema =
    [
        VarbinarySqlType.Get(128),
        SqlType.Int32,
        SqlType.Int32,
        SqlType.Int32,
        NVarcharSqlType.Get(16, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit),
        NVarcharSqlType.Get(4000, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit),
        SqlType.Int32,
        NVarcharSqlType.Get(4000, Collation.Get("Latin1_General_CI_AS_KS_WS"), Coercibility.Implicit),
    ];

    private static readonly string[] FtsParserColumnNames =
        ["keyword", "group_id", "phrase_id", "occurrence", "special_term", "display_term", "expansion_type", "source_term"];

    /// <summary>
    /// Built-in system TVF <c>sys.dm_fts_parser('query_string', lcid,
    /// stoplist_id, accent_sensitivity)</c>: what the full-text engine makes of
    /// a <c>CONTAINS</c> condition — each term the word breaker produced, its
    /// occurrence, whether the stoplist drops it, the sentence and paragraph
    /// markers, and a <c>FORMSOF(INFLECTIONAL, …)</c> leaf's expansions. The
    /// stoplist is 0 for the language's system stoplist or NULL for none;
    /// accent sensitivity 0 folds accents. Probed 2026-09-29 against SQL
    /// Server 2025, including the argument errors.
    /// </summary>
    public static Selection ParseFtsParser(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 4);
        return new Selection(FtsParserSchema, FtsParserColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateFtsParser(arguments, batch, outerResolver));
    }

    private static List<byte[]> EnumerateFtsParser(Expression[] arguments, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var query = arguments[0].Run(runtime);
        var lcid = arguments[1].Run(runtime);
        var stoplistId = arguments[2].Run(runtime);
        var accent = arguments[3].Run(runtime);
        if (query.IsNull)
            throw SimulatedSqlException.FullTextParserNullArgument(201);
        if (lcid.IsNull)
            throw SimulatedSqlException.FullTextParserNullArgument(202);
        if (accent.IsNull)
            throw SimulatedSqlException.FullTextParserNullArgument(203);
        var language = FullTextLanguage.Resolve(lcid);
        var useStoplist = false;
        if (!stoplistId.IsNull)
        {
            var id = stoplistId.CoerceTo(SqlType.Int32).AsInt32;
            if (id != 0)
                throw SimulatedSqlException.FullTextStoplistIdNotFound(id);
            useStoplist = true;
        }
        var text = query.CoerceTo(SqlType.NVarcharMax).AsString;
        if (string.IsNullOrWhiteSpace(text))
            throw SimulatedSqlException.FullTextNullOrEmptyPredicate();
        var accentSensitive = accent.CoerceTo(SqlType.Int32).AsInt32 != 0;

        var report = new FullTextParserReport();
        _ = FullTextSearchCondition.ParseContains(text, accentSensitive, language, useStoplist, report);
        List<byte[]> rows = new(report.Rows.Count);
        foreach (var row in report.Rows)
        {
            rows.Add(RowEncoder.EncodeRow(FtsParserSchema, [
                SqlValue.FromVarbinary(row.Keyword),
                SqlValue.FromInt32(row.GroupId),
                SqlValue.FromInt32(0),
                SqlValue.FromInt32(row.Occurrence),
                SqlValue.FromNVarchar(row.SpecialTerm),
                SqlValue.FromNVarchar(row.DisplayTerm),
                SqlValue.FromInt32(row.ExpansionType),
                SqlValue.FromNVarchar(row.Source),
            ]));
        }
        return rows;
    }
}
