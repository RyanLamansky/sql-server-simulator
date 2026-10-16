using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    // The XML schema component views, shaped and filled as SQL Server 2025's
    // (probed 2026-10-16): the built-in sys collection's rows first, then each
    // user collection's in xml_component_id order. The per-kind views share
    // sys.xml_schema_components' thirteen columns and append their own.
    private static void RegisterXmlSchemaComponents(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        var binary = Collation.Get("Latin1_General_BIN");
        var text = NVarcharSqlType.Get(4000, binary, Coercibility.Implicit);
        var code = CharSqlType.Get(1, binary, Coercibility.Implicit);
        HeapColumn Text(string name, bool nullable) => new(name, text, 4000, nullable);
        HeapColumn Code(string name) => new(name, code, 1, false);
        HeapColumn Description(string name) => new(name, nvarchar60Catalog, 60, true);
        HeapColumn Int(string name, bool nullable = false) => new(name, SqlType.Int32, null, nullable);
        HeapColumn Bit(string name) => new(name, SqlType.Bit, null, false);

        HeapColumn[] Component(params HeapColumn[] own) =>
        [
            Int("xml_component_id"),
            Int("xml_collection_id"),
            Int("xml_namespace_id"),
            Bit("is_qualified"),
            Text("name", true),
            Code("symbol_space"),
            Description("symbol_space_desc"),
            Code("kind"),
            Description("kind_desc"),
            Code("derivation"),
            Description("derivation_desc"),
            Int("base_xml_component_id", true),
            Int("scoping_xml_component_id", true),
            .. own,
        ];

        SqlValue Value(string? value) => value is null ? SqlValue.Null(text) : SqlValue.FromString(text, value);
        SqlValue Letter(char value) => SqlValue.FromChar(code, value.ToString());
        SqlValue Describe(string? description) => description is null ? SqlValue.Null(nvarchar60Catalog) : SqlValue.FromString(nvarchar60Catalog, description);
        static SqlValue Id(int? id) => id is int value ? SqlValue.FromInt32(value) : SqlValue.Null(SqlType.Int32);

        SqlValue[] ComponentRow(int collectionId, XmlSchemaComponent component, int extra)
        {
            var row = new SqlValue[13 + extra];
            row[0] = SqlValue.FromInt32(component.Id);
            row[1] = SqlValue.FromInt32(collectionId);
            row[2] = SqlValue.FromInt32(component.NamespaceId);
            row[3] = SqlValue.FromBoolean(component.IsQualified);
            row[4] = Value(component.Name);
            row[5] = Letter(component.SymbolSpace);
            row[6] = Describe(component.SymbolSpace switch
            {
                'A' => "ATTRIBUTE",
                'E' => "ELEMENT",
                'G' => "ATTRIBUTE_GROUP",
                'M' => "MODEL_GROUP",
                'T' => "TYPE",
                _ => "NONE",
            });
            row[7] = Letter(component.Kind);
            row[8] = Describe(component.Kind switch
            {
                'A' => "ATTRIBUTE",
                'C' => "COMPLEX_SIMPLE_TYPE",
                'E' => "ELEMENT",
                'G' => "ATTRIBUTE_GROUP",
                'K' => "COMPLEX_TYPE",
                'L' => "LIST_TYPE",
                'M' => "MODEL_GROUP",
                'N' => "ANY_TYPE",
                'P' => "PRIMITIVE_TYPE",
                'S' => "SIMPLE_TYPE",
                'U' => "UNION_TYPE",
                'V' => "ATTRIBUTE_WILDCARD",
                'W' => "ELEMENT_WILDCARD",
                'Z' => "ANY_SIMPLE_TYPE",
                _ => null,
            });
            row[9] = Letter(component.Derivation);
            row[10] = Describe(component.Derivation switch
            {
                'R' => "RESTRICTION",
                'S' => "SUBSTITUTION",
                'X' => "EXTENSION",
                _ => "NONE",
            });
            row[11] = Id(component.BaseId);
            row[12] = Id(component.ScopingId);
            return row;
        }

        // Every component of every collection the database holds, as
        // (collection id, component), with each collection's namespaces.
        static IEnumerable<(int CollectionId, XmlSchemaComponentSet Set)> Collections(Database database)
        {
            yield return (1, XmlSchemaSystemCollection.Components);
            var user = new List<(int, XmlSchemaComponentSet)>();
            foreach (var (_, schema) in database.Schemas)
            {
                foreach (var (_, collection) in schema.XmlSchemaCollections)
                    user.Add((collection.Id, collection.GetComponents()));
            }
            user.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
            foreach (var entry in user)
                yield return entry;
        }

        static IEnumerable<(int CollectionId, XmlSchemaComponent Component)> Components(Database database)
        {
            var user = new List<(int, XmlSchemaComponent)>();
            foreach (var (collectionId, set) in Collections(database))
            {
                foreach (var component in set.Components)
                {
                    if (collectionId == 1)
                        yield return (1, component);
                    else
                        user.Add((collectionId, component));
                }
            }
            // ALTER … ADD interleaves collections' ids, so the user rows are
            // ordered by component id rather than by collection.
            user.Sort(static (a, b) => a.Item2.Id.CompareTo(b.Item2.Id));
            foreach (var entry in user)
                yield return entry;
        }

        Sys("xml_schema_namespaces",
        [
            Int("xml_collection_id"),
            Text("name", true),
            Int("xml_namespace_id"),
        ], (_, database) => Collections(database).SelectMany(entry => entry.Set.Namespaces.Select((name, index) => (SqlValue[])
        [
            SqlValue.FromInt32(entry.CollectionId),
            Value(name),
            SqlValue.FromInt32(index + 1),
        ])));

        Sys("xml_schema_components", Component(), (_, database) =>
            Components(database).Select(entry => ComponentRow(entry.CollectionId, entry.Component, 0)));

        Sys("xml_schema_types", Component(
            Bit("is_abstract"),
            Bit("allows_mixed_content"),
            Bit("is_extension_blocked"),
            Bit("is_restriction_blocked"),
            Bit("is_final_extension"),
            Bit("is_final_restriction"),
            Bit("is_final_list_member"),
            Bit("is_final_union_member")), (_, database) =>
            Components(database).Where(static entry => entry.Component.SymbolSpace == 'T').Select(entry =>
            {
                var (collectionId, component) = entry;
                var row = ComponentRow(collectionId, component, 8);
                row[13] = SqlValue.FromBoolean(component.IsAbstract);
                row[14] = SqlValue.FromBoolean(component.AllowsMixedContent);
                row[15] = SqlValue.FromBoolean(component.IsExtensionBlocked);
                row[16] = SqlValue.FromBoolean(component.IsRestrictionBlocked);
                row[17] = SqlValue.FromBoolean(component.IsFinalExtension);
                row[18] = SqlValue.FromBoolean(component.IsFinalRestriction);
                row[19] = SqlValue.FromBoolean(component.IsFinalListMember);
                row[20] = SqlValue.FromBoolean(component.IsFinalUnionMember);
                return row;
            }));

        Sys("xml_schema_elements", Component(
            Bit("is_default_fixed"),
            Bit("is_abstract"),
            Bit("is_nillable"),
            Bit("must_be_qualified"),
            Bit("is_extension_blocked"),
            Bit("is_restriction_blocked"),
            Bit("is_substitution_blocked"),
            Bit("is_final_extension"),
            Bit("is_final_restriction"),
            Text("default_value", true)), (_, database) =>
            Components(database).Where(static entry => entry.Component.Kind == 'E').Select(entry =>
            {
                var (collectionId, component) = entry;
                var row = ComponentRow(collectionId, component, 10);
                row[13] = SqlValue.FromBoolean(component.IsDefaultFixed);
                row[14] = SqlValue.FromBoolean(component.IsAbstract);
                row[15] = SqlValue.FromBoolean(component.IsNillable);
                row[16] = SqlValue.FromBoolean(component.MustBeQualified);
                row[17] = SqlValue.FromBoolean(component.IsExtensionBlocked);
                row[18] = SqlValue.FromBoolean(component.IsRestrictionBlocked);
                row[19] = SqlValue.FromBoolean(component.IsSubstitutionBlocked);
                row[20] = SqlValue.FromBoolean(component.IsFinalExtension);
                row[21] = SqlValue.FromBoolean(component.IsFinalRestriction);
                row[22] = Value(component.DefaultValue);
                return row;
            }));

        Sys("xml_schema_attributes", Component(
            Bit("is_default_fixed"),
            Bit("must_be_qualified"),
            Text("default_value", true)), (_, database) =>
            Components(database).Where(static entry => entry.Component.Kind == 'A').Select(entry =>
            {
                var (collectionId, component) = entry;
                var row = ComponentRow(collectionId, component, 3);
                row[13] = SqlValue.FromBoolean(component.IsDefaultFixed);
                row[14] = SqlValue.FromBoolean(component.MustBeQualified);
                row[15] = Value(component.DefaultValue);
                return row;
            }));

        Sys("xml_schema_facets",
        [
            Int("xml_component_id"),
            Int("facet_id"),
            new("kind", CharSqlType.Get(2, binary, Coercibility.Implicit), 2, false),
            Description("kind_desc"),
            Bit("is_fixed"),
            Text("value", true),
        ], (_, database) => Components(database).SelectMany(entry => entry.Component.Facets.Select((facet, index) => (SqlValue[])
        [
            SqlValue.FromInt32(entry.Component.Id),
            SqlValue.FromInt32(index + 1),
            SqlValue.FromChar(CharSqlType.Get(2, binary, Coercibility.Implicit), facet.Kind),
            Describe(facet.Kind switch
            {
                "DF" => "FRACTION_DIGITS",
                "DT" => "TOTAL_DIGITS",
                "EN" => "MINIMUM_EXCLUSIVE_VALUE",
                "EU" => "ENUMERATION",
                "EX" => "MAXIMUM_EXCLUSIVE_VALUE",
                "IN" => "MINIMUM_INCLUSIVE_VALUE",
                "IX" => "MAXIMUM_INCLUSIVE_VALUE",
                "LG" => "LENGTH",
                "LN" => "MINIMUM_LENGTH",
                "LX" => "MAXIMUM_LENGTH",
                "PT" => "PATTERN",
                "WS" => "WHITESPACE_NORMALIZATION",
                _ => null,
            }),
            SqlValue.FromBoolean(facet.IsFixed),
            Value(facet.Value),
        ])));

        Sys("xml_schema_model_groups", Component(
            Code("compositor"),
            Description("compositor_desc")), (_, database) =>
            Components(database).Where(static entry => entry.Component.Kind == 'M').Select(entry =>
            {
                var (collectionId, component) = entry;
                var row = ComponentRow(collectionId, component, 2);
                row[13] = Letter(component.Compositor);
                row[14] = Describe(component.Compositor switch
                {
                    'A' => "XSD_ALL_GROUP",
                    'C' => "XSD_CHOICE_GROUP",
                    _ => "XSD_SEQUENCE_GROUP",
                });
                return row;
            }));

        Sys("xml_schema_wildcards", Component(
            Code("process_content"),
            Description("process_content_desc"),
            Bit("disallow_namespaces")), (_, database) =>
            Components(database).Where(static entry => entry.Component.Kind is 'V' or 'W').Select(entry =>
            {
                var (collectionId, component) = entry;
                var row = ComponentRow(collectionId, component, 3);
                row[13] = Letter(component.ProcessContent);
                row[14] = Describe(component.ProcessContent switch
                {
                    'L' => "LAX_VALIDATION",
                    'P' => "SKIP_VALIDATION",
                    _ => "STRICT_VALIDATION",
                });
                row[15] = SqlValue.FromBoolean(component.DisallowNamespaces);
                return row;
            }));

        Sys("xml_schema_wildcard_namespaces",
        [
            Int("xml_component_id"),
            Text("namespace", false),
        ], (_, database) => Components(database).SelectMany(entry => entry.Component.WildcardNamespaces.Select(name => (SqlValue[])
        [
            SqlValue.FromInt32(entry.Component.Id),
            Value(name),
        ])));

        Sys("xml_schema_component_placements",
        [
            Int("xml_component_id"),
            Int("placement_id"),
            Int("placed_xml_component_id"),
            Bit("is_default_fixed"),
            Int("min_occurences"),
            Int("max_occurences"),
            Text("default_value", true),
        ], (_, database) => Components(database).SelectMany(entry => entry.Component.Placements.Select((placement, index) => (SqlValue[])
        [
            SqlValue.FromInt32(entry.Component.Id),
            SqlValue.FromInt32(index + 1),
            SqlValue.FromInt32(placement.PlacedId),
            SqlValue.FromBoolean(placement.IsDefaultFixed),
            SqlValue.FromInt32(placement.MinOccurs),
            SqlValue.FromInt32(placement.MaxOccurs),
            Value(placement.DefaultValue),
        ])));
    }
}
