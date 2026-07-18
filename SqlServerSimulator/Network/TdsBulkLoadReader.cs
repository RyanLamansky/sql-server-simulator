using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>
/// Decodes the <c>BulkLoadBCP</c> token stream (MS-TDS §2.2.6.4, packet type
/// 7) SqlClient's <c>SqlBulkCopy</c> sends after an <c>INSERT BULK</c> batch:
/// a COLMETADATA token declaring the column TYPE_INFO, then a run of ROW
/// tokens carrying values in the same wire encoding <see cref="TdsTypeCodec"/>
/// writes for result rows, terminated by a DONE token. The per-column TYPE_INFO
/// and per-value decode are the shared <see cref="TdsColumnDecoder"/> (also
/// driving table-valued-parameter rows); this file owns only the bulk framing.
/// </summary>
internal static class TdsBulkLoadReader
{
    /// <summary>
    /// Parses the COLMETADATA + ROW stream into a per-row array of values,
    /// each row in COLMETADATA (= <c>INSERT BULK</c> column-list) order.
    /// </summary>
    public static List<SqlValue[]> ReadRows(byte[] payload)
    {
        var reader = new TdsValueReader(payload);
        var token = reader.ReadByte();
        if (token != Tds.TokenColMetadata)
            throw new InvalidDataException($"A bulk-load stream must begin with COLMETADATA (0x81); found 0x{token:X2}.");

        var columnCount = reader.ReadUInt16();
        var columns = new TdsColumnDecoder.Column[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            _ = reader.ReadUInt32(); // UserType
            _ = reader.ReadUInt16(); // Flags
            columns[i] = TdsColumnDecoder.ReadColumnMetadata(reader);
            _ = reader.ReadUcs2(reader.ReadByte()); // ColName (B_VARCHAR, empty in bulk)
        }

        var rows = new List<SqlValue[]>();
        while (!reader.AtEnd)
        {
            var rowToken = reader.ReadByte();
            if (rowToken == Tds.TokenRow)
            {
                var values = new SqlValue[columnCount];
                for (var i = 0; i < columnCount; i++)
                    values[i] = columns[i].ReadValue(reader);
                rows.Add(values);
                continue;
            }

            // DONE / DONEINPROC / DONEPROC terminate the stream.
            if (rowToken is Tds.TokenDone or Tds.TokenDoneInProc or Tds.TokenDoneProc)
                break;

            throw new InvalidDataException($"Unexpected token 0x{rowToken:X2} in a bulk-load ROW stream.");
        }

        return rows;
    }
}
