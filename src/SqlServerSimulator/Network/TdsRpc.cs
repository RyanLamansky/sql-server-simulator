using System.Buffers.Binary;
using System.Data;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>One RPC invocation parsed from an RPC request message (MS-TDS §2.2.6.6).</summary>
internal sealed class TdsRpcRequest
{
    /// <summary>The stored-procedure name; empty when the numeric ProcID form is used.</summary>
    public readonly string ProcName;

    /// <summary>The numeric well-known procedure id; zero when the name form is used.</summary>
    public readonly ushort ProcId;

    /// <summary>The RPC parameters in declaration order.</summary>
    public readonly List<TdsRpcParameter> Parameters;

    private TdsRpcRequest(string procName, ushort procId, List<TdsRpcParameter> parameters)
    {
        this.ProcName = procName;
        this.ProcId = procId;
        this.Parameters = parameters;
    }

    /// <summary>
    /// Parses a full RPC message payload: skips ALL_HEADERS (a leading uint32 LE
    /// total length covering the header block), then one or more RPC requests
    /// separated by the 0xFF batch-flag byte (TDS 7.2+).
    /// </summary>
    public static List<TdsRpcRequest> ParseMessage(byte[] payload, string currentDatabase)
    {
        var reader = new TdsValueReader(payload);

        var headerLength = reader.ReadUInt32();
        if (headerLength < 4 || headerLength > (uint)payload.Length)
            throw new InvalidDataException($"RPC ALL_HEADERS length {headerLength} is outside the {payload.Length}-byte payload.");

        reader.Position = (int)headerLength;

        var requests = new List<TdsRpcRequest>();
        while (!reader.AtEnd)
            requests.Add(ParseRequest(reader, currentDatabase));

        return requests;
    }

    private static TdsRpcRequest ParseRequest(TdsValueReader reader, string currentDatabase)
    {
        var procName = "";
        ushort procId = 0;
        var nameLenProcId = reader.ReadUInt16();
        if (nameLenProcId == 0xFFFF)
            procId = reader.ReadUInt16();
        else
            procName = reader.ReadUcs2(nameLenProcId);

        // OptionFlags (fWithRecomp / fNoMetaData / fReuseMetaData); no behavior here.
        _ = reader.ReadUInt16();

        var parameters = new List<TdsRpcParameter>();
        while (!reader.AtEnd)
        {
            // A 0xFF at parameter-start position is the batch flag opening the
            // next RPC request; a real B_VARCHAR name length never reaches it.
            if (reader.PeekByte() == 0xFF)
            {
                _ = reader.ReadByte();
                break;
            }

            parameters.Add(ParseParameter(reader, currentDatabase, parameters.Count + 1));
        }

        return new TdsRpcRequest(procName, procId, parameters);
    }

    private static TdsRpcParameter ParseParameter(TdsValueReader reader, string currentDatabase, int ordinal)
    {
        var name = reader.ReadUcs2(reader.ReadByte());
        var isOutput = (reader.ReadByte() & 0x01) != 0;
        var token = reader.ReadByte();
        return token switch
        {
            0x22 => DecodeImage(reader, name, isOutput),
            0x23 => DecodeLegacyLob(reader, name, isOutput, ansi: true),
            0x24 => DecodeGuid(reader, name, isOutput),
            0x26 => DecodeIntN(reader, name, isOutput),
            0x28 => DecodeDate(reader, name, isOutput),
            0x29 => DecodeTime(reader, name, isOutput),
            0x2A => DecodeDateTime2(reader, name, isOutput),
            0x2B => DecodeDateTimeOffset(reader, name, isOutput),
            0x62 => DecodeSqlVariant(reader, name, isOutput),
            0x63 => DecodeLegacyLob(reader, name, isOutput, ansi: false),
            0x68 => DecodeBit(reader, name, isOutput),
            0x6A => DecodeDecimal(reader, name, isOutput),
            0x6C => DecodeDecimal(reader, name, isOutput),
            0x6D => DecodeFloatN(reader, name, isOutput),
            0x6E => DecodeMoneyN(reader, name, isOutput),
            0x6F => DecodeDateTimeN(reader, name, isOutput),
            0xA5 => DecodeBinary(reader, name, isOutput),
            0xA7 => DecodeAnsiString(reader, name, isOutput, DbType.AnsiString),
            0xAD => DecodeBinary(reader, name, isOutput),
            0xAF => DecodeAnsiString(reader, name, isOutput, DbType.AnsiStringFixedLength),
            0xE7 => DecodeNationalString(reader, name, isOutput, DbType.String),
            0xEF => DecodeNationalString(reader, name, isOutput, DbType.StringFixedLength),
            0xF0 => DecodeClrUdt(reader, name, isOutput, currentDatabase, ordinal),
            0xF1 => DecodeXml(reader, name, isOutput),
            0xF3 => DecodeTableValued(reader, name, isOutput),
            _ => throw new NotSupportedException($"Unrecognized TDS RPC parameter type token 0x{token:X2}."),
        };
    }

    /// <summary>
    /// Decodes a legacy <c>image</c> RPC parameter (type token <c>0x22</c>):
    /// TYPE_INFO is a 4-byte LONGLEN max size (ignored) with no collation, and
    /// the value is a 4-byte data length (<c>0xFFFFFFFF</c> = NULL) followed by
    /// the raw bytes — the contiguous legacy form, not PLP (probe-confirmed
    /// against SqlClient 7.0.2 that even a &gt;1-packet image parameter arrives
    /// this way, 2026-07-19). Binds as <see cref="DbType.Binary"/>; the engine
    /// coerces varbinary into the target <c>image</c> column, mirroring how the
    /// <c>text</c> / <c>ntext</c> string parameters bind. Output-direction image
    /// parameters are not a real SQL Server feature, so none arrive.
    /// </summary>
    private static TdsRpcParameter DecodeImage(TdsValueReader reader, string name, bool isOutput)
    {
        _ = reader.ReadUInt32();
        var dataLength = reader.ReadUInt32();
        return dataLength == 0xFFFFFFFF
            ? new TdsRpcParameter(name, isOutput, DbType.Binary, null, size: -1)
            : new TdsRpcParameter(name, isOutput, DbType.Binary, reader.ReadBytes(checked((int)dataLength)).ToArray(), size: -1);
    }

    /// <summary>
    /// Decodes a table-valued parameter (type token <c>0xF3</c>) into a
    /// <see cref="TableValuedParameterData"/> carried on the parameter's value,
    /// which the engine's structured-parameter binding materializes into a
    /// table variable — the same path the in-process ADO.NET Structured
    /// parameter takes. The parameter's <see cref="DbType"/> is a placeholder;
    /// the value shape is what routes it to the TVP binding.
    /// </summary>
    private static TdsRpcParameter DecodeTableValued(TdsValueReader reader, string name, bool isOutput)
    {
        var data = TdsTableValuedParameterReader.Read(reader);
        return new TdsRpcParameter(name, isOutput, DbType.Object, data);
    }

    /// <summary>
    /// Decodes a CLR-UDT parameter (type token <c>0xF0</c>): the client-sent
    /// UDT_INFO is three B_VARCHAR names (db / schema / type — SqlClient fills
    /// only the type from <c>SqlParameter.UdtTypeName</c>, leaving db + schema
    /// empty), with neither the leading <c>USHORT</c> max byte size nor the
    /// assembly-qualified name the server's COLMETADATA form carries
    /// (probe-confirmed against SqlClient 7.0.2, 2026-07-19). The value is PLP
    /// carrying the CLR-UDT serialization: OrdPath bytes for
    /// <c>hierarchyid</c> (stored verbatim) or the MS spatial binary for
    /// <c>geography</c> / <c>geometry</c> (decoded back to WKT). The decoded
    /// <see cref="SqlValue"/> is carried on the parameter and bound directly by
    /// the engine (the <c>BatchContext</c> variable-seed <see cref="SqlValue"/>
    /// passthrough). An unrecognized type name raises Msg 8064; spatial bytes the
    /// decoder cannot model raise Msg 8023 — both probe-confirmed against SQL
    /// Server 2025 (2026-07-19).
    /// </summary>
    private static TdsRpcParameter DecodeClrUdt(TdsValueReader reader, string name, bool isOutput, string currentDatabase, int ordinal)
    {
        var db = reader.ReadUcs2(reader.ReadByte());
        _ = reader.ReadUcs2(reader.ReadByte());
        var typeName = reader.ReadUcs2(reader.ReadByte());
        var bytes = TdsWireValue.ReadPlp(reader);
        var value = TdsWireValue.BuildUdtValue(typeName, db, currentDatabase, ordinal, name, bytes);
        return new TdsRpcParameter(name, isOutput, DbType.Object, value);
    }

    /// <summary>
    /// Decodes a <c>sql_variant</c> parameter (type token <c>0x62</c>): the
    /// TYPE_INFO is a 4-byte max length (ignored) and the value is a 4-byte total
    /// length (<c>0</c> = NULL) followed by the MS-TDS §2.2.5.5.3 body — a base-type
    /// token, a property-byte count, the property bytes, then the inner value's raw
    /// data. The body is the read mirror of <c>TdsTypeCodec.BuildVariantBody</c>;
    /// the decoded inner <see cref="SqlValue"/> is wrapped via
    /// <see cref="SqlValue.FromVariant"/> so <c>SQL_VARIANT_PROPERTY(@p,'BaseType')</c>
    /// reports the sent base type. Probe-confirmed against SQL Server 2025 +
    /// SqlClient 7.0.2 (2026-07-19).
    /// </summary>
    private static TdsRpcParameter DecodeSqlVariant(TdsValueReader reader, string name, bool isOutput)
    {
        _ = reader.ReadUInt32();
        var length = reader.ReadUInt32();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Object, SqlValue.Null(SqlType.SqlVariant));

        var inner = TdsWireValue.ReadVariantBody(reader);
        return new TdsRpcParameter(name, isOutput, DbType.Object, SqlValue.FromVariant(inner));
    }

    private static TdsRpcParameter DecodeIntN(TdsValueReader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var dbType = declaredLength switch
        {
            1 => DbType.Byte,
            2 => DbType.Int16,
            4 => DbType.Int32,
            8 => DbType.Int64,
            _ => throw new InvalidDataException($"INTN declared length {declaredLength} is not 1, 2, 4, or 8."),
        };

        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, dbType, null);

        var raw = TdsWireValue.AssembleLittleEndian(reader.ReadBytes(length));
        object value = declaredLength switch
        {
            1 => (byte)raw,
            2 => (short)raw,
            4 => (int)raw,
            _ => raw,
        };

        return new TdsRpcParameter(name, isOutput, dbType, value);
    }

    private static TdsRpcParameter DecodeBit(TdsValueReader reader, string name, bool isOutput)
    {
        _ = reader.ReadByte();
        var length = reader.ReadByte();
        return length == 0
            ? new TdsRpcParameter(name, isOutput, DbType.Boolean, null)
            : new TdsRpcParameter(name, isOutput, DbType.Boolean, reader.ReadByte() != 0);
    }

    private static TdsRpcParameter DecodeFloatN(TdsValueReader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var dbType = declaredLength == 4 ? DbType.Single : DbType.Double;
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, dbType, null);

        var payload = reader.ReadBytes(length);
        object value = length == 4
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload))
            : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload));

        return new TdsRpcParameter(name, isOutput, dbType, value);
    }

    private static TdsRpcParameter DecodeMoneyN(TdsValueReader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Currency, null);

        var payload = reader.ReadBytes(length);
        long scaled;
        if (declaredLength == 4)
        {
            scaled = BinaryPrimitives.ReadInt32LittleEndian(payload);
        }
        else
        {
            var high = BinaryPrimitives.ReadInt32LittleEndian(payload);
            var low = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            scaled = ((long)high << 32) | low;
        }

        return new TdsRpcParameter(name, isOutput, DbType.Currency, scaled / 10000m);
    }

    private static TdsRpcParameter DecodeDecimal(TdsValueReader reader, string name, bool isOutput)
    {
        _ = reader.ReadByte();
        var precision = reader.ReadByte();
        var scale = reader.ReadByte();

        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Decimal, null, precision: precision, scale: scale);

        var payload = reader.ReadBytes(length);
        var isNegative = payload[0] != 1;
        var magnitude = payload[1..];
        for (var i = 12; i < magnitude.Length; i++)
        {
            if (magnitude[i] != 0)
                throw new NotSupportedException("A decimal RPC parameter exceeds the range of System.Decimal.");
        }

        Span<byte> assembled = stackalloc byte[12];
        assembled.Clear();
        magnitude[..Math.Min(magnitude.Length, 12)].CopyTo(assembled);
        var lo = BinaryPrimitives.ReadInt32LittleEndian(assembled);
        var mid = BinaryPrimitives.ReadInt32LittleEndian(assembled[4..]);
        var hi = BinaryPrimitives.ReadInt32LittleEndian(assembled[8..]);
        var value = new decimal(lo, mid, hi, isNegative, scale);

        return new TdsRpcParameter(name, isOutput, DbType.Decimal, value, precision: precision, scale: scale);
    }

    private static TdsRpcParameter DecodeGuid(TdsValueReader reader, string name, bool isOutput)
    {
        _ = reader.ReadByte();
        var length = reader.ReadByte();
        return length == 0
            ? new TdsRpcParameter(name, isOutput, DbType.Guid, null)
            : new TdsRpcParameter(name, isOutput, DbType.Guid, new Guid(reader.ReadBytes(16)));
    }

    private static TdsRpcParameter DecodeDate(TdsValueReader reader, string name, bool isOutput)
    {
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Date, null);

        var days = TdsWireValue.ReadThreeByteInt(reader.ReadBytes(3));
        var value = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue);
        return new TdsRpcParameter(name, isOutput, DbType.Date, value);
    }

    private static TdsRpcParameter DecodeTime(TdsValueReader reader, string name, bool isOutput)
    {
        var scale = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Time, null, scale: scale);

        var timeBytes = TdsWireValue.TimeValueBytes(scale);
        var ticks = TdsWireValue.ScaledUnitsToTicks(TdsWireValue.AssembleLittleEndian(reader.ReadBytes(timeBytes)), scale);
        return new TdsRpcParameter(name, isOutput, DbType.Time, TimeSpan.FromTicks(ticks), scale: scale);
    }

    private static TdsRpcParameter DecodeDateTime2(TdsValueReader reader, string name, bool isOutput)
    {
        var scale = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.DateTime2, null, scale: scale);

        var timeBytes = TdsWireValue.TimeValueBytes(scale);
        var payload = reader.ReadBytes(timeBytes + 3);
        var ticks = TdsWireValue.ScaledUnitsToTicks(TdsWireValue.AssembleLittleEndian(payload[..timeBytes]), scale);
        var days = TdsWireValue.ReadThreeByteInt(payload[timeBytes..]);
        var value = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).AddTicks(ticks);
        return new TdsRpcParameter(name, isOutput, DbType.DateTime2, value, scale: scale);
    }

    private static TdsRpcParameter DecodeDateTimeOffset(TdsValueReader reader, string name, bool isOutput)
    {
        var scale = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.DateTimeOffset, null, scale: scale);

        var timeBytes = TdsWireValue.TimeValueBytes(scale);
        var payload = reader.ReadBytes(timeBytes + 5);
        var ticks = TdsWireValue.ScaledUnitsToTicks(TdsWireValue.AssembleLittleEndian(payload[..timeBytes]), scale);
        var days = TdsWireValue.ReadThreeByteInt(payload[timeBytes..(timeBytes + 3)]);
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(payload[(timeBytes + 3)..]);
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var utcTicks = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).Ticks + ticks;
        var value = new DateTimeOffset(utcTicks + offset.Ticks, offset);
        return new TdsRpcParameter(name, isOutput, DbType.DateTimeOffset, value, scale: scale);
    }

    /// <summary>
    /// DATETIMN carries both legacy temporal types, told apart by the declared
    /// length: 4 bytes is <c>smalldatetime</c>, 8 is <c>datetime</c>.
    /// </summary>
    /// <remarks>
    /// The 4-byte form binds a pre-built <see cref="SqlValue"/> rather than a
    /// CLR <see cref="DateTime"/> plus <see cref="DbType.DateTime"/>, because
    /// no <see cref="DbType"/> names <c>smalldatetime</c> and the distinction
    /// is observable: storing the parameter into a <c>sql_variant</c> column
    /// keeps <c>smalldatetime</c> as the base type on real, where a widened
    /// <c>datetime</c> would report the wrong one.
    /// </remarks>
    private static TdsRpcParameter DecodeDateTimeN(TdsValueReader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var isSmall = declaredLength == 4;
        var length = reader.ReadByte();
        if (length == 0)
        {
            return isSmall
                ? new TdsRpcParameter(name, isOutput, DbType.DateTime, SqlValue.Null(SqlType.SmallDateTime))
                : new TdsRpcParameter(name, isOutput, DbType.DateTime, null);
        }

        var payload = reader.ReadBytes(length);
        if (isSmall)
        {
            var smallDays = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            var minutes = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
            var small = TdsWireValue.Epoch1900.AddDays(smallDays).AddMinutes(minutes);
            return new TdsRpcParameter(name, isOutput, DbType.DateTime, SqlValue.FromSmallDateTime(small));
        }

        var days = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var thirds = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var ticks = (((long)thirds * 10_000_000) + 150) / 300;
        return new TdsRpcParameter(name, isOutput, DbType.DateTime, TdsWireValue.Epoch1900.AddDays(days).AddTicks(ticks));
    }

    private static TdsRpcParameter DecodeAnsiString(TdsValueReader reader, string name, bool isOutput, DbType dbType)
    {
        var maxLength = reader.ReadUInt16();
        var utf8 = TdsWireValue.ReadCollationUtf8(reader);
        var encoding = utf8 ? Encoding.UTF8 : CharSqlType.Cp1252Encoder;

        if (maxLength == 0xFFFF)
        {
            var bytes = TdsWireValue.ReadPlp(reader);
            var value = bytes is null ? null : encoding.GetString(bytes);
            return new TdsRpcParameter(name, isOutput, dbType, value, size: -1);
        }

        var length = reader.ReadUInt16();
        return length == 0xFFFF
            ? new TdsRpcParameter(name, isOutput, dbType, null, size: maxLength)
            : new TdsRpcParameter(name, isOutput, dbType, encoding.GetString(reader.ReadBytes(length)), size: maxLength);
    }

    private static TdsRpcParameter DecodeNationalString(TdsValueReader reader, string name, bool isOutput, DbType dbType)
    {
        var maxLength = reader.ReadUInt16();
        _ = TdsWireValue.ReadCollationUtf8(reader);

        if (maxLength == 0xFFFF)
        {
            var bytes = TdsWireValue.ReadPlp(reader);
            var value = bytes is null ? null : Encoding.Unicode.GetString(bytes);
            return new TdsRpcParameter(name, isOutput, dbType, value, size: -1);
        }

        var length = reader.ReadUInt16();
        return length == 0xFFFF
            ? new TdsRpcParameter(name, isOutput, dbType, null, size: maxLength / 2)
            : new TdsRpcParameter(name, isOutput, dbType, Encoding.Unicode.GetString(reader.ReadBytes(length)), size: maxLength / 2);
    }

    private static TdsRpcParameter DecodeBinary(TdsValueReader reader, string name, bool isOutput)
    {
        var maxLength = reader.ReadUInt16();
        if (maxLength == 0xFFFF)
        {
            var bytes = TdsWireValue.ReadPlp(reader);
            return new TdsRpcParameter(name, isOutput, DbType.Binary, bytes, size: -1);
        }

        var length = reader.ReadUInt16();
        return length == 0xFFFF
            ? new TdsRpcParameter(name, isOutput, DbType.Binary, null, size: maxLength)
            : new TdsRpcParameter(name, isOutput, DbType.Binary, reader.ReadBytes(length).ToArray(), size: maxLength);
    }

    private static TdsRpcParameter DecodeXml(TdsValueReader reader, string name, bool isOutput)
    {
        if (reader.ReadByte() == 1)
        {
            // Schema declaration: database, owning schema, and collection name.
            _ = reader.ReadUcs2(reader.ReadByte());
            _ = reader.ReadUcs2(reader.ReadByte());
            _ = reader.ReadUcs2(reader.ReadUInt16());
        }

        var bytes = TdsWireValue.ReadPlp(reader);
        var value = bytes is null ? null : Encoding.Unicode.GetString(bytes);
        // SqlClient prefixes xml parameter content with a UTF-16 BOM; the
        // server treats it as an encoding signal, not document content.
        if (value is ['\uFEFF', ..])
            value = value[1..];

        return new TdsRpcParameter(name, isOutput, DbType.Xml, value);
    }

    /// <summary>
    /// Decodes a legacy large-object string RPC parameter — <c>text</c> (0x23,
    /// CP1252) or <c>ntext</c> (0x63, UTF-16). SqlClient sends the
    /// <c>sp_executesql</c> statement / declaration parameters as <c>ntext</c>
    /// once they exceed nvarchar(4000) (the proc's declared parameter type),
    /// so large parameterized queries — SMO's Object-Explorer database
    /// enumeration among them — arrive this way. TYPE_INFO is a 4-byte LONGLEN
    /// max size + the 5-byte collation; the value is PLP (uint64 total length,
    /// all-ones = NULL, then length-prefixed chunks) exactly like the MAX
    /// string types.
    /// </summary>
    private static TdsRpcParameter DecodeLegacyLob(TdsValueReader reader, string name, bool isOutput, bool ansi)
    {
        _ = reader.ReadUInt32();
        var utf8 = TdsWireValue.ReadCollationUtf8(reader);
        var encoding = ansi ? (utf8 ? Encoding.UTF8 : CharSqlType.Cp1252Encoder) : Encoding.Unicode;
        var dbType = ansi ? DbType.AnsiString : DbType.String;
        var dataLength = reader.ReadUInt32();
        return dataLength == 0xFFFFFFFF
            ? new TdsRpcParameter(name, isOutput, dbType, null, size: -1)
            : new TdsRpcParameter(name, isOutput, dbType, encoding.GetString(reader.ReadBytes(checked((int)dataLength))), size: -1);
    }
}

/// <summary>One RPC parameter: its wire declaration plus the decoded CLR value.</summary>
internal sealed class TdsRpcParameter(string name, bool isOutput, DbType dbType, object? value, int size = 0, byte precision = 0, byte scale = 0)
{
    /// <summary>The parameter name as sent, usually with a leading '@'; may be empty.</summary>
    public readonly string Name = name;

    /// <summary>True when the fByRefValue status bit marks the parameter for output.</summary>
    public readonly bool IsOutput = isOutput;

    /// <summary>The ADO.NET type inferred from the wire type token.</summary>
    public readonly DbType DbType = dbType;

    /// <summary>The decoded CLR value, or null when the wire value is NULL.</summary>
    public readonly object? Value = value;

    /// <summary>Declared maximum: characters for strings, bytes for binary; -1 for MAX; 0 when not applicable.</summary>
    public readonly int Size = size;

    /// <summary>Declared precision for decimal parameters, otherwise 0.</summary>
    public readonly byte Precision = precision;

    /// <summary>Declared scale for decimal / time / datetime2 / datetimeoffset parameters, otherwise 0.</summary>
    public readonly byte Scale = scale;
}
