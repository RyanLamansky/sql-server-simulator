using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace SqlServerSimulator.Bacpac;

/// <summary>
/// Builds an in-memory bacpac stream targeted at a specific test objective.
/// Replaces the multi-GB AdventureWorks / WideWorldImporters reference
/// bacpacs that previously gated the loader tests — each migrated test
/// owns a minimal builder invocation that exercises exactly the element
/// types its assertion needs.
/// </summary>
/// <remarks>
/// <para>Emitted archive contains <c>model.xml</c> + zero or more
/// <c>Data/&lt;schema&gt;.&lt;table&gt;/TableData-000-00000.BCP</c>
/// entries. The OPC bookkeeping files DACFx normally writes
/// (<c>[Content_Types].xml</c>, <c>_rels/.rels</c>, <c>DacMetadata.xml</c>,
/// <c>Origin.xml</c>) are omitted — the simulator's loader doesn't
/// consume them.</para>
/// <para>Feature surface grows as tests demand it. v0 supports
/// <c>int</c> columns (NULL / NOT NULL), one schema, one or more tables,
/// zero or more rows. New types layer in via
/// <see cref="TableBuilder.Column(string, string, bool)"/> + the
/// per-type branch in <see cref="EncodeBcpValue"/>.</para>
/// </remarks>
public sealed class BacpacBuilder
{
    internal const string ModelNs = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    private readonly HashSet<string> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TableBuilder> _tables = [];

    private BacpacBuilder() { }

    public static BacpacBuilder Create() => new();

    /// <summary>
    /// Adds a <c>dbo</c>-default-schema or named-schema to the model.
    /// Idempotent — calling twice with the same name is a no-op. The
    /// loader auto-creates <c>dbo</c>, but adding it explicitly mirrors
    /// real DACFx output and keeps the SqlSchema element-count assertions
    /// honest.
    /// </summary>
    public BacpacBuilder Schema(string name)
    {
        _ = _schemas.Add(name);
        return this;
    }

    /// <summary>
    /// Adds a table to <paramref name="schemaName"/>. The schema is
    /// auto-created (effectively <see cref="Schema"/> + the table)
    /// when not previously added.
    /// </summary>
    public BacpacBuilder Table(string schemaName, string tableName, Action<TableBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _ = _schemas.Add(schemaName);
        var table = new TableBuilder(schemaName, tableName);
        configure(table);
        _tables.Add(table);
        return this;
    }

    /// <summary>
    /// Emits the bacpac as a <see cref="MemoryStream"/> ready to feed to
    /// <c>Simulation.FromBacpac(Stream, out BacpacLoadResult)</c>.
    /// The stream is positioned at 0 and is seekable.
    /// </summary>
    public Stream Build()
    {
        var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteModelXml(archive);
            foreach (var table in _tables)
                WriteTableData(archive, table);
        }
        memory.Position = 0;
        return memory;
    }

    private void WriteModelXml(ZipArchive archive)
    {
        XNamespace ns = ModelNs;
        var model = new XElement(ns + "Model");

        // dbo is pre-seeded on every Database — emitting CREATE SCHEMA dbo
        // raises Msg 2714 (or similar). Real DACFx omits dbo from the
        // SqlSchema element list for the same reason; the builder mirrors.
        foreach (var schemaName in _schemas)
        {
            if (string.Equals(schemaName, "dbo", StringComparison.OrdinalIgnoreCase))
                continue;
            model.Add(new XElement(ns + "Element",
                new XAttribute("Type", "SqlSchema"),
                new XAttribute("Name", $"[{schemaName}]")));
        }

        foreach (var table in _tables)
            model.Add(table.ToModelElement(ns));

        var doc = new XDocument(new XElement(ns + "DataSchemaModel", model));
        var entry = archive.CreateEntry("model.xml");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(writer);
    }

    private static void WriteTableData(ZipArchive archive, TableBuilder table)
    {
        if (table.RowCount == 0)
            return;

        var entryName = $"Data/{table.SchemaName}.{table.TableName}/TableData-000-00000.BCP";
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        foreach (var row in table.Rows)
            table.WriteBcpRow(stream, row);
    }

    /// <summary>
    /// Encodes one column value in BCP wire format. Per-type wire shapes
    /// match the format the simulator's BCP reader decodes — see the
    /// CLAUDE.md "BCP wire format" section.
    /// </summary>
    internal static void EncodeBcpValue(Stream stream, ColumnDef column, object? value)
    {
        switch (column.SqlType)
        {
            case "int":
                EncodeInt32(stream, column.Nullable, value);
                return;
            default:
                throw new NotSupportedException($"BacpacBuilder doesn't model BCP encoding for type '{column.SqlType}' yet.");
        }
    }

    private static void EncodeInt32(Stream stream, bool nullable, object? value)
    {
        Span<byte> buf = stackalloc byte[4];
        if (nullable)
        {
            if (value is null)
            {
                stream.WriteByte(0xFF);
                return;
            }
            stream.WriteByte(0x04);
        }
        BinaryPrimitives.WriteInt32LittleEndian(buf, Convert.ToInt32(value));
        stream.Write(buf);
    }
}
