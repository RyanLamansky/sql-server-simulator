namespace SqlServerSimulator.Network;

/// <summary>
/// Decodes a table-valued-parameter value (the RPC parameter TYPE_INFO token
/// <c>0xF3</c>, MS-TDS §2.2.5.5.5 TVP_TYPE_INFO): a TVP_TYPENAME (db / schema /
/// type, each B_VARCHAR), TVP_COLMETADATA (a column count then per-column
/// UserType / Flags / TYPE_INFO / ColName), optional ordering/unique metadata,
/// the TVP_END_TOKEN, then TVP_ROW (<c>0x01</c>) tokens each carrying every
/// column's value in the same wire encoding <see cref="TdsColumnDecoder"/>
/// decodes for bulk-load rows, terminated by a final <c>0x00</c>. The decoded
/// rows are handed to the engine's structured-parameter binding, which
/// resolves the named table type, materializes the clone, and inserts the rows
/// exactly as the in-process ADO.NET Structured parameter path does.
/// </summary>
internal static class TdsTableValuedParameterReader
{
    /// <summary>The TVP_NULL column-count sentinel: the whole value is NULL.</summary>
    private const ushort TvpNull = 0xFFFF;

    private const byte TvpEndToken = 0x00;
    private const byte TvpRowToken = 0x01;
    private const byte TvpOrderUnique = 0x10;
    private const byte TvpColumnOrdering = 0x11;

    /// <summary>
    /// Reads the TVP body that follows the <c>0xF3</c> parameter type token
    /// from <paramref name="reader"/>, leaving it positioned after the value's
    /// terminating TVP_END_TOKEN.
    /// </summary>
    public static TableValuedParameterData Read(TdsValueReader reader)
    {
        var databaseName = ReadBVarchar(reader);
        var schemaName = ReadBVarchar(reader);
        var typeName = ReadBVarchar(reader);
        var qualifiedTypeName = schemaName.Length == 0 ? typeName : $"{schemaName}.{typeName}";
        _ = databaseName;

        var columnCount = reader.ReadUInt16();
        if (columnCount == TvpNull)
            return new TableValuedParameterData(qualifiedTypeName, columnCount: -1, rows: []);

        var columns = new TdsColumnDecoder.Column[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            _ = reader.ReadUInt32(); // UserType
            _ = reader.ReadUInt16(); // Flags (fDefault-driven column omission is not sent by DataTable / SqlDataReader sources)
            columns[i] = TdsColumnDecoder.ReadColumnMetadata(reader);
            _ = ReadBVarchar(reader); // ColName (empty; TVP binding is positional)
        }

        // Optional ordering / unique metadata precedes the TVP_END_TOKEN.
        // DataTable / SqlDataReader sources never emit it; a SqlMetaData-driven
        // source that does is rejected with a clear error rather than a
        // stream-desyncing guessed parse.
        byte token;
        while ((token = reader.ReadByte()) != TvpEndToken)
        {
            throw token switch
            {
                TvpOrderUnique or TvpColumnOrdering => new NotSupportedException(
                    "The network listener does not decode table-valued-parameter ordering / unique metadata (TVP_ORDER_UNIQUE / TVP_COLUMN_ORDERING)."),
                _ => new InvalidDataException($"Unexpected token 0x{token:X2} in table-valued-parameter column metadata."),
            };
        }

        var rows = new List<Storage.SqlValue[]>();
        while ((token = reader.ReadByte()) != TvpEndToken)
        {
            if (token != TvpRowToken)
                throw new InvalidDataException($"Unexpected token 0x{token:X2} in a table-valued-parameter ROW stream.");

            var row = new Storage.SqlValue[columnCount];
            for (var i = 0; i < columnCount; i++)
                row[i] = columns[i].ReadValue(reader);
            rows.Add(row);
        }

        return new TableValuedParameterData(qualifiedTypeName, columnCount, rows);
    }

    private static string ReadBVarchar(TdsValueReader reader) => reader.ReadUcs2(reader.ReadByte());
}
