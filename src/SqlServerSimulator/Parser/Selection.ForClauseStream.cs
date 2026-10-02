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

    private static IEnumerable<byte[]> StreamDocument(Selection document, SqlType type, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        foreach (var row in document.Execute(batch, outerResolver).RowValues)
        {
            var text = row[0].AsString;
            for (var start = 0; start < text.Length; start += StreamedDocumentChunkLength)
            {
                var chunk = text.Substring(start, Math.Min(StreamedDocumentChunkLength, text.Length - start));
                yield return RowEncoder.EncodeRow([type], [type == SqlType.NText ? SqlValue.FromNText(chunk) : SqlValue.FromNVarchar(SqlType.NVarcharMax, chunk)]);
            }
        }
    }

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
