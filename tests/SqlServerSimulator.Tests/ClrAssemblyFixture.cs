using System.Data.SqlTypes;
using System.Reflection;
using System.Reflection.Emit;

namespace SqlServerSimulator;

/// <summary>
/// Emits SQLCLR-shaped assemblies in memory for the CLR tests.
/// </summary>
/// <remarks>
/// The repo keeps no binary fixtures, so the tests build their own assembly at
/// run time with <see cref="PersistedAssemblyBuilder"/> rather than loading a
/// committed <c>.dll</c>. The emitted shape mirrors what a real SQLCLR
/// assembly exposes — a static class of static methods over
/// <see cref="System.Data.SqlTypes"/> — which is all the
/// <c>EXTERNAL NAME</c> binder looks at.
/// </remarks>
internal static class ClrAssemblyFixture
{
    /// <summary>Hex literal (<c>0x…</c>) form, ready to paste into
    /// <c>CREATE ASSEMBLY … FROM</c>.</summary>
    public static string HexLiteral(byte[] assembly) => "0x" + Convert.ToHexString(assembly);

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
    public static byte[] Safe(string name = "sim_safe") => Emit(name, includeFileIo: false, includeMutableStatic: false);

    /// <summary>An assembly that touches <see cref="System.IO.File"/> — the
    /// denied-API path of the static SAFE verification.</summary>
    public static byte[] WithFileIo(string name = "sim_fileio") => Emit(name, includeFileIo: true, includeMutableStatic: false);

    /// <summary>An assembly declaring a writable static field — Msg 6211.</summary>
    public static byte[] WithMutableStatic(string name = "sim_static") => Emit(name, includeFileIo: false, includeMutableStatic: true);

    private static byte[] Emit(string name, bool includeFileIo, bool includeMutableStatic)
    {
        var builder = new PersistedAssemblyBuilder(
            new AssemblyName(name) { Version = new Version(1, 2, 3, 4) },
            typeof(object).Assembly);
        var module = builder.DefineDynamicModule(name);
        var type = module.DefineType(
            "UserDefinedFunctions",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        EmitDoubler(type);
        EmitShout(type);
        EmitBoom(type);

        foreach (var echoed in EchoTypes)
            EmitEcho(type, echoed);

        if (includeFileIo)
            EmitReadFile(type);

        if (includeMutableStatic)
            _ = type.DefineField("Counter", typeof(int), FieldAttributes.Public | FieldAttributes.Static);

        _ = type.CreateType();

        var stream = new MemoryStream();
        builder.Save(stream);
        return stream.ToArray();
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

    /// <summary><c>SqlString Shout(SqlString s) =&gt; new(s.Value + "!")</c>,
    /// returning <see cref="SqlString.Null"/> for NULL input so the tests can
    /// observe that NULL arrives as the sentinel rather than as a CLR null.</summary>
    private static void EmitShout(TypeBuilder type)
    {
        var method = type.DefineMethod("Shout", MethodAttributes.Public | MethodAttributes.Static, typeof(SqlString), [typeof(SqlString)]);
        var il = method.GetILGenerator();
        var notNull = il.DefineLabel();

        il.Emit(OpCodes.Ldarga_S, (byte)0);
        il.Emit(OpCodes.Call, typeof(SqlString).GetProperty(nameof(SqlString.IsNull))!.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse_S, notNull);
        il.Emit(OpCodes.Ldsfld, typeof(SqlString).GetField(nameof(SqlString.Null))!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notNull);
        il.Emit(OpCodes.Ldarga_S, (byte)0);
        il.Emit(OpCodes.Call, typeof(SqlString).GetProperty(nameof(SqlString.Value))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "!");
        il.Emit(OpCodes.Call, typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, typeof(SqlString).GetConstructor([typeof(string)])!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary><c>T Echo&lt;T&gt;(T v) =&gt; v</c>, named <c>Echo</c> + the type's
    /// own name.</summary>
    private static void EmitEcho(TypeBuilder type, Type clrType)
    {
        var method = type.DefineMethod(
            "Echo" + clrType.Name, MethodAttributes.Public | MethodAttributes.Static, clrType, [clrType]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary><c>SqlInt32 Boom(SqlInt32) =&gt; throw new InvalidOperationException("boom")</c>.</summary>
    private static void EmitBoom(TypeBuilder type)
    {
        var method = type.DefineMethod("Boom", MethodAttributes.Public | MethodAttributes.Static, typeof(SqlInt32), [typeof(SqlInt32)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "boom");
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
        il.Emit(OpCodes.Throw);
    }

    /// <summary><c>SqlString ReadFile(SqlString path)</c> — exists only to carry
    /// a <see cref="System.IO.File"/> type reference into the metadata.</summary>
    private static void EmitReadFile(TypeBuilder type)
    {
        var method = type.DefineMethod("ReadFile", MethodAttributes.Public | MethodAttributes.Static, typeof(SqlString), [typeof(SqlString)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarga_S, (byte)0);
        il.Emit(OpCodes.Call, typeof(SqlString).GetProperty(nameof(SqlString.Value))!.GetGetMethod()!);
        il.Emit(OpCodes.Call, typeof(File).GetMethod(nameof(File.ReadAllText), [typeof(string)])!);
        il.Emit(OpCodes.Newobj, typeof(SqlString).GetConstructor([typeof(string)])!);
        il.Emit(OpCodes.Ret);
    }
}
