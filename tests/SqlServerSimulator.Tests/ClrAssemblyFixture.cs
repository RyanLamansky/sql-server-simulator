using System.Data.SqlTypes;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SqlServerSimulator;

/// <summary>
/// Builds SQLCLR-shaped assemblies in memory for the CLR tests.
/// </summary>
/// <remarks>
/// The repo keeps no binary fixtures, so the tests build their own assemblies
/// at run time. The ones the server is meant to take are compiled the way one
/// is built for real SQL Server — against the .NET Framework 4.8 reference
/// assemblies (<see cref="ClrFrameworkFixture.Compile"/>) — since real's
/// catalog refuses a .NET-targeted one (Msg 6503); <see cref="NetTargeted"/>
/// and <see cref="WithReferences"/> build the ones it refuses.
/// </remarks>
internal static class ClrAssemblyFixture
{
    /// <summary>Hex literal (<c>0x…</c>) form, ready to paste into
    /// <c>CREATE ASSEMBLY … FROM</c>.</summary>
    public static string HexLiteral(byte[] assembly) => "0x" + Convert.ToHexString(assembly);

    /// <summary>
    /// Turns <c>clr strict security</c> off, which real installs on: what a
    /// developer does on real to register an assembly it hasn't signed.
    /// </summary>
    public const string TrustAllAssemblies = "exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'clr strict security', 0; reconfigure;";

    /// <summary>A CLR-enabled simulation with <see cref="TrustAllAssemblies"/> applied.</summary>
    public static Simulation TrustingSimulation()
    {
        var simulation = new Simulation { EnableClr = true };
        _ = simulation.ExecuteNonQuery(TrustAllAssemblies);
        return simulation;
    }

    /// <summary>
    /// Every <see cref="System.Data.SqlTypes"/> type the <c>EXTERNAL NAME</c>
    /// binder marshals, each carried by an <c>Echo</c>&#160;+&#160;type-name
    /// identity routine in <see cref="Safe"/>. An identity body exercises both
    /// directions at once: the argument has to arrive converted for the routine
    /// to return it, and the return value has to convert back.
    /// </summary>
    public static readonly Type[] EchoTypes =
    [
        typeof(SqlBinary),
        typeof(SqlBoolean),
        typeof(SqlByte),
        typeof(SqlDateTime),
        typeof(SqlDecimal),
        typeof(SqlDouble),
        typeof(SqlGuid),
        typeof(SqlInt16),
        typeof(SqlInt32),
        typeof(SqlInt64),
        typeof(SqlMoney),
        typeof(SqlSingle),
        typeof(SqlString),
        typeof(SqlXml),
    ];

    /// <summary>
    /// A well-behaved assembly: <c>UserDefinedFunctions.Doubler(SqlInt32)</c>
    /// returning <c>SqlInt32</c>, plus
    /// <c>UserDefinedFunctions.Shout(SqlString)</c> returning
    /// <see cref="SqlString"/>, <c>Boom(SqlInt32)</c> which always throws, and
    /// one identity routine per <see cref="EchoTypes"/> entry.
    /// </summary>
    public static byte[] Safe(string name = "sim_safe") => ClrFrameworkFixture.Compile(name, Source(string.Empty));

    /// <summary>An assembly that touches <see cref="System.IO.File"/> — the
    /// denied-API path of the static SAFE verification.</summary>
    public static byte[] WithFileIo(string name = "sim_fileio") => ClrFrameworkFixture.Compile(
        name, Source("public static SqlString ReadFile(SqlString path) { return new SqlString(System.IO.File.ReadAllText(path.Value)); }"));

    /// <summary>An assembly declaring a writable static field — Msg 6211.</summary>
    public static byte[] WithMutableStatic(string name = "sim_static") => ClrFrameworkFixture.Compile(name, Source("public static int Counter;"));

    private static string Source(string extra)
    {
        var echoes = string.Concat(EchoTypes.Select(type => $"public static {type.Name} Echo{type.Name}({type.Name} v) {{ return v; }}\n"));
        return $$"""
            using System;
            using System.Data.SqlTypes;

            [assembly: System.Reflection.AssemblyVersion("1.2.3.4")]

            public static class UserDefinedFunctions
            {
                public static SqlInt32 Doubler(SqlInt32 v) { return new SqlInt32(v.Value * 2); }
                public static SqlString Shout(SqlString s) { return s.IsNull ? SqlString.Null : new SqlString(s.Value + "!"); }
                public static SqlInt32 Boom(SqlInt32 v) { throw new InvalidOperationException("boom"); }
                {{echoes}}
                {{extra}}
            }
            """;
    }

    /// <summary>
    /// <c>Doubler</c> in an assembly built for .NET — referencing
    /// <c>System.Private.CoreLib</c> and its facades — which real's catalog
    /// refuses.
    /// </summary>
    public static byte[] NetTargeted(string name = "sim_net")
    {
        var builder = new PersistedAssemblyBuilder(
            new AssemblyName(name) { Version = new Version(1, 2, 3, 4) },
            typeof(object).Assembly);
        var module = builder.DefineDynamicModule(name);
        var type = module.DefineType(
            "UserDefinedFunctions",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        EmitDoubler(type);
        _ = type.CreateType();
        var stream = new MemoryStream();
        builder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// An assembly holding no code that references <c>mscorlib</c> and then
    /// each of <paramref name="references"/> — name, version and public key
    /// token, null for an unsigned one.
    /// </summary>
    public static byte[] WithReferences(string name, params (string Name, string Version, string? Token)[] references)
    {
        var metadata = new MetadataBuilder();
        _ = metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        _ = metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.Sha1);
        var corlib = metadata.AddAssemblyReference(
            metadata.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default, metadata.GetOrAddBlob(Convert.FromHexString("b77a5c561934e089")), default, default);
        foreach (var (referenceName, version, token) in references)
        {
            _ = metadata.AddAssemblyReference(
                metadata.GetOrAddString(referenceName), Version.Parse(version), default,
                token is null ? default : metadata.GetOrAddBlob(Convert.FromHexString(token)), default, default);
        }
        var objectType = metadata.AddTypeReference(corlib, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        _ = metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        _ = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, metadata.GetOrAddString("P"), metadata.GetOrAddString("C"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var image = new BlobBuilder();
        _ = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        return image.ToArray();
    }

    /// <summary><c>SqlInt32 Doubler(SqlInt32 v) =&gt; new(v.Value * 2)</c>.</summary>
    private static void EmitDoubler(TypeBuilder type)
    {
        var method = type.DefineMethod("Doubler", MethodAttributes.Public | MethodAttributes.Static, typeof(SqlInt32), [typeof(SqlInt32)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarga_S, (byte)0);
        il.Emit(OpCodes.Call, typeof(SqlInt32).GetProperty(nameof(SqlInt32.Value))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Newobj, typeof(SqlInt32).GetConstructor([typeof(int)])!);
        il.Emit(OpCodes.Ret);
    }
}
