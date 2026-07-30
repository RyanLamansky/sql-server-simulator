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
/// <see cref="TableBuilder.Column"/> + the
/// per-type branch in <see cref="EncodeBcpValue"/>.</para>
/// </remarks>
public sealed partial class BacpacBuilder
{
    internal const string ModelNs = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    private readonly HashSet<string> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TableBuilder> _tables = [];
    private readonly Dictionary<string, string> _databaseOptions = new(StringComparer.Ordinal);
    private readonly List<ProgrammableObjectDef> _programmableObjects = [];
    private readonly List<ExtendedPropertyDef> _extendedProperties = [];
    private readonly List<SequenceDef> _sequences = [];
    private readonly List<string> _roles = [];
    private readonly Dictionary<string, string> _roleAuthorizers = new(StringComparer.Ordinal);
    private readonly List<TableTypeDef> _tableTypes = [];
    private readonly List<PermissionDef> _permissions = [];
    private readonly List<ViewIndexDef> _viewIndexes = [];
    private readonly List<UserDefinedDataTypeDef> _uddts = [];
    private readonly List<XmlSchemaCollectionDef> _xmlSchemaCollections = [];
    private readonly List<XmlIndexDef> _xmlIndexes = [];
    private readonly List<FullTextCatalogDef> _fullTextCatalogs = [];
    private readonly List<FullTextIndexDef> _fullTextIndexes = [];
    private readonly List<(string ElementType, string Name)> _silentlySkipped = [];
    private readonly List<(string ElementType, string Name)> _unknownElements = [];
    private string? _dspName;

    private BacpacBuilder() { }

    public static BacpacBuilder Create() => new();

    /// <summary>
    /// Sets the root <c>DataSchemaModel</c>'s <c>DspName</c> to the schema
    /// provider for compatibility level <paramref name="level"/> (e.g. 130 →
    /// <c>Microsoft.Data.Tools.Schema.Sql.Sql130DatabaseSchemaProvider</c>),
    /// the attribute the loader reads to set the imported database's
    /// compatibility level.
    /// </summary>
    public BacpacBuilder CompatibilityLevel(int level)
    {
        _dspName = $"Microsoft.Data.Tools.Schema.Sql.Sql{level}DatabaseSchemaProvider";
        return this;
    }

    /// <summary>
    /// Sets a database-level option that lands as a property on the
    /// <c>SqlDatabaseOptions</c> element. <paramref name="name"/> is the
    /// DACFx property name as it appears in model.xml (e.g.
    /// <c>"IsReadCommittedSnapshot"</c>); <paramref name="value"/> is the
    /// raw string form (<c>"True"</c> / <c>"False"</c> for toggles, the
    /// DACFx integer enum string for enum-shaped options). All
    /// <see cref="DatabaseOption"/> calls accumulate into one
    /// SqlDatabaseOptions element on Build.
    /// </summary>
    public BacpacBuilder DatabaseOption(string name, string value)
    {
        _databaseOptions[name] = value;
        return this;
    }

    /// <summary>
    /// Adds a CREATE VIEW emission. <paramref name="createStatement"/> is
    /// the full raw T-SQL CREATE VIEW … AS … body — the builder splits it
    /// into HeaderContents + QueryScript that the loader concatenates back
    /// into the same string before re-tokenizing.
    /// </summary>
    public BacpacBuilder View(string schemaName, string viewName, string createStatement)
    {
        _ = _schemas.Add(schemaName);
        _programmableObjects.Add(new ProgrammableObjectDef("SqlView", "QueryScript", schemaName, viewName, createStatement, FunctionBodyHost: false));
        return this;
    }

    /// <summary>Adds a CREATE PROCEDURE emission.</summary>
    public BacpacBuilder Procedure(string schemaName, string procedureName, string createStatement)
    {
        _ = _schemas.Add(schemaName);
        _programmableObjects.Add(new ProgrammableObjectDef("SqlProcedure", "BodyScript", schemaName, procedureName, createStatement, FunctionBodyHost: false));
        return this;
    }

    /// <summary>Adds a CREATE FUNCTION emission (scalar UDF).</summary>
    public BacpacBuilder ScalarFunction(string schemaName, string functionName, string createStatement)
    {
        _ = _schemas.Add(schemaName);
        _programmableObjects.Add(new ProgrammableObjectDef("SqlScalarFunction", "BodyScript", schemaName, functionName, createStatement, FunctionBodyHost: true));
        return this;
    }

    /// <summary>Adds a CREATE FUNCTION emission (multi-statement table-valued function).</summary>
    public BacpacBuilder MultiStatementTvf(string schemaName, string functionName, string createStatement)
    {
        _ = _schemas.Add(schemaName);
        _programmableObjects.Add(new ProgrammableObjectDef("SqlMultiStatementTableValuedFunction", "BodyScript", schemaName, functionName, createStatement, FunctionBodyHost: true));
        return this;
    }

    /// <summary>
    /// Adds a CREATE TRIGGER emission. The 4-segment qualified Name attribute
    /// DACFx uses is <c>[schema].[parentTable].[triggerName]</c>; the
    /// builder produces that shape so the simulator's name resolver routes
    /// the trigger to its parent table correctly.
    /// </summary>
    public BacpacBuilder Trigger(string schemaName, string parentTable, string triggerName, string createStatement)
    {
        _ = _schemas.Add(schemaName);
        _programmableObjects.Add(new ProgrammableObjectDef("SqlDmlTrigger", "BodyScript", schemaName, triggerName, createStatement, FunctionBodyHost: false, ParentTable: parentTable));
        return this;
    }

    /// <summary>
    /// Adds a <c>SqlExtendedProperty</c> element. Host is inferred from
    /// which arguments are provided: column-level (all three), table-level
    /// (schema + table), schema-level (schema only), database-level (none).
    /// </summary>
    public BacpacBuilder ExtendedProperty(string propertyName, string value, string? schemaName = null, string? tableName = null, string? columnName = null)
    {
        _extendedProperties.Add(new ExtendedPropertyDef(schemaName, tableName, columnName, propertyName, value, ExtendedPropertyHost.AutoDetect));
        return this;
    }

    /// <summary>Adds an extended property bound to an index host (SqlIndexBase).</summary>
    public BacpacBuilder IndexExtendedProperty(string schemaName, string tableName, string indexName, string propertyName, string value)
    {
        _extendedProperties.Add(new ExtendedPropertyDef(schemaName, tableName, indexName, propertyName, value, ExtendedPropertyHost.Index));
        return this;
    }

    /// <summary>Adds an extended property bound to a constraint host (SqlConstraint).</summary>
    public BacpacBuilder ConstraintExtendedProperty(string schemaName, string constraintName, string propertyName, string value)
    {
        _extendedProperties.Add(new ExtendedPropertyDef(schemaName, constraintName, null, propertyName, value, ExtendedPropertyHost.Constraint));
        return this;
    }

    /// <summary>
    /// Adds an extended property bound to a database DDL trigger host
    /// (<c>SqlDatabaseDdlTrigger</c>, addressed via <c>@level0type=N'TRIGGER'</c>).
    /// The <paramref name="triggerName"/> must match a
    /// <see cref="DatabaseDdlTrigger"/> emitted in the same model.
    /// </summary>
    public BacpacBuilder DdlTriggerExtendedProperty(string triggerName, string propertyName, string value)
    {
        _extendedProperties.Add(new ExtendedPropertyDef(triggerName, null, null, propertyName, value, ExtendedPropertyHost.DdlTrigger));
        return this;
    }

    /// <summary>
    /// Adds an extended property bound to a filegroup host
    /// (<c>SqlFilegroup</c>, addressed via <c>@level0type=N'FILEGROUP'</c>).
    /// The <paramref name="filegroupName"/> must match a <see cref="Filegroup"/>
    /// registered in the same model (or the built-in <c>PRIMARY</c>).
    /// </summary>
    public BacpacBuilder FilegroupExtendedProperty(string filegroupName, string propertyName, string value)
    {
        _extendedProperties.Add(new ExtendedPropertyDef(filegroupName, null, null, propertyName, value, ExtendedPropertyHost.Filegroup));
        return this;
    }

    /// <summary>
    /// Adds an extended property whose host kind is one the loader doesn't
    /// model (e.g. <c>SqlFilegroup</c> / <c>SqlDatabaseDdlTrigger</c>). Lands
    /// on <c>Skipped</c> with a "Host kind … not modeled" reason; exercises
    /// the default arm of the host-kind switch.
    /// </summary>
    public BacpacBuilder UnknownHostExtendedProperty(string hostKind, string hostName, string propertyName, string value)
    {
        _extendedProperties.Add(new ExtendedPropertyDef(hostName, null, null, propertyName, value, ExtendedPropertyHost.Unknown) { UnknownHostKind = hostKind });
        return this;
    }

    /// <summary>
    /// Adds a CREATE SEQUENCE … AS &lt;type&gt; START WITH … INCREMENT BY …
    /// emission. Type defaults to <c>bigint</c> (matches DACFx default);
    /// startValue / increment default to 1 / 1.
    /// </summary>
    public BacpacBuilder Sequence(string schemaName, string sequenceName, string sqlType = "bigint", long startValue = 1, long increment = 1)
    {
        _ = _schemas.Add(schemaName);
        _sequences.Add(new SequenceDef(schemaName, sequenceName, sqlType, startValue, increment));
        return this;
    }

    /// <summary>
    /// Adds a CREATE ROLE emission. <paramref name="ownerPrincipal"/>, when
    /// non-null, attaches an <c>Authorizer</c> Relationship → <c>CREATE ROLE
    /// name AUTHORIZATION owner</c>. Bare form omits the clause.
    /// </summary>
    public BacpacBuilder Role(string roleName, string? ownerPrincipal = null)
    {
        _roles.Add(roleName);
        if (ownerPrincipal is not null)
            _roleAuthorizers[roleName] = ownerPrincipal;
        return this;
    }

    /// <summary>
    /// Adds a CREATE TYPE … AS TABLE (cols [, PRIMARY KEY (cols)]) emission.
    /// </summary>
    public BacpacBuilder TableType(string schemaName, string typeName, Action<TableBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _ = _schemas.Add(schemaName);
        var builder = new TableBuilder(schemaName, typeName);
        configure(builder);
        _tableTypes.Add(new TableTypeDef(schemaName, typeName, builder));
        return this;
    }

    /// <summary>
    /// Adds a database-scope <c>GRANT … TO …</c> emission. The
    /// <paramref name="permission"/> is the camel-case DACFx token (e.g.
    /// <c>"ViewAnyColumnEncryptionKeyDefinition"</c>); the loader splits
    /// each camel-case boundary into a space-separated uppercase
    /// permission name for the simulator's GRANT parser.
    /// </summary>
    public BacpacBuilder Grant(string permission, string grantee)
    {
        _permissions.Add(new PermissionDef("Grant", permission, grantee));
        return this;
    }

    /// <summary>
    /// Emits an <c>SqlIndex</c> element whose IndexedObject points at a view
    /// — an indexed view. The referenced view must be created (via
    /// <see cref="View"/>) <c>WITH SCHEMABINDING</c>. Defaults to
    /// <c>IsUnique=True IsClustered=True</c> (the required first index on a
    /// view); the loader emits <c>CREATE UNIQUE CLUSTERED INDEX … ON
    /// &lt;view&gt;</c>.
    /// </summary>
    public BacpacBuilder IndexOnView(string viewSchema, string viewName, string indexName, string[] columns, bool isUnique = true, bool isClustered = true)
    {
        _viewIndexes.Add(new ViewIndexDef(viewSchema, viewName, indexName, columns, isUnique, isClustered));
        return this;
    }

    /// <summary>
    /// Adds a user-defined data type (alias type) over a built-in
    /// (<c>CREATE TYPE [schema].[name] FROM &lt;builtin&gt; [NULL|NOT NULL]</c>).
    /// Reference the alias in subsequent columns by passing its bracketed
    /// 2-part name as the <c>sqlType</c> argument to
    /// <see cref="TableBuilder.Column"/>.
    /// </summary>
    public BacpacBuilder UserDefinedDataType(string schemaName, string typeName, string baseType, bool nullable = true)
    {
        _ = _schemas.Add(schemaName);
        _uddts.Add(new UserDefinedDataTypeDef(schemaName, typeName, baseType, nullable));
        return this;
    }

    /// <summary>
    /// Adds a <c>CREATE XML SCHEMA COLLECTION [schema].[name] AS N'…'</c>
    /// emission. <paramref name="xsdText"/> is the raw XSD source (no <c>N'…'</c>
    /// wrapping) — the builder wraps it into the complete T-SQL string literal
    /// DACFx stores in the <c>SchemaExpression</c> property's CDATA body (N
    /// prefix, quotes, doubled embedded quotes). Reference the collection from a
    /// typed-xml column via <see cref="TableBuilder.Column"/>'s
    /// <c>xmlSchemaCollection</c> argument.
    /// </summary>
    public BacpacBuilder XmlSchemaCollection(string schemaName, string collectionName, string xsdText)
    {
        _ = _schemas.Add(schemaName);
        _xmlSchemaCollections.Add(new XmlSchemaCollectionDef(schemaName, collectionName, xsdText));
        return this;
    }

    /// <summary>
    /// Emits a primary <c>SqlXmlIndex</c> element
    /// (<c>CREATE PRIMARY XML INDEX name ON table(col)</c>). The table must
    /// carry a clustered PK and an xml <paramref name="column"/>.
    /// </summary>
    public BacpacBuilder PrimaryXmlIndex(string schemaName, string tableName, string indexName, string column)
    {
        _xmlIndexes.Add(new XmlIndexDef(schemaName, tableName, indexName, column, IsPrimary: true, null, null));
        return this;
    }

    /// <summary>
    /// Emits a secondary <c>SqlXmlIndex</c> element
    /// (<c>CREATE XML INDEX name ON table(col) USING XML INDEX primary FOR
    /// PATH|PROPERTY|VALUE</c>). <paramref name="usage"/> is DACFx's enum:
    /// 1 = PATH, 2 = PROPERTY, 3 = VALUE.
    /// </summary>
    public BacpacBuilder SecondaryXmlIndex(string schemaName, string tableName, string indexName, string column, string usingPrimaryIndexName, int usage)
    {
        _xmlIndexes.Add(new XmlIndexDef(schemaName, tableName, indexName, column, IsPrimary: false, usingPrimaryIndexName, usage));
        return this;
    }

    /// <summary>
    /// Emits a <c>SqlFullTextCatalog</c> element
    /// (<c>CREATE FULLTEXT CATALOG name WITH ACCENT_SENSITIVITY = … [AS DEFAULT]
    /// AUTHORIZATION owner</c>).
    /// </summary>
    public BacpacBuilder FullTextCatalog(string name, bool accentSensitive = true, bool isDefault = true, string owner = "dbo")
    {
        _fullTextCatalogs.Add(new FullTextCatalogDef(name, accentSensitive, isDefault, owner));
        return this;
    }

    /// <summary>
    /// Emits a <c>SqlFullTextIndex</c> element
    /// (<c>CREATE FULLTEXT INDEX ON table (col [TYPE COLUMN t] LANGUAGE n, …)
    /// KEY INDEX keyName ON catalog</c>). Each column is
    /// <c>(column, languageId, typeColumn?)</c>.
    /// </summary>
    public BacpacBuilder FullTextIndex(string schemaName, string tableName, string catalogName, string keyIndexName, params (string Column, int LanguageId, string? TypeColumn)[] columns)
    {
        var cols = columns.Select(c => new FullTextIndexColumnDef(c.Column, c.LanguageId, c.TypeColumn)).ToArray();
        _fullTextIndexes.Add(new FullTextIndexDef(schemaName, tableName, catalogName, keyIndexName, cols));
        return this;
    }

    /// <summary>
    /// Emits a <c>SqlPartitionFunction</c> element. The loader treats this
    /// as a silent no-op (filegroup-mapping metadata with no semantic effect
    /// on the simulator's row-store-only storage); the test surface for
    /// this builder method is "presence of the element doesn't add a
    /// Skipped entry".
    /// </summary>
    public BacpacBuilder PartitionFunction(string name)
    {
        _silentlySkipped.Add(("SqlPartitionFunction", $"[{name}]"));
        return this;
    }

    /// <summary>
    /// Emits a <c>SqlPartitionScheme</c> element. Same silent-skip path as
    /// <see cref="PartitionFunction"/>.
    /// </summary>
    public BacpacBuilder PartitionScheme(string name)
    {
        _silentlySkipped.Add(("SqlPartitionScheme", $"[{name}]"));
        return this;
    }

    /// <summary>
    /// Emits a <c>SqlColumnStoreIndex</c> element. Read-optimization shape
    /// over the same row data; same silent-skip path as
    /// <see cref="PartitionFunction"/>.
    /// </summary>
    public BacpacBuilder ColumnStoreIndex(string name)
    {
        _silentlySkipped.Add(("SqlColumnStoreIndex", $"[{name}]"));
        return this;
    }

    /// <summary>
    /// Emits a top-level element with an arbitrary, loader-unrecognized
    /// <paramref name="elementType"/>. The dispatcher walks every phase
    /// without matching → lands on Skipped with "Element type not yet
    /// handled by the loader."
    /// </summary>
    public BacpacBuilder UnknownTopLevelElement(string elementType, string elementName)
    {
        _unknownElements.Add((elementType, elementName));
        return this;
    }

    /// <summary>
    /// Emits a <c>SqlFilegroup</c> element. The loader registers the filegroup
    /// on the target database (no Skipped entry) so
    /// <c>sys.filegroups</c> / <c>sys.data_spaces</c> surface it; there's no
    /// physical file model, so table / index placement is unaffected.
    /// </summary>
    public BacpacBuilder Filegroup(string name)
    {
        _silentlySkipped.Add(("SqlFilegroup", $"[{name}]"));
        return this;
    }

    /// <summary>
    /// Adds a CREATE TRIGGER … ON DATABASE … emission (database-scope DDL
    /// trigger). The loader dispatches through the same programmable-object
    /// path as DML triggers but routes to <c>Database.DdlTriggers</c> via
    /// the CREATE TRIGGER … ON DATABASE grammar. Body is wrapped in the
    /// HeaderContents / BodyScript envelope.
    /// </summary>
    public BacpacBuilder DatabaseDdlTrigger(string triggerName, string createStatement)
    {
        _programmableObjects.Add(new ProgrammableObjectDef("SqlDatabaseDdlTrigger", "BodyScript", "", triggerName, createStatement, FunctionBodyHost: false));
        return this;
    }

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
    /// <c>Simulation.ImportBacpac(Stream, out BacpacImportResult)</c>.
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

        if (_databaseOptions.Count > 0)
        {
            var options = new XElement(ns + "Element",
                new XAttribute("Type", "SqlDatabaseOptions"),
                new XAttribute("Name", "[simulated]"));
            foreach (var (name, value) in _databaseOptions)
            {
                options.Add(new XElement(ns + "Property",
                    new XAttribute("Name", name),
                    new XAttribute("Value", value)));
            }
            model.Add(options);
        }

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

        // Constraints are sibling top-level Elements (not nested inside the
        // SqlTable element). The loader's multi-phase dispatch orders them
        // after the table-creation phase regardless of their document
        // position, so emitting them after every table is fine.
        foreach (var table in _tables)
        {
            foreach (var constraint in table.ConstraintElements(ns))
                model.Add(constraint);
        }

        foreach (var table in _tables)
        {
            foreach (var index in table.IndexElements(ns))
                model.Add(index);
        }

        foreach (var vi in _viewIndexes)
            model.Add(BuildViewIndexElement(ns, vi));

        foreach (var uddt in _uddts)
            model.Add(BuildUddtElement(ns, uddt));

        foreach (var xsc in _xmlSchemaCollections)
            model.Add(BuildXmlSchemaCollectionElement(ns, xsc));

        foreach (var cat in _fullTextCatalogs)
            model.Add(BuildFullTextCatalogElement(ns, cat));

        foreach (var fti in _fullTextIndexes)
            model.Add(BuildFullTextIndexElement(ns, fti));

        // Primary XML indexes before secondary ones (a secondary references
        // its primary), mirroring DACFx's name-sorted document order.
        foreach (var xi in _xmlIndexes.Where(x => x.IsPrimary))
            model.Add(BuildXmlIndexElement(ns, xi));
        foreach (var xi in _xmlIndexes.Where(x => !x.IsPrimary))
            model.Add(BuildXmlIndexElement(ns, xi));

        foreach (var seq in _sequences)
            model.Add(BuildSequenceElement(ns, seq));

        foreach (var role in _roles)
            model.Add(BuildRoleElement(ns, role, _roleAuthorizers.GetValueOrDefault(role)));

        foreach (var tt in _tableTypes)
            model.Add(BuildTableTypeElement(ns, tt));

        foreach (var prog in _programmableObjects)
            model.Add(BuildProgrammableObjectElement(ns, prog));

        foreach (var perm in _permissions)
            model.Add(BuildPermissionElement(ns, perm));

        foreach (var ep in _extendedProperties)
            model.Add(BuildExtendedPropertyElement(ns, ep));

        // Silent-skip elements: the loader recognizes the Type and runs an
        // empty action. Name attribute is required by the dispatcher's
        // element-iteration contract but isn't parsed for these types.
        foreach (var (elementType, elementName) in _silentlySkipped)
        {
            model.Add(new XElement(ns + "Element",
                new XAttribute("Type", elementType),
                new XAttribute("Name", elementName)));
        }

        // Unknown-type elements: dispatcher fails to claim them in any
        // phase → record on Skipped.
        foreach (var (elementType, elementName) in _unknownElements)
        {
            model.Add(new XElement(ns + "Element",
                new XAttribute("Type", elementType),
                new XAttribute("Name", elementName)));
        }

        var rootElement = new XElement(ns + "DataSchemaModel", model);
        if (_dspName is not null)
            rootElement.SetAttributeValue("DspName", _dspName);
        var doc = new XDocument(rootElement);
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
        var sqlType = column.SqlType.Trim();
        var openParen = sqlType.IndexOf('(');
        var baseName = (openParen < 0 ? sqlType : sqlType[..openParen]).Trim().ToLowerInvariant();
        var args = openParen < 0 ? "" : sqlType[(openParen + 1)..^1].Trim();
        var isMax = string.Equals(args, "max", StringComparison.OrdinalIgnoreCase);

        switch (baseName)
        {
            case "int":
                EncodeFixedRaw(stream, column.Nullable, 4, value, buf => BinaryPrimitives.WriteInt32LittleEndian(buf, Convert.ToInt32(value)));
                return;
            case "bigint":
                EncodeFixedRaw(stream, column.Nullable, 8, value, buf => BinaryPrimitives.WriteInt64LittleEndian(buf, Convert.ToInt64(value)));
                return;
            case "smallint":
                EncodeFixedRaw(stream, column.Nullable, 2, value, buf => BinaryPrimitives.WriteInt16LittleEndian(buf, Convert.ToInt16(value)));
                return;
            case "tinyint":
                EncodeFixedRaw(stream, column.Nullable, 1, value, buf => buf[0] = Convert.ToByte(value));
                return;
            case "bit":
                // bit is always 1-byte length-prefixed regardless of nullability.
                if (value is null) { stream.WriteByte(0xFF); return; }
                stream.WriteByte(0x01);
                stream.WriteByte((bool)value ? (byte)1 : (byte)0);
                return;
            case "datetime":
                EncodeFixedRaw(stream, column.Nullable, 8, value, buf => WriteDateTime(buf, (DateTime)value!));
                return;
            case "date":
                EncodeFixedRaw(stream, column.Nullable, 3, value, buf => WriteDate(buf, value is DateOnly d ? d : DateOnly.FromDateTime((DateTime)value!)));
                return;
            case "uniqueidentifier":
                EncodeOneByteLengthPrefixed(stream, 16, value, buf => ((Guid)value!).TryWriteBytes(buf));
                return;
            case "decimal" or "numeric":
                EncodeDecimal(stream, (decimal?)value, args);
                return;
            case "money":
                EncodeFixedRaw(stream, column.Nullable, 8, value, buf => WriteMoney(buf, (decimal)value!));
                return;
            case "smallmoney":
                EncodeFixedRaw(stream, column.Nullable, 4, value, buf => BinaryPrimitives.WriteInt32LittleEndian(buf, checked((int)((decimal)value! * 10000m))));
                return;
            case "datetime2":
                EncodeDateTime2(stream, column.Nullable, args, value);
                return;
            case "time":
                EncodeTime(stream, column.Nullable, args, value);
                return;
            case "datetimeoffset":
                EncodeDateTimeOffset(stream, column.Nullable, args, value);
                return;
            case "smalldatetime":
                EncodeFixedRaw(stream, column.Nullable, 4, value, buf => WriteSmallDateTime(buf, (DateTime)value!));
                return;
            case "varchar" or "nvarchar" or "char" or "nchar" or "sysname" when !isMax:
                Encode2BytePrefixedString(stream, value as string);
                return;
            case "varbinary" or "binary" when !isMax:
                Encode2BytePrefixedBytes(stream, value as byte[]);
                return;
            case "varchar" or "nvarchar" when isMax:
                Encode8BytePrefixedString(stream, value as string);
                return;
            case "varbinary" when isMax:
                Encode8BytePrefixedBytes(stream, value as byte[]);
                return;
            case "xml":
                Encode8BytePrefixedString(stream, value as string);
                return;
            case "geography" or "geometry":
                // Geography / geometry values are pre-encoded into Microsoft's
                // spatial UDT binary (typically via MakeGeographyPoint /
                // MakeGeometryPoint) and serialized with the same 8-byte
                // length-prefix shape as varbinary(MAX).
                Encode8BytePrefixedBytes(stream, value as byte[]);
                return;
            case "hierarchyid":
                // Hierarchyid values are pre-encoded via MakeHierarchyIdBytes
                // (bit-packed OrdPath form).
                Encode8BytePrefixedBytes(stream, value as byte[]);
                return;
            default:
                throw new NotSupportedException($"BacpacBuilder doesn't model BCP encoding for type '{column.SqlType}' yet.");
        }
    }

    /// <summary>
    /// Builds Microsoft's geography simple-point wire form (22 bytes:
    /// 4-byte SRID + 1-byte version + 1-byte properties (IsSinglePoint) +
    /// 16-byte lat/long doubles). Pass as a row value for a column of
    /// type <c>geography</c>; the loader's WKB decoder converts back
    /// to a WKT <c>POINT (long lat)</c> string for the simulator's
    /// <c>SpatialSqlType</c> storage.
    /// </summary>
    public static byte[] MakeGeographyPoint(double latitude, double longitude, int srid = 4326)
        => MakeSimplePointBytes(srid, latitude, longitude);

    /// <summary>
    /// Builds Microsoft's geometry simple-point wire form (axis order
    /// is (x, y) — the simulator's decoder honors the inversion vs
    /// geography's (lat, long) ordering).
    /// </summary>
    public static byte[] MakeGeometryPoint(double x, double y, int srid = 0)
        => MakeSimplePointBytes(srid, x, y);

    /// <summary>
    /// Builds the hierarchyid OrdPath wire bytes for a path like
    /// <c>"/1/2/"</c>. Each segment <paramref name="ordinals"/> is bit-encoded
    /// using SQL Server's prefix code (range [0..3] uses 5 bits, [4..7] 6 bits,
    /// [8..15] 7 bits, [16..79] 12 bits). Returns empty array for the root
    /// path. Negative ordinals + ordinals ≥ 80 are deferred.
    /// </summary>
    public static byte[] MakeHierarchyIdBytes(params int[] ordinals)
    {
        if (ordinals.Length == 0)
            return [];
        var bits = new List<bool>();
        foreach (var ord in ordinals)
            AppendOrdinalBits(bits, ord);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return bytes;
    }

    private static void AppendOrdinalBits(List<bool> bits, int ord)
    {
        if (ord < 0)
            throw new NotSupportedException("MakeHierarchyIdBytes doesn't yet support negative ordinals.");
        if (ord < 4)
        {
            // Range [0..3]: prefix 01, 2 value bits, terminator 1.
            bits.Add(false);
            bits.Add(true);
            bits.Add((ord & 2) != 0);
            bits.Add((ord & 1) != 0);
            bits.Add(true);
        }
        else if (ord < 8)
        {
            // Range [4..7]: prefix 100, 2 value bits, terminator 1.
            bits.Add(true); bits.Add(false); bits.Add(false);
            var v = ord - 4;
            bits.Add((v & 2) != 0);
            bits.Add((v & 1) != 0);
            bits.Add(true);
        }
        else if (ord < 16)
        {
            // Range [8..15]: prefix 101, 3 value bits, terminator 1.
            bits.Add(true); bits.Add(false); bits.Add(true);
            var v = ord - 8;
            bits.Add((v & 4) != 0);
            bits.Add((v & 2) != 0);
            bits.Add((v & 1) != 0);
            bits.Add(true);
        }
        else if (ord < 80)
        {
            // Range [16..79]: prefix 110, 2 high bits, static 0, 1 mid bit,
            // static 1, 3 low bits, terminator 1. The decoder reconstructs
            // value = (high2 << 4) | (midHigh << 3) | low3, then adds 16.
            bits.Add(true); bits.Add(true); bits.Add(false);
            var v = ord - 16;
            var high2 = (v >> 4) & 0x3;
            var midHigh = (v >> 3) & 0x1;
            var low3 = v & 0x7;
            bits.Add((high2 & 2) != 0);
            bits.Add((high2 & 1) != 0);
            bits.Add(false);
            bits.Add(midHigh != 0);
            bits.Add(true);
            bits.Add((low3 & 4) != 0);
            bits.Add((low3 & 2) != 0);
            bits.Add((low3 & 1) != 0);
            bits.Add(true);
        }
        else
        {
            throw new NotSupportedException($"MakeHierarchyIdBytes doesn't yet support ordinal {ord} (>= 80).");
        }
    }

    /// <summary>
    /// Builds Microsoft's geography full-shape wire form for a single-ring
    /// polygon. The ring is given as <c>(latitude, longitude)</c> pairs and
    /// must close (last point equals first); the loader's
    /// <c>SpatialBinaryCodec</c> reads back <c>POLYGON ((long lat, …))</c>
    /// WKT with axis order inverted vs storage. Use for exercising the
    /// full-shape decoder path (numPoints/figures/shapes tables) that the
    /// simple-point shortcut bypasses.
    /// </summary>
    public static byte[] MakeGeographyPolygon(int srid, params (double Latitude, double Longitude)[] ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        return ring.Length < 4
            ? throw new ArgumentException("Ring must have at least 4 points (3 distinct corners closed back to start).", nameof(ring))
            : MakeSinglePolygonBytes(srid, ring);
    }

    private static byte[] MakeSinglePolygonBytes(int srid, (double Latitude, double Longitude)[] ring)
    {
        // 6-byte header + (4 + n*16) points + (4 + 1*5) figures + (4 + 1*9) shapes.
        var size = 6 + 4 + (ring.Length * 16) + 4 + 5 + 4 + 9;
        var buf = new byte[size];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[..4], srid);
        span[4] = 0x01; // version
        span[5] = 0x00; // properties (no IsSinglePoint, no IsSingleLineString, no Z/M)
        var pos = 6;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), ring.Length);
        pos += 4;
        foreach (var (lat, lon) in ring)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos, 8), lat);
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(pos + 8, 8), lon);
            pos += 16;
        }
        // 1 figure: 1-byte attribute (decoder ignores) + 4-byte pointOffset = 0.
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), 1);
        pos += 4;
        span[pos] = 0x02; // attribute — ExteriorRing-equivalent; decoder ignores
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos + 1, 4), 0);
        pos += 5;
        // 1 shape: parent=-1, figureOffset=0, type=Polygon (0x03).
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), 1);
        pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), -1);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos + 4, 4), 0);
        span[pos + 8] = 0x03;
        return buf;
    }

    private static byte[] MakeSimplePointBytes(int srid, double first, double second)
    {
        var buf = new byte[22];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), srid);
        buf[4] = 0x01;
        buf[5] = 0x08;
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(6, 8), first);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(14, 8), second);
        return buf;
    }

    private static void EncodeFixedRaw(Stream stream, bool nullable, int width, object? value, SpanWriter writer)
    {
        if (nullable)
        {
            if (value is null)
            {
                stream.WriteByte(0xFF);
                return;
            }
            stream.WriteByte((byte)width);
        }
        Span<byte> buf = stackalloc byte[width];
        writer(buf);
        stream.Write(buf);
    }

    private static void EncodeOneByteLengthPrefixed(Stream stream, int width, object? value, SpanWriter writer)
    {
        if (value is null)
        {
            stream.WriteByte(0xFF);
            return;
        }
        stream.WriteByte((byte)width);
        Span<byte> buf = stackalloc byte[width];
        writer(buf);
        stream.Write(buf);
    }

    private static void Encode2BytePrefixedString(Stream stream, string? value)
    {
        Span<byte> prefix = stackalloc byte[2];
        if (value is null)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(prefix, 0xFFFF);
            stream.Write(prefix);
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(value);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix, checked((ushort)bytes.Length));
        stream.Write(prefix);
        stream.Write(bytes);
    }

    private static void Encode2BytePrefixedBytes(Stream stream, byte[]? value)
    {
        Span<byte> prefix = stackalloc byte[2];
        if (value is null)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(prefix, 0xFFFF);
            stream.Write(prefix);
            return;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(prefix, checked((ushort)value.Length));
        stream.Write(prefix);
        stream.Write(value);
    }

    private static void Encode8BytePrefixedString(Stream stream, string? value)
    {
        Span<byte> prefix = stackalloc byte[8];
        if (value is null)
        {
            BinaryPrimitives.WriteInt64LittleEndian(prefix, -1);
            stream.Write(prefix);
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(value);
        BinaryPrimitives.WriteInt64LittleEndian(prefix, bytes.Length);
        stream.Write(prefix);
        stream.Write(bytes);
    }

    private static void Encode8BytePrefixedBytes(Stream stream, byte[]? value)
    {
        Span<byte> prefix = stackalloc byte[8];
        if (value is null)
        {
            BinaryPrimitives.WriteInt64LittleEndian(prefix, -1);
            stream.Write(prefix);
            return;
        }
        BinaryPrimitives.WriteInt64LittleEndian(prefix, value.Length);
        stream.Write(prefix);
        stream.Write(value);
    }

    /// <summary>
    /// <c>smalldatetime</c>: 2-byte LE days since 1900-01-01 + 2-byte LE
    /// minutes since midnight.
    /// </summary>
    private static void WriteSmallDateTime(Span<byte> buf, DateTime dt)
    {
        var epoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[..2], checked((ushort)dt.Date.Subtract(epoch).Days));
        BinaryPrimitives.WriteUInt16LittleEndian(buf[2..4], checked((ushort)(dt.TimeOfDay.Ticks / TimeSpan.TicksPerMinute)));
    }

    private static void WriteDateTime(Span<byte> buf, DateTime dt)
    {
        // SQL Server datetime: 4-byte int32 days since 1900-01-01 + 4-byte uint32 1/300-second ticks since midnight.
        var epoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var diff = dt - epoch;
        var days = diff.Days;
        var timeTicks = diff.Ticks - (TimeSpan.TicksPerDay * days);
        var ticks300 = (uint)(((timeTicks * 300) + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], days);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..8], ticks300);
    }

    /// <summary>
    /// Encodes a decimal in DACFx's BCP wire format:
    /// <c>[1-byte length N][precision][scale][sign][N-3 mantissa bytes LE]</c>.
    /// Mantissa width comes from the precision bucket (≤9 → 4, ≤19 → 8, ≤28 → 12,
    /// ≤38 → 16 bytes). <c>System.Decimal.GetBits</c> covers up to 28 digits;
    /// values needing more precision fall outside the simulator's storage model.
    /// </summary>
    private static void EncodeDecimal(Stream stream, decimal? value, string args)
    {
        if (value is null)
        {
            stream.WriteByte(0xFF);
            return;
        }
        var commaIndex = args.IndexOf(',');
        var precision = commaIndex < 0
            ? int.Parse(args, System.Globalization.CultureInfo.InvariantCulture)
            : int.Parse(args[..commaIndex].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var declaredScale = commaIndex < 0 ? 0 : int.Parse(args[(commaIndex + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var mantissaWidth = precision switch
        {
            <= 9 => 4,
            <= 19 => 8,
            <= 28 => 12,
            _ => 16,
        };

        var d = value.Value;
        var bits = decimal.GetBits(d);
        var actualScale = (bits[3] >> 16) & 0x7F;
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;

        // Rescale to declared scale by multiplying / dividing the mantissa.
        var rescaled = d;
        if (actualScale != declaredScale)
        {
            // Adjust by multiplying by 10^(declaredScale - actualScale)
            var diff = declaredScale - actualScale;
            for (var i = 0; i < diff; i++) rescaled *= 10m;
            for (var i = 0; i < -diff; i++) rescaled /= 10m;
            bits = decimal.GetBits(rescaled);
            negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        }

        Span<byte> mantissa = stackalloc byte[mantissaWidth];
        BinaryPrimitives.WriteUInt32LittleEndian(mantissa[..4], (uint)bits[0]);
        if (mantissaWidth >= 8)
            BinaryPrimitives.WriteUInt32LittleEndian(mantissa[4..8], (uint)bits[1]);
        if (mantissaWidth >= 12)
            BinaryPrimitives.WriteUInt32LittleEndian(mantissa[8..12], (uint)bits[2]);

        var payloadLength = 3 + mantissaWidth;
        stream.WriteByte((byte)payloadLength);
        stream.WriteByte((byte)precision);
        stream.WriteByte((byte)declaredScale);
        stream.WriteByte(negative ? (byte)0 : (byte)1);
        stream.Write(mantissa);
    }

    private static void WriteMoney(Span<byte> buf, decimal value)
    {
        // money: scaled = value * 10000, stored as int64 split high (signed)
        // / low (unsigned) — bytes[0..4] = high 32 bits LE, [4..8] = low.
        var scaled = (long)(value * 10000m);
        var high = (int)(scaled >> 32);
        var low = (uint)(scaled & 0xFFFFFFFFu);
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], high);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..8], low);
    }

    /// <summary>
    /// Encodes <c>datetime2(N)</c> the way DacFx does: always the maximum
    /// 8-byte width — 5-byte LE count of 100-nanosecond units since midnight
    /// plus 3-byte LE days-since-0001-01-01 — with the value scaled to 7
    /// fractional digits whatever the column's declared precision.
    /// </summary>
    private static void EncodeDateTime2(Stream stream, bool nullable, string args, object? value)
    {
        _ = args;
        if (nullable)
        {
            if (value is null)
            {
                stream.WriteByte(0xFF);
                return;
            }
            stream.WriteByte(8);
        }
        WriteDateTime2Body(stream, (DateTime)value!);
    }

    /// <summary>
    /// Encodes <c>datetimeoffset(N)</c> as DacFx does: the maximum 10-byte
    /// width, whose date and time carry the instant in <b>UTC</b>, followed by
    /// a 2-byte LE signed minutes-from-UTC offset.
    /// </summary>
    private static void EncodeDateTimeOffset(Stream stream, bool nullable, string args, object? value)
    {
        _ = args;
        if (nullable)
        {
            if (value is null)
            {
                stream.WriteByte(0xFF);
                return;
            }
            stream.WriteByte(10);
        }
        var dto = (DateTimeOffset)value!;
        WriteDateTime2Body(stream, dto.UtcDateTime);
        Span<byte> offsetBytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(offsetBytes, checked((short)dto.Offset.TotalMinutes));
        stream.Write(offsetBytes);
    }

    /// <summary>The 8-byte datetime2 body shared by datetime2 and datetimeoffset.</summary>
    private static void WriteDateTime2Body(Stream stream, DateTime value)
    {
        var days = value.Date.Subtract(new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)).Days;
        var ticks = value.TimeOfDay.Ticks;
        Span<byte> buf = stackalloc byte[8];
        for (var i = 0; i < 5; i++)
            buf[i] = (byte)(ticks >> (8 * i));
        buf[5] = (byte)days;
        buf[6] = (byte)(days >> 8);
        buf[7] = (byte)(days >> 16);
        stream.Write(buf);
    }

    /// <summary>
    /// Encodes <c>time(N)</c> as DacFx does: the maximum 5-byte width holding
    /// a LE count of 100-nanosecond units since midnight, scaled to 7
    /// fractional digits regardless of declared precision.
    /// </summary>
    private static void EncodeTime(Stream stream, bool nullable, string args, object? value)
    {
        _ = args;
        if (nullable)
        {
            if (value is null)
            {
                stream.WriteByte(0xFF);
                return;
            }
            stream.WriteByte(5);
        }
        var span = value is TimeOnly to ? to.ToTimeSpan() : (TimeSpan)value!;
        Span<byte> buf = stackalloc byte[5];
        for (var i = 0; i < 5; i++)
            buf[i] = (byte)(span.Ticks >> (8 * i));
        stream.Write(buf);
    }

    private static void WriteDate(Span<byte> buf, DateOnly d)
    {
        // SQL Server date: 3-byte LE days since 0001-01-01.
        var days = d.DayNumber;
        buf[0] = (byte)(days & 0xFF);
        buf[1] = (byte)((days >> 8) & 0xFF);
        buf[2] = (byte)((days >> 16) & 0xFF);
    }

    private delegate void SpanWriter(Span<byte> buf);

    private static XElement BuildProgrammableObjectElement(XNamespace ns, ProgrammableObjectDef prog)
    {
        // DDL triggers (database-scope) carry a 1-part bracketed name with
        // no schema prefix; every other programmable object is schema-qualified.
        var qualifiedName = string.IsNullOrEmpty(prog.SchemaName)
            ? $"[{prog.ObjectName}]"
            : prog.ParentTable is null
                ? $"[{prog.SchemaName}].[{prog.ObjectName}]"
                : $"[{prog.SchemaName}].[{prog.ParentTable}].[{prog.ObjectName}]";

        var element = new XElement(ns + "Element",
            new XAttribute("Type", prog.ElementType),
            new XAttribute("Name", qualifiedName));

        // Header lives on either the element itself or the nested
        // SqlScriptFunctionImplementation (for scalar / multi-stmt TVF).
        // Use a minimal "-- header" marker — the loader concatenates
        // header + "\n" + body before parsing, so the content just needs
        // to be a valid T-SQL comment that won't break the statement.
        XElement bodyHost;
        if (prog.FunctionBodyHost)
        {
            bodyHost = new XElement(ns + "Element",
                new XAttribute("Type", "SqlScriptFunctionImplementation"));
            element.Add(new XElement(ns + "Relationship",
                new XAttribute("Name", "FunctionBody"),
                new XElement(ns + "Entry", bodyHost)));
        }
        else
        {
            bodyHost = element;
        }

        bodyHost.Add(new XElement(ns + "Annotation",
            new XAttribute("Type", "SysCommentsObjectAnnotation"),
            new XElement(ns + "Property",
                new XAttribute("Name", "HeaderContents"),
                new XAttribute("Value", "-- header"))));
        bodyHost.Add(new XElement(ns + "Property",
            new XAttribute("Name", prog.BodyPropertyName),
            new XElement(ns + "Value", new XCData(prog.CreateStatement))));

        return element;
    }
}

/// <summary>
/// Internal capture of a programmable-object emission targeted at a
/// specific element-type emitter inside the loader.
/// </summary>
internal sealed record ProgrammableObjectDef(
    string ElementType,
    string BodyPropertyName,
    string SchemaName,
    string ObjectName,
    string CreateStatement,
    bool FunctionBodyHost,
    string? ParentTable = null);

internal enum ExtendedPropertyHost { AutoDetect, Index, Constraint, Unknown, DdlTrigger, Filegroup }

internal sealed record ExtendedPropertyDef(string? SchemaName, string? TableName, string? ColumnName, string PropertyName, string Value, ExtendedPropertyHost Host = ExtendedPropertyHost.AutoDetect)
{
    public string? UnknownHostKind { get; init; }
}

internal sealed record SequenceDef(string SchemaName, string SequenceName, string SqlType, long StartValue, long Increment);

internal sealed record TableTypeDef(string SchemaName, string TypeName, TableBuilder Body);

internal sealed record PermissionDef(string Action, string Permission, string Grantee);

internal sealed record ViewIndexDef(string ViewSchema, string ViewName, string IndexName, string[] KeyColumns, bool IsUnique = true, bool IsClustered = true);

internal sealed record XmlIndexDef(string SchemaName, string TableName, string IndexName, string Column, bool IsPrimary, string? UsingPrimaryIndexName, int? PrimaryXmlIndexUsage);

internal sealed record FullTextCatalogDef(string Name, bool AccentSensitive, bool IsDefault, string Owner);

internal sealed record FullTextIndexColumnDef(string Column, int LanguageId, string? TypeColumn);

internal sealed record FullTextIndexDef(string SchemaName, string TableName, string CatalogName, string KeyIndexName, FullTextIndexColumnDef[] Columns);

internal sealed record UserDefinedDataTypeDef(string SchemaName, string TypeName, string BaseType, bool Nullable);

internal sealed record XmlSchemaCollectionDef(string SchemaName, string CollectionName, string XsdText);

sealed partial class BacpacBuilder
{
    private static XElement BuildUddtElement(XNamespace ns, UserDefinedDataTypeDef uddt)
    {
        // The UDDT element IS the TypeSpecifier — TranslateTypeSpecifier is
        // called on it directly, looking for "Type" relationship + Length /
        // Precision / Scale / IsMax properties.
        var (baseName, length, precision, scale, isMax) = ParseUddtBaseType(uddt.BaseType);
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlUserDefinedDataType"),
            new XAttribute("Name", $"[{uddt.SchemaName}].[{uddt.TypeName}]"));

        element.Add(new XElement(ns + "Property",
            new XAttribute("Name", "IsNullable"),
            new XAttribute("Value", uddt.Nullable ? "True" : "False")));

        element.Add(new XElement(ns + "Relationship",
            new XAttribute("Name", "Type"),
            new XElement(ns + "Entry",
                new XElement(ns + "References",
                    new XAttribute("Name", $"[{baseName}]"),
                    new XAttribute("ExternalSource", "BuiltIns")))));

        if (isMax)
            element.Add(new XElement(ns + "Property", new XAttribute("Name", "IsMax"), new XAttribute("Value", "True")));
        if (length is not null)
            element.Add(new XElement(ns + "Property", new XAttribute("Name", "Length"), new XAttribute("Value", length)));
        if (precision is not null)
            element.Add(new XElement(ns + "Property", new XAttribute("Name", "Precision"), new XAttribute("Value", precision)));
        if (scale is not null)
            element.Add(new XElement(ns + "Property", new XAttribute("Name", "Scale"), new XAttribute("Value", scale)));
        return element;
    }

    private static (string Base, string? Length, string? Precision, string? Scale, bool IsMax) ParseUddtBaseType(string sqlType)
    {
        var trimmed = sqlType.Trim();
        var openParen = trimmed.IndexOf('(');
        if (openParen < 0)
            return (trimmed.ToLowerInvariant(), null, null, null, false);
        var baseName = trimmed[..openParen].Trim().ToLowerInvariant();
        var args = trimmed[(openParen + 1)..^1].Trim();
        if (string.Equals(args, "max", StringComparison.OrdinalIgnoreCase))
            return (baseName, null, null, null, true);
        var commaIndex = args.IndexOf(',');
        if (commaIndex < 0)
        {
            return baseName is "decimal" or "numeric"
                ? (baseName, null, args.Trim(), null, false)
                : (baseName, args.Trim(), null, null, false);
        }
        return (baseName, null, args[..commaIndex].Trim(), args[(commaIndex + 1)..].Trim(), false);
    }

    private static XElement BuildXmlSchemaCollectionElement(XNamespace ns, XmlSchemaCollectionDef xsc)
    {
        // DACFx stores the SchemaExpression as a complete N'…' string literal
        // in the property's CDATA body — N prefix, surrounding quotes, doubled
        // embedded quotes. Mirror that so the loader forwards it verbatim into
        // the AS clause.
        var literal = "N'" + xsc.XsdText.Replace("'", "''", StringComparison.Ordinal) + "'";
        return new XElement(ns + "Element",
            new XAttribute("Type", "SqlXmlSchemaCollection"),
            new XAttribute("Name", $"[{xsc.SchemaName}].[{xsc.CollectionName}]"),
            new XElement(ns + "Property",
                new XAttribute("Name", "SchemaExpression"),
                new XElement(ns + "Value", new XCData(literal))));
    }

    private static XElement BuildSequenceElement(XNamespace ns, SequenceDef seq)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlSequence"),
            new XAttribute("Name", $"[{seq.SchemaName}].[{seq.SequenceName}]"));

        element.Add(new XElement(ns + "Relationship",
            new XAttribute("Name", "TypeSpecifier"),
            new XElement(ns + "Entry",
                new XElement(ns + "Element",
                    new XAttribute("Type", "SqlTypeSpecifier"),
                    new XElement(ns + "Relationship",
                        new XAttribute("Name", "Type"),
                        new XElement(ns + "Entry",
                            new XElement(ns + "References",
                                new XAttribute("Name", $"[{seq.SqlType}]"),
                                new XAttribute("ExternalSource", "BuiltIns"))))))));

        element.Add(new XElement(ns + "Property",
            new XAttribute("Name", "StartValue"),
            new XAttribute("Value", seq.StartValue.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        element.Add(new XElement(ns + "Property",
            new XAttribute("Name", "Increment"),
            new XAttribute("Value", seq.Increment.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return element;
    }

    private static XElement BuildRoleElement(XNamespace ns, string roleName, string? authorizer)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlRole"),
            new XAttribute("Name", $"[{roleName}]"));
        if (!string.IsNullOrEmpty(authorizer))
        {
            element.Add(new XElement(ns + "Relationship",
                new XAttribute("Name", "Authorizer"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", $"[{authorizer}]")))));
        }
        return element;
    }

    private static XElement BuildTableTypeElement(XNamespace ns, TableTypeDef tt)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlTableType"),
            new XAttribute("Name", $"[{tt.SchemaName}].[{tt.TypeName}]"));

        var columnsRel = new XElement(ns + "Relationship",
            new XAttribute("Name", "Columns"));
        foreach (var column in tt.Body.Columns)
            columnsRel.Add(new XElement(ns + "Entry", tt.Body.TableTypeColumnElement(ns, column)));
        element.Add(columnsRel);

        // Optional PK constraint inside the table-type body. Looks for any
        // PrimaryKeyDef accumulated through TableBuilder.PrimaryKey.
        var pks = tt.Body.Constraints.OfType<PrimaryKeyDef>().ToList();
        if (pks.Count > 0)
        {
            var constraintsRel = new XElement(ns + "Relationship",
                new XAttribute("Name", "Constraints"));
            foreach (var pk in pks)
            {
                var pkElement = new XElement(ns + "Element",
                    new XAttribute("Type", "SqlTableTypePrimaryKeyConstraint"));
                var specs = new XElement(ns + "Relationship",
                    new XAttribute("Name", "ColumnSpecifications"));
                foreach (var col in pk.Columns)
                {
                    specs.Add(new XElement(ns + "Entry",
                        new XElement(ns + "Element",
                            new XAttribute("Type", "SqlTableTypeIndexedColumnSpecification"),
                            new XElement(ns + "Relationship",
                                new XAttribute("Name", "Column"),
                                new XElement(ns + "Entry",
                                    new XElement(ns + "References",
                                        new XAttribute("Name", $"[{tt.SchemaName}].[{tt.TypeName}].[{col}]")))))));
                }
                pkElement.Add(specs);
                constraintsRel.Add(new XElement(ns + "Entry", pkElement));
            }
            element.Add(constraintsRel);
        }
        return element;
    }

    private static XElement BuildViewIndexElement(XNamespace ns, ViewIndexDef vi)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlIndex"),
            new XAttribute("Name", $"[{vi.ViewSchema}].[{vi.ViewName}].[{vi.IndexName}]"),
            new XElement(ns + "Relationship",
                new XAttribute("Name", "IndexedObject"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", $"[{vi.ViewSchema}].[{vi.ViewName}]")))));

        if (vi.IsUnique)
            element.Add(new XElement(ns + "Property", new XAttribute("Name", "IsUnique"), new XAttribute("Value", "True")));
        if (vi.IsClustered)
            element.Add(new XElement(ns + "Property", new XAttribute("Name", "IsClustered"), new XAttribute("Value", "True")));

        var columnSpecs = new XElement(ns + "Relationship",
            new XAttribute("Name", "ColumnSpecifications"));
        foreach (var col in vi.KeyColumns)
        {
            var spec = new XElement(ns + "Element",
                new XAttribute("Type", "SqlIndexedColumnSpecification"),
                new XElement(ns + "Relationship",
                    new XAttribute("Name", "Column"),
                    new XElement(ns + "Entry",
                        new XElement(ns + "References",
                            new XAttribute("Name", $"[{vi.ViewSchema}].[{vi.ViewName}].[{col}]")))));
            columnSpecs.Add(new XElement(ns + "Entry", spec));
        }
        element.Add(columnSpecs);
        return element;
    }

    private static XElement Relationship(XNamespace ns, string name, string reference) =>
        new(ns + "Relationship",
            new XAttribute("Name", name),
            new XElement(ns + "Entry",
                new XElement(ns + "References",
                    new XAttribute("Name", reference))));

    private static XElement BuildXmlIndexElement(XNamespace ns, XmlIndexDef xi)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlXmlIndex"),
            new XAttribute("Name", $"[{xi.SchemaName}].[{xi.TableName}].[{xi.IndexName}]"));
        if (xi.IsPrimary)
        {
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "IsPrimary"), new XAttribute("Value", "True")));
        }
        else
        {
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "PrimaryXmlIndexUsage"),
                new XAttribute("Value", xi.PrimaryXmlIndexUsage!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        element.Add(Relationship(ns, "Column", $"[{xi.SchemaName}].[{xi.TableName}].[{xi.Column}]"));
        element.Add(Relationship(ns, "IndexedObject", $"[{xi.SchemaName}].[{xi.TableName}]"));
        if (!xi.IsPrimary)
            element.Add(Relationship(ns, "UsingPrimaryXmlIndex", $"[{xi.SchemaName}].[{xi.TableName}].[{xi.UsingPrimaryIndexName}]"));
        return element;
    }

    private static XElement BuildFullTextCatalogElement(XNamespace ns, FullTextCatalogDef cat)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlFullTextCatalog"),
            new XAttribute("Name", $"[{cat.Name}]"),
            new XElement(ns + "Property",
                new XAttribute("Name", "IsAccentSensitive"), new XAttribute("Value", cat.AccentSensitive ? "True" : "False")));
        if (cat.IsDefault)
        {
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "IsDefault"), new XAttribute("Value", "True")));
        }
        element.Add(new XElement(ns + "Relationship",
            new XAttribute("Name", "Authorizer"),
            new XElement(ns + "Entry",
                new XElement(ns + "References",
                    new XAttribute("ExternalSource", "BuiltIns"),
                    new XAttribute("Name", $"[{cat.Owner}]")))));
        return element;
    }

    private static XElement BuildFullTextIndexElement(XNamespace ns, FullTextIndexDef fti)
    {
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlFullTextIndex"),
            new XAttribute("Name", $"[{fti.SchemaName}].[{fti.TableName}]"));
        element.Add(Relationship(ns, "Catalog", $"[{fti.CatalogName}]"));

        var columns = new XElement(ns + "Relationship", new XAttribute("Name", "Columns"));
        foreach (var col in fti.Columns)
        {
            var spec = new XElement(ns + "Element",
                new XAttribute("Type", "SqlFullTextIndexColumnSpecifier"),
                new XElement(ns + "Property",
                    new XAttribute("Name", "LanguageId"),
                    new XAttribute("Value", col.LanguageId.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                Relationship(ns, "Column", $"[{fti.SchemaName}].[{fti.TableName}].[{col.Column}]"));
            if (col.TypeColumn is not null)
                spec.Add(Relationship(ns, "TypeColumn", $"[{fti.SchemaName}].[{fti.TableName}].[{col.TypeColumn}]"));
            columns.Add(new XElement(ns + "Entry", spec));
        }
        element.Add(columns);
        element.Add(Relationship(ns, "IndexedObject", $"[{fti.SchemaName}].[{fti.TableName}]"));
        element.Add(Relationship(ns, "KeyName", $"[{fti.SchemaName}].[{fti.KeyIndexName}]"));
        return element;
    }

    private static XElement BuildPermissionElement(XNamespace ns, PermissionDef perm)
    {
        // Name shape: `[Action.PermissionCamelCase.Database].[grantee].[grantor]`.
        // Database-scope only — Object / Schema scope would consult a
        // SecuredObject relationship; defer until tests exercise it.
        var name = $"[{perm.Action}.{perm.Permission}.Database].[{perm.Grantee}].[dbo]";
        return new XElement(ns + "Element",
            new XAttribute("Type", "SqlPermissionStatement"),
            new XAttribute("Name", name),
            new XElement(ns + "Relationship",
                new XAttribute("Name", "Grantee"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", $"[{perm.Grantee}]")))));
    }

    private static XElement BuildExtendedPropertyElement(XNamespace ns, ExtendedPropertyDef ep)
    {
        // Host-kind: explicit override for Index / Constraint (Column-shaped
        // payloads collide with SqlColumn auto-detect); otherwise inferred
        // from which target-name slots are populated.
        var (hostKind, name, hostRef) = ep.Host switch
        {
            ExtendedPropertyHost.Index =>
                ("SqlIndexBase",
                 $"[SqlIndexBase].[{ep.SchemaName}].[{ep.TableName}].[{ep.ColumnName}].[{ep.PropertyName}]",
                 $"[{ep.SchemaName}].[{ep.TableName}].[{ep.ColumnName}]"),
            ExtendedPropertyHost.Constraint =>
                ("SqlConstraint",
                 $"[SqlConstraint].[{ep.SchemaName}].[{ep.TableName}].[{ep.PropertyName}]",
                 $"[{ep.SchemaName}].[{ep.TableName}]"),
            ExtendedPropertyHost.DdlTrigger =>
                ("SqlDatabaseDdlTrigger",
                 $"[SqlDatabaseDdlTrigger].[{ep.SchemaName}].[{ep.PropertyName}]",
                 $"[{ep.SchemaName}]"),
            ExtendedPropertyHost.Filegroup =>
                ("SqlFilegroup",
                 $"[SqlFilegroup].[{ep.SchemaName}].[{ep.PropertyName}]",
                 $"[{ep.SchemaName}]"),
            ExtendedPropertyHost.Unknown =>
                (ep.UnknownHostKind ?? "Unknown",
                 $"[{ep.UnknownHostKind}].[{ep.SchemaName}].[{ep.PropertyName}]",
                 $"[{ep.SchemaName}]"),
            _ => ep switch
            {
                { ColumnName: not null, TableName: not null, SchemaName: not null } =>
                    ("SqlColumn",
                     $"[SqlColumn].[{ep.SchemaName}].[{ep.TableName}].[{ep.ColumnName}].[{ep.PropertyName}]",
                     $"[{ep.SchemaName}].[{ep.TableName}].[{ep.ColumnName}]"),
                { TableName: not null, SchemaName: not null } =>
                    ("SqlTableBase",
                     $"[SqlTableBase].[{ep.SchemaName}].[{ep.TableName}].[{ep.PropertyName}]",
                     $"[{ep.SchemaName}].[{ep.TableName}]"),
                { SchemaName: not null } =>
                    ("SqlSchema",
                     $"[SqlSchema].[{ep.SchemaName}].[{ep.PropertyName}]",
                     $"[{ep.SchemaName}]"),
                _ =>
                    ("SqlDatabaseOptions",
                     $"[SqlDatabaseOptions].[simulated].[{ep.PropertyName}]",
                     null),
            },
        };

        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlExtendedProperty"),
            new XAttribute("Name", name));

        if (hostRef is not null)
        {
            element.Add(new XElement(ns + "Relationship",
                new XAttribute("Name", "Host"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", hostRef)))));
        }
        // DACFx encodes the Value pre-wrapped (N'…' or numeric); the loader
        // splices it directly into `EXEC sp_addextendedproperty @value = …`.
        // Escape single quotes the SQL way (' → '').
        var wrappedValue = "N'" + ep.Value.Replace("'", "''", StringComparison.Ordinal) + "'";
        element.Add(new XElement(ns + "Property",
            new XAttribute("Name", "Value"),
            new XElement(ns + "Value", new XCData(wrappedValue))));

        _ = hostKind; // host-kind isn't a stored attribute — it's recoverable from the Name shape.
        return element;
    }
}
