using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Client-boundary cursor decorator applying the session's <c>SET TEXTSIZE</c>
/// byte cap to values as they leave the engine — the wire-egress clip real SQL
/// Server applies (probe-confirmed 2026-07-19). Only MAX-typed and legacy-LOB
/// values truncate: <c>varchar(max)</c> / <c>text</c> at 1 byte per char,
/// <c>nvarchar(max)</c> / <c>ntext</c> at 2 bytes per char with an odd byte
/// floored (TEXTSIZE 11 → 5 chars), <c>varbinary(max)</c> / <c>image</c> at
/// raw bytes. Bounded var types, <c>xml</c>, UDTs, and everything else pass
/// through untouched. LOB-ness keys off the column's <em>declared</em> schema
/// type, not the current value's runtime type — a CAST-produced value can
/// carry a bounded runtime type under a MAX-declared column, and real
/// truncates by the declared type. Installed by
/// <see cref="SimulatedQueryResult.CreateClientCursor"/> only when the result
/// was produced under a finite cap, so unlimited sessions pay nothing.
/// </summary>
internal sealed class TextSizeCursor(RowCursor inner, SqlType[] schema, int textSize) : RowCursor
{
    public override int FieldCount => inner.FieldCount;

    public override bool HasRows => inner.HasRows;

    public override bool MoveNext() => inner.MoveNext();

    public override SqlValue this[int ordinal] => Apply(inner[ordinal], schema[ordinal], textSize);

    protected override void DisposeCore() => inner.Dispose();

    /// <summary>
    /// The client-visible form of <paramref name="value"/> under a
    /// <paramref name="textSize"/> byte cap; the value itself when no
    /// truncation applies (negative cap, NULL, non-LOB declared type, or
    /// within cap). Also applied to output-parameter write-back — real
    /// truncates RETURNVALUE data identically (probe-confirmed).
    /// </summary>
    public static SqlValue Apply(SqlValue value, SqlType declared, int textSize)
    {
        if (textSize < 0 || value.IsNull)
            return value;
        switch (declared)
        {
            case VarcharSqlType { length: SqlType.MaxLengthSentinel } varchar:
                {
                    var text = value.AsString;
                    return text.Length <= textSize ? value : SqlValue.FromVarchar(varchar, text[..textSize]);
                }

            case TextSqlType:
                {
                    var text = value.AsString;
                    return text.Length <= textSize ? value : SqlValue.FromText(text[..textSize]);
                }

            case NVarcharSqlType { length: SqlType.MaxLengthSentinel } nvarchar:
                {
                    var text = value.AsString;
                    var chars = textSize / 2;
                    return text.Length <= chars ? value : SqlValue.FromNVarchar(nvarchar, text[..chars]);
                }

            case NTextSqlType:
                {
                    var text = value.AsString;
                    var chars = textSize / 2;
                    return text.Length <= chars ? value : SqlValue.FromNText(text[..chars]);
                }

            case VarbinarySqlType { length: SqlType.MaxLengthSentinel } varbinary:
                {
                    var bytes = value.AsBytes;
                    return bytes.Length <= textSize ? value : SqlValue.FromVarbinary(varbinary, bytes[..textSize]);
                }

            case ImageSqlType:
                {
                    var bytes = value.AsBytes;
                    return bytes.Length <= textSize ? value : SqlValue.FromImage(bytes[..textSize]);
                }

            default:
                return value;
        }
    }
}
