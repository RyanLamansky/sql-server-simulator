using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// How a SELECT statement's own FOR JSON / FOR XML result reaches the client.
// Real streams the document rather than returning it as one value: rows of
// 2033 UTF-16 units each, split with no regard for surrogate pairs, the
// untyped FOR XML column typed ntext (nvarchar(max) where the same query is a
// subquery, a view or a function body), and the statement's row count — the
// DONE token's and @@ROWCOUNT — the number of rows the clause serialized
// rather than the rows it sent (probed 2026-09-26 against SQL Server 2025).
internal sealed partial class Selection
{
    private const int StreamedDocumentChunkLength = 2033;

    /// <summary>
    /// Set on a FOR JSON or untyped FOR XML wrapper to the type its document
    /// streams to the client as when it is a SELECT statement's own query.
    /// </summary>
    private SqlType? streamedDocumentType;

    /// <summary>
    /// A FOR JSON or FOR XML wrapper's document text in the order it is
    /// written, a piece as each source row serializes, which a SELECT
    /// statement's own document streams from (<see cref="StreamDocument"/>)
    /// and any other use concatenates (<see cref="WholeDocument"/>).
    /// </summary>
    private DocumentPieces? documentPieces;

    /// <summary>Produces a FOR JSON / FOR XML document's text a piece at a time (see <see cref="documentPieces"/>).</summary>
    private delegate IEnumerable<string> DocumentPieces(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver);

    /// <summary>What <paramref name="body"/> holds, which it then no longer does.</summary>
    private static string Drain(StringBuilder body)
    {
        var text = body.ToString();
        _ = body.Clear();
        return text;
    }

    /// <summary>
    /// The document <paramref name="pieces"/> make as the clause's one result
    /// row (<paramref name="row"/>), or <paramref name="whenEmpty"/> — no row
    /// when null — where they make none.
    /// </summary>
    private static IEnumerable<byte[]> WholeDocument(IEnumerable<string> pieces, Func<string, byte[]> row, byte[]? whenEmpty)
    {
        StringBuilder? document = null;
        foreach (var piece in pieces)
            _ = (document ??= new StringBuilder()).Append(piece);
        if (document is not null)
            yield return row(document.ToString());
        else if (whenEmpty is not null)
            yield return whenEmpty;
    }

    /// <summary>
    /// Whether this is a FOR JSON or untyped FOR XML wrapper, whose single
    /// column's name only the client result carries.
    /// </summary>
    public bool IsForClauseDocument => this.streamedDocumentType is not null;

    /// <summary>
    /// Whether this is a SELECT statement's streamed FOR JSON / FOR XML result,
    /// whose row count is <see cref="StatementContext.ForClauseSourceRows"/>
    /// rather than the rows it returns.
    /// </summary>
    public bool CountsForClauseSourceRows;

    /// <summary>
    /// A SELECT statement's query as the client receives it: a FOR JSON or
    /// untyped FOR XML result streamed in chunks, anything else unchanged.
    /// </summary>
    public Selection AsStatementResult()
    {
        if (this.streamedDocumentType is not { } type)
            return this;
        var document = this;
        return new Selection([type], this.ColumnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => StreamDocument(document, type, batch, outerResolver))
        {
            CountsForClauseSourceRows = true,
            ReferencedSecurables = this.ReferencedSecurables,
            ReadColumnsByObject = this.ReadColumnsByObject,
        };
    }

    /// <summary>
    /// The statement's document in chunks, each going out as the source rows
    /// that write it serialize, as real's does: a reader one chunk into a
    /// <c>FOR JSON PATH</c> over 2,000 rows of 2,000 characters held the key
    /// locks of eleven, twenty chunks in twenty-nine, and one a chunk into the
    /// same rows narrowed to two integers 1,042 (probed 2026-10-09 against
    /// SQL Server 2025 under <c>REPEATABLE READ</c>; <c>FOR XML PATH</c> alike).
    /// </summary>
    private static IEnumerable<byte[]> StreamDocument(Selection document, SqlType type, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var pending = new StringBuilder();
        foreach (var piece in document.documentPieces!(batch, outerResolver))
        {
            var start = 0;
            // A piece completing the pending chunk, then whole chunks of it.
            if (pending.Length != 0)
            {
                var taken = Math.Min(StreamedDocumentChunkLength - pending.Length, piece.Length);
                _ = pending.Append(piece, 0, taken);
                start = taken;
                if (pending.Length < StreamedDocumentChunkLength)
                    continue;
                yield return DocumentChunk(type, Drain(pending));
            }
            for (; piece.Length - start >= StreamedDocumentChunkLength; start += StreamedDocumentChunkLength)
                yield return DocumentChunk(type, piece.Substring(start, StreamedDocumentChunkLength));
            _ = pending.Append(piece, start, piece.Length - start);
        }
        if (pending.Length != 0)
            yield return DocumentChunk(type, pending.ToString());
    }

    private static byte[] DocumentChunk(SqlType type, string chunk) =>
        RowEncoder.EncodeRow([type], [type == SqlType.NText ? SqlValue.FromNText(chunk) : SqlValue.FromNVarchar(SqlType.NVarcharMax, chunk)]);

    /// <summary>
    /// The rows a FOR JSON / FOR XML clause serializes, recording their count
    /// for the statement once they run out. A principal without <c>UNMASK</c>
    /// serializes the masked values (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<byte[]> ForClauseSourceRows(Selection inner, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var count = 0;
        var masking = DataMasking.Applying(batch, inner.ColumnMasks);
        foreach (var row in inner.Execute(batch, outerResolver).RowBytes)
        {
            count++;
            yield return masking is null
                ? row
                : RowEncoder.EncodeRow(inner.Schema, DataMasking.MaskRowForStorage(RowDecoder.DecodeRow(inner.Schema, row), masking, inner.Schema));
        }
        batch.CurrentStatement.ForClauseSourceRows = count;
    }

    /// <summary>
    /// A FOR JSON / FOR XML document's own mask, read where the document is a
    /// value — a scalar subquery's column reads as <c>default()</c> when the
    /// query it serializes reads a masked column (probed 2026-09-27: a masked
    /// <c>(SELECT … FOR JSON PATH)</c> reads <c>xxxx</c>). A SELECT statement's
    /// own streamed document doesn't carry it (<see cref="AsStatementResult"/>).
    /// </summary>
    private static DataMask?[]? ForClauseDocumentMasks(Selection inner)
    {
        DataMask? merged = null;
        if (inner.ColumnMasks is { } masks)
        {
            foreach (var mask in masks)
            {
                if (mask is not null)
                    merged = new DataMask(MaskingFunction.Default, [.. merged?.Sources ?? [], .. mask.Sources]);
            }
        }
        return merged is null ? null : [merged];
    }
}
