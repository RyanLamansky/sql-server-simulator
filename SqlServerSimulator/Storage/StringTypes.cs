using System.Text;

namespace SqlServerSimulator.Storage;

internal sealed class VarcharSqlType() : SqlType(SqlTypeCategory.String)
{
    /// <remarks>
    /// CP1252 isn't part of the .NET default encodings on non-Windows
    /// platforms; the provider must be registered before <see cref="Encoding.GetEncoding(int)"/>
    /// will resolve it. The static initializer registers it once per process.
    /// The fallback is literal <c>?</c> replacement rather than .NET's
    /// default best-fit (which maps e.g. Greek Ω → Latin O); SQL Server's
    /// CP1252 conversion replaces unsupported characters with a literal
    /// <c>?</c> and does not transliterate.
    /// </remarks>
    private static readonly Encoding cp1252 = LoadCp1252();

    private static Encoding LoadCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            1252,
            new EncoderReplacementFallback("?"),
            DecoderFallback.ReplacementFallback);
    }

    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => cp1252.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => cp1252.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVarchar(cp1252.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromVarchar((string)raw);

    public override string ToString() => "varchar";
}

internal sealed class NVarcharSqlType() : SqlType(SqlTypeCategory.String)
{
    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNVarchar(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromNVarchar((string)raw);

    public override string ToString() => "nvarchar";
}

internal sealed class SystemNameSqlType() : SqlType(SqlTypeCategory.String)
{
    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromSystemName(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromSystemName((string)raw);

    public override string ToString() => "sysname";
}

internal sealed class VarbinarySqlType() : SqlType(SqlTypeCategory.Other)
{
    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => value.AsBytes.Length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsBytes;
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVarbinary(source.ToArray());

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromVarbinary((byte[])raw);

    public override string ToString() => "varbinary";
}
