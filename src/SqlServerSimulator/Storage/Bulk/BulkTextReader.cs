namespace SqlServerSimulator.Storage.Bulk;

/// <summary>
/// Splits a character-mode data file (<c>char</c> / <c>widechar</c>, decoded
/// to text) into fields, the way the bulk provider reads one: a stream, not
/// lines. Each field runs to its own terminator — the field terminator, or the
/// row terminator for a row's last field — so a row short of fields borrows
/// the next line's text, and a row with too many leaves the rest in its last
/// field (probed 2026-09-29 against SQL Server 2025).
/// </summary>
/// <remarks>
/// <para>
/// At the end of the file: a row's last field that has text is the row's,
/// though no terminator follows; one with none drops the row; any other field
/// the file ends inside is Msg 4832, unless all it read is exactly the row
/// terminator, which ends the file quietly.
/// </para>
/// <para>
/// <c>FORMAT = 'CSV'</c> reads records instead: a field either is enclosed
/// in the quote character, a doubled quote inside standing for one, or has
/// none at all, and it must end at the terminator its position expects — a
/// row terminator where a field terminator belongs, or the reverse, is
/// Msg 4879, as is a stray quote.
/// </para>
/// </remarks>
internal sealed class BulkTextReader(string text, string[] terminators, string fieldTerminator, string rowTerminator, char? quote)
{
    private int position;

    /// <summary>The row the next <see cref="Read"/> reads, counted from 1.</summary>
    public long Row = 1;

    /// <summary>
    /// Reads the next row's fields into <paramref name="fields"/> (one per
    /// terminator, null for an empty one); false at the end of the file. <paramref name="path"/> names
    /// the file in a CSV error.
    /// </summary>
    public bool Read(string?[] fields, string path)
    {
        var read = quote is { } q ? this.ReadCsv(fields, q, path) : this.ReadPlain(fields);
        if (read)
        {
            // An empty field is NULL, quoted or not; a lone NUL character is
            // the empty string, as bcp writes one.
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i] = fields[i] switch
                {
                    { Length: 0 } => null,
                    "\0" => "",
                    var field => field,
                };
            }
            this.Row++;
        }
        return read;
    }

    private bool ReadPlain(string?[] fields)
    {
        for (var i = 0; i < terminators.Length; i++)
        {
            var terminator = terminators[i];
            var last = i == terminators.Length - 1;
            if (i == 0 && this.position >= text.Length)
                return false;
            var end = terminator.Length == 0 ? -1 : text.IndexOf(terminator, this.position, StringComparison.Ordinal);
            if (end < 0)
            {
                var rest = text[this.position..];
                this.position = text.Length;
                if (last)
                {
                    if (rest.Length == 0)
                        return false;
                    fields[i] = rest;
                    return true;
                }
                if (rest == rowTerminator)
                    return false;
                throw SimulatedSqlException.BulkUnexpectedEndOfFile();
            }
            fields[i] = text[this.position..end];
            this.position = end + terminator.Length;
        }
        return true;
    }

    private bool ReadCsv(string?[] fields, char q, string path)
    {
        for (var i = 0; i < terminators.Length; i++)
        {
            var expected = terminators[i];
            var last = i == terminators.Length - 1;
            if (this.position >= text.Length)
            {
                if (i == 0)
                    return false;
                if (last)
                    return false;
                throw SimulatedSqlException.BulkUnexpectedEndOfFile();
            }

            string value;
            if (text[this.position] == q)
            {
                var builder = new System.Text.StringBuilder();
                var scan = this.position + 1;
                while (true)
                {
                    var close = text.IndexOf(q, scan);
                    if (close < 0)
                        throw SimulatedSqlException.BulkUnexpectedEndOfFile();
                    _ = builder.Append(text, scan, close - scan);
                    if (close + 1 < text.Length && text[close + 1] == q)
                    {
                        _ = builder.Append(q);
                        scan = close + 2;
                        continue;
                    }
                    this.position = close + 1;
                    break;
                }
                value = builder.ToString();
                if (this.position >= text.Length)
                {
                    if (!last)
                        throw SimulatedSqlException.BulkUnexpectedEndOfFile();
                    fields[i] = value;
                    return true;
                }
                if (!Matches(expected, this.position))
                    throw SimulatedSqlException.BulkInvalidCsvValue(path, this.Row, i + 1);
                this.position += expected.Length;
                fields[i] = value;
                continue;
            }

            var fieldEnd = fieldTerminator.Length == 0 ? -1 : text.IndexOf(fieldTerminator, this.position, StringComparison.Ordinal);
            var rowEnd = rowTerminator.Length == 0 ? -1 : text.IndexOf(rowTerminator, this.position, StringComparison.Ordinal);
            int end;
            string found;
            if (fieldEnd >= 0 && (rowEnd < 0 || fieldEnd < rowEnd || (fieldEnd == rowEnd && string.Equals(expected, fieldTerminator, StringComparison.Ordinal))))
                (end, found) = (fieldEnd, fieldTerminator);
            else
                (end, found) = (rowEnd, rowTerminator);
            if (end < 0)
            {
                var rest = text[this.position..];
                this.position = text.Length;
                if (rest.Contains(q, StringComparison.Ordinal))
                    throw SimulatedSqlException.BulkInvalidCsvValue(path, this.Row, i + 1);
                if (!last)
                    throw SimulatedSqlException.BulkUnexpectedEndOfFile();
                fields[i] = rest;
                return true;
            }
            value = text[this.position..end];
            if (value.Contains(q, StringComparison.Ordinal) || !string.Equals(found, expected, StringComparison.Ordinal))
                throw SimulatedSqlException.BulkInvalidCsvValue(path, this.Row, i + 1);
            this.position = end + found.Length;
            fields[i] = value;
        }
        return true;
    }

    private bool Matches(string terminator, int at) =>
        terminator.Length > 0 && string.CompareOrdinal(text, at, terminator, 0, terminator.Length) == 0;
}
