using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>xml</c> data type — variable-length Unicode text with
/// the documented XML semantics layered on top. The simulator stores the
/// payload identically to <c>nvarchar(MAX)</c> (raw UTF-16 LE bytes) and
/// preserves type identity through SystemTypeId 241 / catalog surface; the
/// XPath / XQuery method surface (<c>.value()</c> / <c>.nodes()</c> /
/// <c>.query()</c> / <c>.exist()</c> / <c>.modify()</c>) raises
/// <see cref="NotSupportedException"/> at execution time. The optional
/// per-column <c>xml(schema_collection)</c> binding lives on the owning
/// <see cref="HeapColumn"/> rather than the type singleton so all
/// xml-typed values share one identity.
/// </summary>
/// <remarks>
/// Matches the doc's skip-with-diagnostic stance for the bacpac baseline:
/// CREATE TABLE accepts <c>xml</c> columns, raw text round-trips through
/// SqlClient as a string (matching real SQL Server's wire format), and
/// XML methods fail loudly when invoked. CAST between <c>xml</c> and
/// <c>varchar</c>/<c>nvarchar</c> is a no-op encoding swap routed through
/// <see cref="SqlValue"/>.<c>Coerce</c> / <see cref="SqlType.Promote"/>.
/// </remarks>
internal sealed class XmlSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override string SqlServerName => "xml";

    public override bool IsFixedLength => false;

    /// <summary>
    /// True — xml columns store off-row in a LOB chain like
    /// <c>nvarchar(MAX)</c>. Routes the GetByName lookup through the
    /// LOB branch, which sets the column's <c>MaxLength</c> to the
    /// <see cref="SqlType.MaxLengthSentinel"/> so truncation checks
    /// don't reject arbitrarily-large xml payloads.
    /// </summary>
    public override bool IsLob => true;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromXml(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromXml((string)raw);

    public override string ToString() => "xml";
}
