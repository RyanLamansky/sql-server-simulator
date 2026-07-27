using System.Collections.Frozen;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Clr;

/// <summary>
/// The manifest facts <c>sys.assemblies</c> / <c>ASSEMBLYPROPERTY</c> project,
/// read straight out of the PE metadata without loading or executing anything.
/// </summary>
internal readonly struct ClrAssemblyIdentity(string name, Version version, string culture, byte[] publicKey, Guid mvid)
{
    public readonly string Name = name;
    public readonly Version Version = version;

    /// <summary>Empty for the neutral culture, which is what SQLCLR assemblies carry.</summary>
    public readonly string Culture = culture;

    /// <summary>Empty when the assembly is not strong-named.</summary>
    public readonly byte[] PublicKey = publicKey;

    public readonly Guid Mvid = mvid;
}

/// <summary>
/// Reads and validates the bytes handed to <c>CREATE ASSEMBLY … FROM 0x…</c>.
/// Everything here works on metadata only — the assembly is never loaded, so a
/// rejected candidate never gets the chance to run a module initializer.
/// </summary>
internal static class ClrAssemblyMetadata
{
    /// <summary>The trailing detail real SQL Server appends to Msg 6544.</summary>
    private const string NativeStubDetail = "Unverifiable PE Header/native stub.";

    /// <summary>
    /// Assembly simple names a candidate may reference. Real SQL Server
    /// resolves every <c>AssemblyRef</c> against its own catalog of hosted
    /// .NET Framework assemblies and raises Msg 6503 for anything else; the
    /// simulator applies the same rule against the framework it actually runs
    /// on, which is why the modern .NET assembly names appear alongside the
    /// Framework ones.
    /// </summary>
    /// <remarks>
    /// This is a deliberate over-acceptance relative to real SQL Server, which
    /// rejects a modern-.NET-targeted assembly outright (probe-confirmed: an
    /// assembly referencing <c>system.data.common, version=10.0.0.0</c> fails
    /// with Msg 6503). The simulator runs on .NET, so those references
    /// resolve — accepting them is what lets a test emit a fixture assembly
    /// without a .NET Framework toolchain.
    /// </remarks>
    private static readonly FrozenSet<string> allowedReferencePrefixes = FrozenSet.ToFrozenSet(
        ["System", "netstandard", "mscorlib", "Microsoft.CSharp", "Microsoft.VisualBasic", "Microsoft.SqlServer.Server", "Microsoft.SqlServer.Types"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Types a <c>SAFE</c> assembly may not reference. Real SQL Server relied
    /// on Code Access Security to block these at run time; .NET removed CAS,
    /// so the simulator refuses them at <c>CREATE ASSEMBLY</c> instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is type-level rather than namespace-level on purpose: every
    /// compiled assembly carries <c>System.Reflection.Assembly*Attribute</c>
    /// and <c>System.Runtime.InteropServices.ComVisibleAttribute</c> type
    /// references from its own custom attributes, so denying those namespaces
    /// wholesale would reject ordinary, harmless assemblies.
    /// </para>
    /// <para>
    /// <strong>This is defense in depth, not a sandbox.</strong> A metadata
    /// denylist cannot stop a determined assembly — string-driven reflection
    /// and unlisted APIs remain reachable — and .NET offers no in-process
    /// isolation to fall back on. The real control over untrusted assembly
    /// bytes is the host-level <c>Simulation.EnableClr</c> opt-in.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> deniedTypes = FrozenSet.ToFrozenSet(
        [
            "System.Activator",
            "System.AppDomain",
            "System.Environment",
            "System.IO.Directory",
            "System.IO.DirectoryInfo",
            "System.IO.DriveInfo",
            "System.IO.File",
            "System.IO.FileInfo",
            "System.IO.FileStream",
            "System.IO.FileSystemWatcher",
            "System.IO.StreamReader",
            "System.IO.StreamWriter",
            "System.Reflection.Assembly",
            "System.Reflection.ConstructorInfo",
            "System.Reflection.FieldInfo",
            "System.Reflection.MethodInfo",
            "System.Reflection.Module",
            "System.Reflection.PropertyInfo",
            "System.Runtime.InteropServices.GCHandle",
            "System.Runtime.InteropServices.Marshal",
            "System.Runtime.InteropServices.NativeLibrary",
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Namespace prefixes a <c>SAFE</c> assembly may not reference at all.
    /// Unlike <see cref="deniedTypes"/> these carry no attribute traffic, so a
    /// prefix match is safe.
    /// </summary>
    private static readonly string[] deniedNamespacePrefixes =
    [
        "Microsoft.Win32",
        "System.Diagnostics.Process",
        "System.Net",
        "System.Reflection.Emit",
        "System.Runtime.Loader",
        "System.Security.Permissions",
    ];

    /// <summary>
    /// Reads the manifest identity. Throws
    /// <see cref="SimulatedSqlException"/> (Msg 6544) when the bytes are not a
    /// managed assembly at all.
    /// </summary>
    public static ClrAssemblyIdentity ReadIdentity(byte[] content, string verb, string assemblyName)
    {
        using var peReader = OpenPortableExecutable(content, verb, assemblyName);
        return ReadIdentity(peReader.GetMetadataReader());
    }

    private static ClrAssemblyIdentity ReadIdentity(MetadataReader metadata)
    {
        var definition = metadata.GetAssemblyDefinition();
        return new(
            metadata.GetString(definition.Name),
            definition.Version,
            metadata.GetString(definition.Culture),
            definition.PublicKey.IsNil ? [] : metadata.GetBlobBytes(definition.PublicKey),
            metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
    }

    /// <summary>
    /// The <c>sys.assemblies.clr_name</c> / <c>ASSEMBLYPROPERTY(…, 'CLRName')</c>
    /// projection: the binding identity, lowercased.
    /// </summary>
    /// <remarks>
    /// The version reads <c>0.0.0.0</c> for an assembly without a strong name
    /// — probe-confirmed against SQL Server 2025, which reports
    /// <c>version=0.0.0.0</c> in <c>clr_name</c> for <c>regex_clr.dll</c> while
    /// <c>ASSEMBLYPROPERTY(…, 'VersionMajor')</c> and friends report the real
    /// <c>1.0.5100.29893</c> off the same manifest. Version participates in
    /// binding only for strong-named assemblies, so a simple name binds
    /// version-agnostically. The strong-named case is unprobed.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "sys.assemblies.clr_name is a lowercase projection in real SQL Server; this reproduces that format rather than normalizing for comparison.")]
    public static string BuildClrName(byte[] content)
    {
        using var peReader = OpenPortableExecutable(content, "CREATE", "");
        var identity = ReadIdentity(peReader.GetMetadataReader());
        var signed = identity.PublicKey.Length > 0;
        var version = signed ? identity.Version : new Version(0, 0, 0, 0);
        var token = signed ? Convert.ToHexStringLower(ComputePublicKeyToken(identity.PublicKey)) : "null";
        var culture = identity.Culture.Length == 0 ? "neutral" : identity.Culture.ToLowerInvariant();

        return $"{identity.Name.ToLowerInvariant()}, version={version}, culture={culture}, publickeytoken={token}, processorarchitecture=msil";
    }

    /// <summary>
    /// The low 8 bytes of the SHA-1 of the public key, reversed — the standard
    /// strong-name token derivation.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "The strong-name public key token is defined by ECMA-335 as the truncated SHA-1 of the public key; the algorithm is dictated by the format being read, not chosen for security.")]
    private static byte[] ComputePublicKeyToken(byte[] publicKey)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(publicKey);
        var token = new byte[8];
        for (var i = 0; i < 8; i++)
            token[i] = hash[hash.Length - 1 - i];
        return token;
    }

    /// <summary>
    /// Runs the static <c>CREATE ASSEMBLY</c> validation: the candidate must be
    /// a pure-IL managed assembly, may only reference framework assemblies,
    /// and — when registered <c>SAFE</c> — may not declare P/Invoke, mutable
    /// statics, or references into the denied API surface.
    /// </summary>
    public static void Verify(byte[] content, string assemblyName, AssemblyPermissionSet permissionSet, string verb)
    {
        using var peReader = OpenPortableExecutable(content, verb, assemblyName);
        var metadata = peReader.GetMetadataReader();

        if ((peReader.PEHeaders.CorHeader!.Flags & CorFlags.ILOnly) == 0)
            throw SimulatedSqlException.AssemblyMalformed(verb, assemblyName, NativeStubDetail);

        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            var root = name.Split('.')[0];
            if (!allowedReferencePrefixes.Contains(name) && !allowedReferencePrefixes.Contains(root))
                throw SimulatedSqlException.ReferencedAssemblyNotInCatalog(DescribeReference(metadata, handle));
        }

        // EXTERNAL_ACCESS / UNSAFE opt out of the API restrictions, matching
        // real SQL Server's permission-set ladder.
        if (permissionSet != AssemblyPermissionSet.Safe)
            return;

        if (metadata.GetTableRowCount(TableIndex.ImplMap) > 0)
            throw SimulatedSqlException.AssemblyFailedVerification(verb, assemblyName, ": P/Invoke declarations are not allowed in SAFE assemblies.");

        foreach (var handle in metadata.TypeReferences)
        {
            var typeReference = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(typeReference.Namespace);
            var fullName = ns.Length == 0
                ? metadata.GetString(typeReference.Name)
                : $"{ns}.{metadata.GetString(typeReference.Name)}";

            if (deniedTypes.Contains(fullName) || IsDeniedNamespace(ns))
                throw SimulatedSqlException.AssemblyFailedVerification(verb, assemblyName, $": type '{fullName}' is not allowed in SAFE assemblies.");
        }

        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var typeDefinition = metadata.GetTypeDefinition(typeHandle);
            foreach (var fieldHandle in typeDefinition.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                const FieldAttributes mutableStatic = FieldAttributes.Static;
                if ((field.Attributes & mutableStatic) == 0)
                    continue;
                if ((field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) != 0)
                    continue;

                throw SimulatedSqlException.AssemblyMutableStaticField(
                    verb,
                    metadata.GetString(typeDefinition.Name),
                    "SAFE",
                    assemblyName,
                    metadata.GetString(field.Name));
            }
        }
    }

    private static bool IsDeniedNamespace(string ns) => Array.Exists(
        deniedNamespacePrefixes,
        prefix => ns.Equals(prefix, StringComparison.Ordinal) || ns.StartsWith(prefix + ".", StringComparison.Ordinal));

    /// <summary>
    /// The lowercase full identity real SQL Server names in Msg 6503, including
    /// its trailing period inside the quotes.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Msg 6503 names the reference in lowercase in real SQL Server; this reproduces that text rather than normalizing for comparison.")]
    private static string DescribeReference(MetadataReader metadata, AssemblyReferenceHandle handle)
    {
        var reference = metadata.GetAssemblyReference(handle);
        var token = reference.PublicKeyOrToken.IsNil
            ? "null"
            : Convert.ToHexStringLower(metadata.GetBlobBytes(reference.PublicKeyOrToken));
        var culture = reference.Culture.IsNil || metadata.GetString(reference.Culture).Length == 0
            ? "neutral"
            : metadata.GetString(reference.Culture).ToLowerInvariant();

        return $"{metadata.GetString(reference.Name).ToLowerInvariant()}, version={reference.Version}, culture={culture}, publickeytoken={token}.";
    }

    /// <summary>
    /// Opens the image for metadata-only inspection. Ownership of the reader
    /// transfers to the caller — every <see cref="MetadataReader"/> it hands
    /// out points into memory the <see cref="PEReader"/> owns, so the reader
    /// must outlive the metadata borrow.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the caller, which disposes via using; the returned MetadataReader borrows memory this object owns.")]
    private static PEReader OpenPortableExecutable(byte[] content, string verb, string assemblyName)
    {
        PEReader? peReader = null;
        try
        {
            peReader = new PEReader(new MemoryStream(content, writable: false));
            return peReader.HasMetadata && peReader.GetMetadataReader().IsAssembly
                ? peReader
                : throw SimulatedSqlException.AssemblyMalformed(verb, assemblyName, NativeStubDetail);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or IOException)
        {
            peReader?.Dispose();
            throw SimulatedSqlException.AssemblyMalformed(verb, assemblyName, NativeStubDetail);
        }
        catch
        {
            peReader?.Dispose();
            throw;
        }
    }
}
