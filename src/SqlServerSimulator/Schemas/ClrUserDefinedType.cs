using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A CLR user-defined type — <c>CREATE TYPE name EXTERNAL NAME
/// assembly.[class]</c> — bound to its class: how a value serializes, the
/// <c>Parse</c> / <c>ToString</c> pair behind the string conversions, and the
/// members a query calls. The catalog entry is an <see cref="AliasType"/>
/// whose underlying type is this type's <see cref="SqlType"/>, so the type
/// shares the alias types' namespace, <c>TYPE_ID</c> and <c>DROP TYPE</c>.
/// </summary>
/// <remarks>
/// Every call into the class runs its code, so each one opens the routine
/// context a function gets (no pipe) and reports a throw as Msg 6522 state 2
/// naming the type, as real does (probed 2026-09-28 against SQL Server 2025).
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
[UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
[UnconditionalSuppressMessage("Trimming", "IL2077:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
[UnconditionalSuppressMessage("Trimming", "IL2080:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
internal sealed class ClrUserDefinedType
{
    private const string BinarySerializeInterface = "Microsoft.SqlServer.Server.IBinarySerialize";

    private readonly MethodInfo parse;
    private readonly MemberInfo nullMember;
    private readonly MethodInfo? read;
    private readonly MethodInfo? write;

    /// <summary>The <c>Format.Native</c> field layout; null for <c>Format.UserDefined</c>.</summary>
    public readonly ClrNativeLayout? Layout;

    public Schema Schema;

    public readonly string Name;

    public readonly SqlAssembly Assembly;

    /// <summary>The class segment of <c>EXTERNAL NAME</c>, as written — <c>sys.assembly_types.assembly_class</c>.</summary>
    public readonly string ClassName;

    public readonly Type Type;

    /// <summary>
    /// The largest serialized value, -1 for unlimited: a native layout's
    /// width, else <c>SqlUserDefinedType(MaxByteSize = …)</c>.
    /// </summary>
    public readonly int MaxByteSize;

    public readonly bool IsByteOrdered;

    public readonly bool IsFixedLength;

    public readonly int UserTypeId;

    /// <summary>This type as the storage layer carries it.</summary>
    public readonly ClrUdtSqlType SqlType;

    private ClrUserDefinedType(
        Schema schema,
        string name,
        SqlAssembly assembly,
        string className,
        Type type,
        ClrNativeLayout? layout,
        int maxByteSize,
        bool isByteOrdered,
        bool isFixedLength,
        MethodInfo parse,
        MemberInfo nullMember,
        MethodInfo? read,
        MethodInfo? write,
        int userTypeId)
    {
        this.Schema = schema;
        this.Name = name;
        this.Assembly = assembly;
        this.ClassName = className;
        this.Type = type;
        this.Layout = layout;
        this.MaxByteSize = maxByteSize;
        this.IsByteOrdered = isByteOrdered;
        this.IsFixedLength = isFixedLength;
        this.parse = parse;
        this.nullMember = nullMember;
        this.read = read;
        this.write = write;
        this.UserTypeId = userTypeId;
        this.SqlType = new ClrUdtSqlType(this);
    }

    /// <summary>
    /// <c>database.schema.type</c>, how a client and the explicit-conversion
    /// errors name the type.
    /// </summary>
    public string QualifiedName => $"{this.Schema.Database.Name}.{this.Schema.Name}.{this.Name}";

    /// <summary><c>sys.assembly_types.assembly_qualified_name</c>, which the wire also carries.</summary>
    public string AssemblyQualifiedName
    {
        get
        {
            // clr_name reads "name, version=…, culture=…, publickeytoken=…, …";
            // the qualified name restores the casing .NET writes.
            var parts = this.Assembly.ClrName.Split(", ");
            string Part(string key, string fallback)
            {
                foreach (var part in parts)
                {
                    if (part.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                        return part[(key.Length + 1)..];
                }

                return fallback;
            }

            return $"{this.ClassName}, {this.Assembly.Name}, Version={Part("version", "0.0.0.0")}, Culture={Part("culture", "neutral")}, PublicKeyToken={Part("publickeytoken", "null")}";
        }
    }

    /// <summary>
    /// Binds <paramref name="type"/> as a user-defined type, raising what real
    /// raises for a class that doesn't conform (probed 2026-09-28 against
    /// SQL Server 2025), in this order: no <c>SqlUserDefinedType</c>
    /// attribute (Msg 6255); for <c>Format.Native</c> a class not laid out
    /// sequentially (Msg 6229), a reference-typed field (Msg 6225) or one
    /// native serialization can't carry (Msg 6222); for
    /// <c>Format.UserDefined</c> no <c>IBinarySerialize</c> (Msg 6226) or a
    /// <c>MaxByteSize</c> outside -1 and 1–8000 (Msg 6244); then no
    /// <c>INullable</c> (Msg 6577), no static <c>Null</c> (Msg 6557) and no
    /// static <c>Parse(SqlString)</c> (Msg 6558), those three followed by Msg
    /// 6597.
    /// </summary>
    public static ClrUserDefinedType Bind(Schema schema, string name, SqlAssembly assembly, string className, Type type, int userTypeId)
    {
        if (ClrAttributes.Find(type, ClrAttributes.SqlUserDefinedType) is not { } attribute)
            throw SimulatedSqlException.ClrUdtMissingAttribute(className);

        var qualifiedClass = $"{assembly.Name}.{type.FullName}";
        var isNative = attribute.ConstructorArguments is [{ Value: 1 }];
        ClrNativeLayout? layout = null;
        MethodInfo? read = null;
        MethodInfo? write = null;
        int maxByteSize;
        bool isFixedLength;
        if (isNative)
        {
            if (!type.IsValueType && !type.IsLayoutSequential)
                throw SimulatedSqlException.ClrNativeNotSequential(qualifiedClass);
            layout = ClrNativeLayout.TryBuild(type, out var refused);
            if (layout is null)
            {
                var fieldType = refused!.FieldType;
                if (!fieldType.IsValueType)
                {
                    var fieldAssembly = fieldType.Assembly == typeof(object).Assembly ? "mscorlib" : fieldType.Assembly.GetName().Name;
                    throw SimulatedSqlException.ClrNativeFormatField(assembly.Name, refused.DeclaringType!.FullName!, refused.Name, $"{fieldAssembly}.{fieldType.FullName}");
                }

                throw SimulatedSqlException.ClrNativeFieldInvalid($"{assembly.Name}.{refused.DeclaringType!.FullName}", refused.Name);
            }

            maxByteSize = layout.Size;
            isFixedLength = true;
        }
        else
        {
            var serializer = Array.Find(type.GetInterfaces(), candidate => candidate.FullName == BinarySerializeInterface)
                ?? throw SimulatedSqlException.ClrUdtNotBinarySerialize(qualifiedClass);
            // The class's own implementations, so a reported stack names them.
            var map = type.GetInterfaceMap(serializer);
            read = map.TargetMethods[Array.FindIndex(map.InterfaceMethods, method => method.Name == "Read")];
            write = map.TargetMethods[Array.FindIndex(map.InterfaceMethods, method => method.Name == "Write")];
            maxByteSize = NamedArgument(attribute, "MaxByteSize") is int declared ? declared : 0;
            if (maxByteSize is not (-1 or (>= 1 and <= 8000)))
                throw SimulatedSqlException.ClrUdtSizeOutOfRange(maxByteSize, qualifiedClass);
            isFixedLength = NamedArgument(attribute, "IsFixedLength") is true;
        }

        if (!typeof(INullable).IsAssignableFrom(type))
            throw SimulatedSqlException.Aggregate([SimulatedSqlException.ClrUdtNotNullable(className), SimulatedSqlException.ClrCreateTypeFailed()]);

        MemberInfo? nullMember = type.GetProperty("Null", BindingFlags.Public | BindingFlags.Static);
        nullMember ??= type.GetField("Null", BindingFlags.Public | BindingFlags.Static);
        if (nullMember is null)
            throw SimulatedSqlException.Aggregate([SimulatedSqlException.ClrUdtNonConforming(className, "field", "Null", 7), SimulatedSqlException.ClrCreateTypeFailed()]);

        var parse = type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(SqlString)]);
        if (parse is null || parse.ReturnType != type)
            throw SimulatedSqlException.Aggregate([SimulatedSqlException.ClrUdtNonConforming(className, "method", "Parse", 1), SimulatedSqlException.ClrCreateTypeFailed()]);

        return new ClrUserDefinedType(
            schema,
            name,
            assembly,
            className,
            type,
            layout,
            maxByteSize,
            NamedArgument(attribute, "IsByteOrdered") is true,
            isFixedLength,
            parse,
            nullMember,
            read,
            write,
            userTypeId);
    }

    private static object? NamedArgument(CustomAttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.MemberName == name)
                return argument.TypedValue.Value;
        }

        return null;
    }

    /// <summary>
    /// The registered type whose class is <paramref name="clrType"/> in
    /// <paramref name="database"/>, or <see langword="null"/>.
    /// </summary>
    public static ClrUserDefinedType? FindByClrType(Database database, Type clrType)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var alias in schema.AliasTypes.Values)
            {
                if (alias.UnderlyingType is ClrUdtSqlType { Udt: var udt } && udt.Type == clrType)
                    return udt;
            }
        }

        return null;
    }

    /// <summary>
    /// Calls into the type's code: the routine context a function gets, and a
    /// throw reported as Msg 6522 state 2 naming the type.
    /// </summary>
    public object? Invoke(MethodBase method, object? target, object?[]? arguments)
    {
        try
        {
            using (this.Assembly.UsesServerContext ? ClrHost.Enter(pipe: null) : default(ClrHost.RoutineScope?))
                return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw SimulatedSqlException.ClrRoutineThrew(this.Name, ClrExceptionReport.Describe(ex.InnerException, method), state: 2);
        }
    }

    /// <summary>The type's <c>Null</c> instance, which a NULL argument of the type passes.</summary>
    public object? NullInstance => this.nullMember switch
    {
        PropertyInfo property => this.Invoke(property.GetMethod!, null, null),
        FieldInfo nullField => nullField.GetValue(null),
        _ => null,
    };

    /// <summary>The value <paramref name="instance"/> is: SQL NULL when it reports <c>IsNull</c>.</summary>
    public SqlValue FromClr(object? instance) => instance is null or INullable { IsNull: true }
        ? SqlValue.Null(this.SqlType)
        : SqlValue.FromClrUdt(this.SqlType, this.Serialize(instance));

    /// <summary>The instance <paramref name="value"/> holds, its <c>Null</c> for SQL NULL.</summary>
    public object? ToClr(SqlValue value) => value.IsNull ? this.NullInstance : this.Deserialize(value.AsClrUdtBytes);

    /// <summary>
    /// A string converted to the type through <c>Parse</c> — the implicit
    /// conversion an assignment from a string literal takes.
    /// </summary>
    public SqlValue ParseText(string text)
    {
        try
        {
            return this.FromClr(this.Invoke(this.parse, null, [new SqlString(text)]));
        }
        catch (SimulatedSqlException failure) when (failure.Number == 6522)
        {
            failure.IsClrTypeParseFailure = true;
            throw;
        }
    }

    /// <summary>The type's <c>ToString()</c> of a non-NULL value.</summary>
    public string ToText(SqlValue value)
    {
        var instance = this.Deserialize(value.AsClrUdtBytes);
        return (string?)this.Invoke(instance.GetType().GetMethod("ToString", Type.EmptyTypes)!, instance, null) ?? "";
    }

    /// <summary>
    /// The value as <c>xml</c>: the class's <see cref="System.Xml.Serialization.XmlSerializer"/>
    /// document, which is what real's conversion produces.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = TrimmingJustification.RegisteredAssembly)]
    public string ToXmlText(SqlValue value)
    {
        var instance = this.Deserialize(value.AsClrUdtBytes);
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using (var writer = System.Xml.XmlWriter.Create(text, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            var namespaces = new System.Xml.Serialization.XmlSerializerNamespaces();
            namespaces.Add("xsd", "http://www.w3.org/2001/XMLSchema");
            namespaces.Add("xsi", "http://www.w3.org/2001/XMLSchema-instance");
            new System.Xml.Serialization.XmlSerializer(this.Type).Serialize(writer, instance, namespaces);
        }

        return text.ToString();
    }

    /// <summary>
    /// An <c>xml</c> document read back through the class's XML
    /// serialization; one it can't read is Msg 6522 state 1.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = TrimmingJustification.RegisteredAssembly)]
    public SqlValue FromXmlText(string xml)
    {
        object? instance;
        try
        {
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            instance = new System.Xml.Serialization.XmlSerializer(this.Type).Deserialize(reader);
        }
        catch (InvalidOperationException ex)
        {
            throw SimulatedSqlException.ClrRoutineThrew(this.Name, ClrExceptionReport.Describe(ex, null), state: 1);
        }

        return this.FromClr(instance);
    }

    /// <summary>
    /// The serialized form of <paramref name="instance"/>. A
    /// <c>Format.UserDefined</c> type writing past its <c>MaxByteSize</c> gets
    /// the <see cref="SqlTypeException"/> the server's bounded buffer throws,
    /// inside its own <c>Write</c>.
    /// </summary>
    public byte[] Serialize(object instance)
    {
        if (this.Layout is { } layout)
            return layout.Serialize(instance);

        using var stream = new BoundedStream(this.MaxByteSize);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            _ = this.Invoke(this.write!, instance, [writer]);
            writer.Flush();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The instance <paramref name="bytes"/> hold. A native value whose length
    /// isn't the layout's is Msg 6235.
    /// </summary>
    public object Deserialize(byte[] bytes)
    {
        if (this.Layout is { } layout)
        {
            return bytes.Length == layout.Size
                ? layout.Deserialize(bytes)
                : throw SimulatedSqlException.ClrUdtLengthMismatch(bytes.Length, layout.Size, this.Name);
        }

        var instance = Activator.CreateInstance(this.Type)!;
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        _ = this.Invoke(this.read!, instance, [reader]);
        return instance;
    }

    /// <summary>
    /// The public instance property or field <paramref name="name"/> names, or
    /// <see langword="null"/>.
    /// </summary>
    public MemberInfo? FindDataMember(string name, bool isStatic)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return (MemberInfo?)this.Type.GetProperty(name, flags) ?? this.Type.GetField(name, flags);
    }

    /// <summary>
    /// The public methods named <paramref name="name"/>, static or instance
    /// as <paramref name="isStatic"/> asks.
    /// </summary>
    public MethodInfo[] FindMethods(string name, bool isStatic)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return Array.FindAll(this.Type.GetMethods(flags), method => method.Name == name && !method.IsSpecialName);
    }

    /// <summary>
    /// A stream that refuses to grow past a CLR type's <c>MaxByteSize</c>
    /// with the exception real's serialization buffer throws.
    /// </summary>
    private sealed class BoundedStream(int limit) : MemoryStream
    {
        private readonly int limit = limit;

        public override void Write(byte[] buffer, int offset, int count)
        {
            this.Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            this.Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            this.Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (this.limit > 0 && this.Position + count > this.limit)
                throw new SqlTypeException("The buffer is insufficient. Read or write operation failed.");
        }
    }
}
