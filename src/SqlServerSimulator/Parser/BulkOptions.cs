using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>Which of <c>OPENROWSET(BULK …)</c>'s whole-file forms a load names.</summary>
internal enum BulkSingleKind : byte
{
    None,
    Blob,
    Clob,
    NClob,
}

/// <summary>
/// The options a <c>BULK INSERT … WITH ( … )</c> or an
/// <c>OPENROWSET(BULK 'file', …)</c> writes, as parsed. Each option is
/// written at most once (Msg 4130), a numeric one takes an unsigned integer
/// literal and a text one a string literal — anything else is Msg 102 near
/// the option, or near a sign (probed 2026-09-29 against SQL Server 2025).
/// </summary>
internal sealed class BulkOptions
{
    public int? BatchSize;
    public bool CheckConstraints;

    /// <summary>The <c>CODEPAGE</c> keyword as written, which the Linux server refuses (Msg 16202).</summary>
    public string? CodePageWritten;

    /// <summary><c>char</c>, <c>native</c>, <c>widechar</c> or <c>widenative</c>, lowercase.</summary>
    public string? DataFileType;

    public string? DataSource;
    public string? ErrorFile;
    public long? FirstRow;
    public bool FireTriggers;
    public bool KeepIdentity;
    public bool KeepNulls;
    public long? LastRow;
    public int? MaxErrors;

    /// <summary>The <c>ORDER</c> hint's column names.</summary>
    public List<string>? Order;

    /// <summary><c>FORMAT = 'CSV'</c>.</summary>
    public bool Csv;

    public string? FieldQuote;
    public string? FormatFile;
    public string? FieldTerminator;
    public string? RowTerminator;
    public BulkSingleKind Single;

    private readonly HashSet<string> written = new(StringComparer.Ordinal);

    private static readonly string[] DataFileTypes = ["char", "native", "widechar", "widenative"];

    /// <summary>
    /// Parses the option at the cursor, leaving it on the token after. With
    /// <paramref name="rowset"/> the <c>SINGLE_*</c> forms are accepted too,
    /// and a second one is Msg 471.
    /// </summary>
    public void ParseOption(ParserContext context, bool rowset)
    {
        if (context.Token is not (Name or ReservedKeyword))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var written = context.Token is Name name ? name.Value : context.Token.Source.ToString();
        var upper = written.Length <= 64 ? stackalloc char[written.Length] : new char[written.Length];
        _ = written.ToUpperInvariant(upper);
        var key = upper.ToString();

        switch (upper)
        {
            case "SINGLE_BLOB" or "SINGLE_CLOB" or "SINGLE_NCLOB" when rowset:
                if (this.Single != BulkSingleKind.None)
                    throw SimulatedSqlException.BulkSingleOptionRepeated();
                this.Single = upper switch
                {
                    "SINGLE_BLOB" => BulkSingleKind.Blob,
                    "SINGLE_CLOB" => BulkSingleKind.Clob,
                    _ => BulkSingleKind.NClob,
                };
                context.MoveNextOptional();
                return;
            case "CHECK_CONSTRAINTS" or "FIRE_TRIGGERS" or "KEEPIDENTITY" or "KEEPNULLS" or "TABLOCK":
                this.NoteWritten(key);
                _ = upper switch
                {
                    "CHECK_CONSTRAINTS" => this.CheckConstraints = true,
                    "FIRE_TRIGGERS" => this.FireTriggers = true,
                    "KEEPIDENTITY" => this.KeepIdentity = true,
                    "KEEPNULLS" => this.KeepNulls = true,
                    _ => true,
                };
                context.MoveNextOptional();
                return;
            case "ORDER":
                this.NoteWritten(key);
                this.Order = ParseOrder(context);
                return;
        }

        var optionToken = context.Token;
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var value = context.GetNextRequired();
        switch (upper)
        {
            case "BATCHSIZE" or "FIRSTROW" or "KILOBYTES_PER_BATCH" or "LASTROW" or "MAXERRORS" or "ROWS_PER_BATCH":
                {
                    if (value is not Numeric { Value.Type: Int32SqlType } number)
                    {
                        if (value is Operator { Character: '-' or '+' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                    }
                    this.NoteWritten(key);
                    var n = number.Value.AsInt32;
                    switch (upper)
                    {
                        case "BATCHSIZE":
                            this.BatchSize = n;
                            break;
                        case "FIRSTROW":
                            this.FirstRow = n;
                            break;
                        case "LASTROW":
                            this.LastRow = n;
                            break;
                        case "MAXERRORS":
                            this.MaxErrors = n;
                            break;
                    }
                    break;
                }
            case "CODEPAGE" or "DATAFILETYPE" or "DATA_SOURCE" or "ERRORFILE" or "ERRORFILE_DATA_SOURCE" or "FIELDQUOTE" or "FIELDTERMINATOR"
                or "FORMAT" or "FORMATFILE" or "FORMATFILE_DATA_SOURCE" or "ROWTERMINATOR":
                {
                    if (value is not Literal { Value.Type.Category: SqlTypeCategory.String } literal)
                        throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                    var text = literal.Value.AsString;
                    switch (upper)
                    {
                        case "CODEPAGE":
                            this.CodePageWritten = written;
                            break;
                        case "DATAFILETYPE":
                            this.DataFileType = Array.Find(DataFileTypes, fileType => string.Equals(fileType, text, StringComparison.OrdinalIgnoreCase))
                                ?? throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                            break;
                        case "DATA_SOURCE":
                            this.DataSource = text;
                            break;
                        case "ERRORFILE":
                            this.ErrorFile = text;
                            break;
                        case "FIELDQUOTE":
                            this.FieldQuote = text;
                            break;
                        case "FIELDTERMINATOR":
                            this.FieldTerminator = text;
                            break;
                        case "FORMAT":
                            this.Csv = string.Equals(text, "CSV", StringComparison.OrdinalIgnoreCase)
                                ? true
                                : throw SimulatedSqlException.SyntaxErrorNear(optionToken);
                            break;
                        case "FORMATFILE":
                            this.FormatFile = text;
                            break;
                        case "ROWTERMINATOR":
                            this.RowTerminator = text;
                            break;
                    }
                    this.NoteWritten(key);
                    break;
                }
            default:
                throw SimulatedSqlException.SyntaxErrorNear(optionToken);
        }
        context.MoveNextOptional();
    }

    private void NoteWritten(string key)
    {
        if (!this.written.Add(key))
            throw SimulatedSqlException.BulkDuplicateHint();
    }

    /// <summary><c>ORDER ( column [ ASC | DESC ] [ , … ] )</c>, entered on <c>ORDER</c>.</summary>
    private static List<string> ParseOrder(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columns = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name column)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            columns.Add(column.Value);
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc })
                context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return columns;
    }
}
