using System.Collections.Frozen;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The built-in <c>sys</c> XML schema collection (<c>xml_collection_id</c>
/// 1) every database lists: the XSD built-in types, the <c>sqltypes</c>
/// namespace's types and attributes, and the <c>xml:</c> namespace's
/// attributes, each under the <c>xml_component_id</c> real gives it — the
/// ids, facets and placements below are SQL Server 2025's own, read from its
/// catalog views (2026-10-16). A user collection's reference to a built-in
/// places the component here.
/// </summary>
internal static class XmlSchemaSystemCollection
{
    /// <summary><c>xs:anyType</c>, the type of an element declared without one and the base of an underived complex type.</summary>
    public const int AnyTypeId = 6;

    /// <summary><c>xs:anySimpleType</c>, the type of an attribute declared without one.</summary>
    public const int AnySimpleTypeId = 11;

    /// <summary>The highest built-in component id; a user collection's start at 65536.</summary>
    public const int LastComponentId = 381;

    /// <summary>The collection's three namespaces and its components.</summary>
    public static readonly XmlSchemaComponentSet Components = Build();

    private static readonly FrozenDictionary<(char Space, string Namespace, string Name), XmlSchemaComponent> ByName =
        Components.Components
            .Where(static component => component.Name is not null)
            .ToFrozenDictionary(static component => (component.SymbolSpace, Components.Namespaces[component.NamespaceId - 1], component.Name!));

    /// <summary>The built-in component a reference names, or null.</summary>
    public static XmlSchemaComponent? Find(char space, string namespaceName, string name) =>
        ByName.GetValueOrDefault((space, namespaceName, name));

    private static XmlSchemaComponentSet Build()
    {
        var components = new List<XmlSchemaComponent>();
        var byId = new Dictionary<int, XmlSchemaComponent>();

        void Add(XmlSchemaComponent component)
        {
            components.Add(component);
            byId[component.Id] = component;
        }

        void Type(int id, int namespaceId, string name, char kind, int? baseId) =>
            Add(new(id, namespaceId, true, name, 'T', kind, null) { Derivation = baseId is null ? 'N' : 'R', BaseId = baseId });

        void Attribute(int id, int namespaceId, string name, string? defaultValue) =>
            Add(new(id, namespaceId, true, name, 'A', 'A', null) { MustBeQualified = true, DefaultValue = defaultValue });

        void Facet(int id, string kind, bool isFixed, string value) => byId[id].Facets.Add(new(kind, isFixed, value));

        void Place(int id, int placedId) => byId[id].Placements.Add(new(placedId, false, 1, 1, null));

        // xml_component_id, xml_namespace_id, name, kind, base_xml_component_id
        Type(6, 1, "anyType", 'N', null);
        Type(11, 1, "anySimpleType", 'Z', 6);
        Type(15, 1, "string", 'P', 11);
        Type(16, 1, "boolean", 'P', 11);
        Type(17, 1, "float", 'P', 11);
        Type(18, 1, "double", 'P', 11);
        Type(19, 1, "decimal", 'P', 11);
        Type(20, 1, "duration", 'P', 11);
        Type(21, 1, "dateTime", 'P', 11);
        Type(22, 1, "time", 'P', 11);
        Type(23, 1, "date", 'P', 11);
        Type(24, 1, "gYearMonth", 'P', 11);
        Type(25, 1, "gYear", 'P', 11);
        Type(26, 1, "gMonthDay", 'P', 11);
        Type(27, 1, "gDay", 'P', 11);
        Type(28, 1, "gMonth", 'P', 11);
        Type(29, 1, "hexBinary", 'P', 11);
        Type(30, 1, "base64Binary", 'P', 11);
        Type(31, 1, "anyURI", 'P', 11);
        Type(32, 1, "QName", 'P', 11);
        Type(33, 1, "NOTATION", 'P', 11);
        Type(100, 1, "normalizedString", 'S', 15);
        Type(101, 1, "token", 'S', 100);
        Type(102, 1, "language", 'S', 101);
        Type(103, 1, "Name", 'S', 101);
        Type(104, 1, "NCName", 'S', 103);
        Type(105, 1, "ID", 'S', 104);
        Type(106, 1, "IDREF", 'S', 104);
        Type(107, 1, "ENTITY", 'S', 104);
        Type(108, 1, "NMTOKEN", 'S', 101);
        Type(109, 1, "integer", 'S', 19);
        Type(110, 1, "nonPositiveInteger", 'S', 109);
        Type(111, 1, "negativeInteger", 'S', 110);
        Type(112, 1, "long", 'S', 109);
        Type(113, 1, "int", 'S', 112);
        Type(114, 1, "short", 'S', 113);
        Type(115, 1, "byte", 'S', 114);
        Type(116, 1, "nonNegativeInteger", 'S', 109);
        Type(117, 1, "unsignedLong", 'S', 116);
        Type(118, 1, "unsignedInt", 'S', 117);
        Type(119, 1, "unsignedShort", 'S', 118);
        Type(120, 1, "unsignedByte", 'S', 119);
        Type(121, 1, "positiveInteger", 'S', 116);
        Type(200, 1, "IDREFS", 'L', 106);
        Type(201, 1, "ENTITIES", 'L', 107);
        Type(202, 1, "NMTOKENS", 'L', 108);
        Attribute(260, 3, "lang", null);
        Attribute(261, 3, "base", null);
        Attribute(262, 3, "space", null);
        Type(263, 3, "xmlSpaceEnum", 'S', 15);
        Type(300, 2, "sqlDbTypeEnum", 'S', 15);
        Type(301, 2, "sqlCompareOptionsEnum", 'S', 15);
        Type(302, 2, "sqlCompareOptionsList", 'L', null);
        Type(303, 2, "char", 'S', 15);
        Type(304, 2, "nchar", 'S', 15);
        Type(305, 2, "varchar", 'S', 15);
        Type(306, 2, "nvarchar", 'S', 15);
        Type(307, 2, "text", 'S', 15);
        Type(308, 2, "ntext", 'S', 15);
        Type(309, 2, "varbinary", 'S', 30);
        Type(310, 2, "binary", 'S', 30);
        Type(311, 2, "image", 'S', 30);
        Type(312, 2, "timestamp", 'S', 30);
        Type(313, 2, "timestampNumeric", 'S', 112);
        Type(314, 2, "decimal", 'S', 19);
        Type(315, 2, "numeric", 'S', 19);
        Type(316, 2, "bigint", 'S', 112);
        Type(317, 2, "int", 'S', 113);
        Type(318, 2, "smallint", 'S', 114);
        Type(319, 2, "tinyint", 'S', 120);
        Type(320, 2, "bit", 'S', 16);
        Type(321, 2, "float", 'S', 18);
        Type(322, 2, "real", 'S', 17);
        Type(324, 2, "datetime", 'S', 21);
        Type(325, 2, "smalldatetime", 'S', 21);
        Type(326, 2, "money", 'S', 19);
        Type(327, 2, "smallmoney", 'S', 19);
        Type(328, 2, "uniqueidentifier", 'S', 15);
        Type(329, 2, "xml", 'K', 6);
        Type(330, 2, "date", 'S', 23);
        Type(331, 2, "time", 'S', 22);
        Type(332, 2, "datetime2", 'S', 21);
        Type(333, 2, "datetimeoffset", 'S', 21);
        Type(334, 2, "hierarchyid", 'S', 15);
        Type(360, 2, "dbobject", 'S', 31);
        Attribute(361, 2, "sqlDbType", "NVarChar");
        Attribute(362, 2, "clrTypeName", null);
        Attribute(363, 2, "maxLength", "1");
        Attribute(364, 2, "localeId", "-1");
        Attribute(365, 2, "sqlCompareOptions", "Default");
        Attribute(366, 2, "sqlCollationVersion", "0");
        Attribute(367, 2, "sqlSortId", "0");
        Attribute(368, 2, "scale", "0");
        Attribute(369, 2, "precision", "18");
        Attribute(370, 2, "xmlSchemaCollection", null);
        Attribute(371, 2, "sqlTypeAlias", null);
        Attribute(372, 2, "base64Encoded", null);
        Attribute(373, 2, "supplementaryCharacters", "false");

        Facet(15, "WS", false, "preserve");
        Facet(16, "WS", true, "collapse");
        Facet(17, "WS", true, "collapse");
        Facet(18, "WS", true, "collapse");
        Facet(19, "WS", true, "collapse");
        Facet(20, "WS", true, "collapse");
        Facet(21, "WS", true, "collapse");
        Facet(22, "WS", true, "collapse");
        Facet(23, "WS", true, "collapse");
        Facet(24, "WS", true, "collapse");
        Facet(25, "WS", true, "collapse");
        Facet(26, "WS", true, "collapse");
        Facet(27, "WS", true, "collapse");
        Facet(28, "WS", true, "collapse");
        Facet(29, "WS", true, "collapse");
        Facet(30, "WS", true, "collapse");
        Facet(31, "WS", true, "collapse");
        Facet(32, "WS", true, "collapse");
        Facet(33, "WS", true, "collapse");
        Facet(100, "WS", false, "replace");
        Facet(101, "WS", false, "collapse");
        Facet(102, "PT", false, "[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*");
        Facet(103, "PT", false, "\\i\\c*");
        Facet(104, "PT", false, "[\\i-[:]][\\c-[:]]*");
        Facet(108, "PT", false, "\\c+");
        Facet(109, "DF", true, "0");
        Facet(110, "IX", false, "0");
        Facet(111, "IX", false, "-1");
        Facet(112, "IN", false, "-9223372036854775808");
        Facet(112, "IX", false, "9223372036854775807");
        Facet(113, "IN", false, "-2147483648");
        Facet(113, "IX", false, "2147483647");
        Facet(114, "IN", false, "-32768");
        Facet(114, "IX", false, "32767");
        Facet(115, "IN", false, "-128");
        Facet(115, "IX", false, "127");
        Facet(116, "IN", false, "0");
        Facet(117, "IX", false, "18446744073709551615");
        Facet(118, "IX", false, "4294967295");
        Facet(119, "IX", false, "65535");
        Facet(120, "IX", false, "255");
        Facet(121, "IN", false, "1");
        Facet(200, "LN", false, "1");
        Facet(201, "LN", false, "1");
        Facet(202, "LN", false, "1");
        Facet(263, "EU", false, "default");
        Facet(263, "EU", false, "preserve");
        Facet(300, "EU", false, "BigInt");
        Facet(300, "EU", false, "Binary");
        Facet(300, "EU", false, "Bit");
        Facet(300, "EU", false, "Char");
        Facet(300, "EU", false, "DateTime");
        Facet(300, "EU", false, "Decimal");
        Facet(300, "EU", false, "Float");
        Facet(300, "EU", false, "Image");
        Facet(300, "EU", false, "Int");
        Facet(300, "EU", false, "Money");
        Facet(300, "EU", false, "NChar");
        Facet(300, "EU", false, "NText");
        Facet(300, "EU", false, "NVarChar");
        Facet(300, "EU", false, "Real");
        Facet(300, "EU", false, "SmallDateTime");
        Facet(300, "EU", false, "SmallInt");
        Facet(300, "EU", false, "SmallMoney");
        Facet(300, "EU", false, "Variant");
        Facet(300, "EU", false, "Text");
        Facet(300, "EU", false, "Timestamp");
        Facet(300, "EU", false, "TinyInt");
        Facet(300, "EU", false, "Udt");
        Facet(300, "EU", false, "UniqueIdentifier");
        Facet(300, "EU", false, "VarBinary");
        Facet(300, "EU", false, "VarChar");
        Facet(300, "EU", false, "Xml");
        Facet(300, "EU", false, "Date");
        Facet(300, "EU", false, "Time");
        Facet(300, "EU", false, "DateTime2");
        Facet(300, "EU", false, "DateTimeOffset");
        Facet(300, "EU", false, "HierarchyId");
        Facet(301, "EU", false, "Default");
        Facet(301, "EU", false, "None");
        Facet(301, "EU", false, "IgnoreCase");
        Facet(301, "EU", false, "IgnoreNonSpace");
        Facet(301, "EU", false, "IgnoreKanaType");
        Facet(301, "EU", false, "IgnoreWidth");
        Facet(301, "EU", false, "BinarySort");
        Facet(301, "EU", false, "BinarySort2");
        Facet(312, "LX", false, "8");
        Facet(324, "PT", false, "((000[1-9])|(00[1-9][0-9])|(0[1-9][0-9]{2})|([1-9][0-9]{3}))-((0[1-9])|(1[012]))-((0[1-9])|([12][0-9])|(3[01]))T(([01][0-9])|(2[0-3]))(:[0-5][0-9]){2}(\\.[0-9]{2}[037])?");
        Facet(324, "IX", false, "9999-12-31T23:59:59.997");
        Facet(324, "IN", false, "1753-01-01T00:00:00.000");
        Facet(325, "PT", false, "((000[1-9])|(00[1-9][0-9])|(0[1-9][0-9]{2})|([1-9][0-9]{3}))-((0[1-9])|(1[012]))-((0[1-9])|([12][0-9])|(3[01]))T(([01][0-9])|(2[0-3]))(:[0-5][0-9])(:00)");
        Facet(325, "IX", false, "2079-06-06T23:59:00");
        Facet(325, "IN", false, "1900-01-01T00:00:00");
        Facet(326, "DT", false, "19");
        Facet(326, "DF", false, "4");
        Facet(326, "IX", false, "922337203685477.5807");
        Facet(326, "IN", false, "-922337203685477.5808");
        Facet(327, "DT", false, "10");
        Facet(327, "DF", false, "4");
        Facet(327, "IX", false, "214748.3647");
        Facet(327, "IN", false, "-214748.3648");
        Facet(328, "PT", false, "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})|(\\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\})");
        Facet(330, "PT", false, "((000[1-9])|(00[1-9][0-9])|(0[1-9][0-9]{2})|([1-9][0-9]{3}))-((0[1-9])|(1[012]))-((0[1-9])|([12][0-9])|(3[01]))");
        Facet(330, "IX", false, "9999-12-31");
        Facet(330, "IN", false, "0001-01-01");
        Facet(331, "PT", false, "(([01][0-9])|(2[0-3]))(:[0-5][0-9]){2}(\\.[0-9]+)?");
        Facet(331, "IN", false, "00:00:00");
        Facet(332, "PT", false, "((000[1-9])|(00[1-9][0-9])|(0[1-9][0-9]{2})|([1-9][0-9]{3}))-((0[1-9])|(1[012]))-((0[1-9])|([12][0-9])|(3[01]))T(([01][0-9])|(2[0-3]))(:[0-5][0-9]){2}(\\.[0-9]+)?");
        Facet(332, "IN", false, "0001-01-01T00:00:00");
        Facet(333, "PT", false, "((000[1-9])|(00[1-9][0-9])|(0[1-9][0-9]{2})|([1-9][0-9]{3}))-((0[1-9])|(1[012]))-((0[1-9])|([12][0-9])|(3[01]))T(([01][0-9])|(2[0-3]))(:[0-5][0-9]){2}(\\.[0-9]+)?(([+-]((((0[0-9])|(1[0-3]))(:[0-5][0-9]))|14:00))|Z)");
        Facet(333, "IN", false, "0001-01-01T00:00:00Z");
        Facet(334, "PT", false, "/((-?[0-9]+)(\\.-?[0-9]+)*/)*");

        Place(260, 102);
        Place(261, 31);
        Place(262, 263);
        Place(302, 301);
        Place(329, 380);
        Place(361, 300);
        Place(362, 15);
        Place(363, 112);
        Place(364, 113);
        Place(365, 302);
        Place(366, 113);
        Place(367, 113);
        Place(368, 120);
        Place(369, 120);
        Place(370, 15);
        Place(371, 15);
        Place(372, 16);
        Place(373, 16);

        // sqltypes:xml's content: a sequence of one skip-validated wildcard
        // over any namespace, any number of times.
        byId[329].AllowsMixedContent = true;
        Add(new(380, 2, false, null, 'M', 'M', 329) { Compositor = 'S' });
        Add(new(381, 2, false, null, 'N', 'W', 380) { ProcessContent = 'P', DisallowNamespaces = true });
        byId[380].Placements.Add(new(381, false, 0, int.MaxValue, null));
        components.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        // Each type's whitespace handling: its own whiteSpace facet, else its
        // base's (ids ascend along every base chain), collapse for a list.
        foreach (var component in components)
        {
            if (component.SymbolSpace != 'T' || component.Kind == 'L')
                continue;
            var own = component.Facets.Find(static facet => facet.Kind == "WS");
            component.WhiteSpace = own.Kind is not null
                ? own.Value switch
                {
                    "preserve" => 'P',
                    "replace" => 'R',
                    _ => 'C',
                }
                : component.BaseId is int baseId ? byId[baseId].WhiteSpace : 'P';
        }

        return new(
            ["http://www.w3.org/2001/XMLSchema", "http://schemas.microsoft.com/sqlserver/2004/sqltypes", "http://www.w3.org/XML/1998/namespace"],
            components);
    }
}
