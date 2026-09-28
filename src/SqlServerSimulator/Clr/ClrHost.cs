using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace SqlServerSimulator.Clr;

/// <summary>
/// The load-context side of SQLCLR hosting: the two assemblies a registered
/// assembly resolves in place of the ones .NET no longer ships, and the bridge
/// into the <c>Microsoft.SqlServer.Server</c> surface they supply.
/// </summary>
/// <remarks>
/// <para>
/// A .NET Framework SQLCLR assembly references <c>System.Data, Version=4.0.0.0</c>
/// for <c>System.Data.SqlTypes</c>, <c>Microsoft.SqlServer.Server</c> and the
/// in-process provider's <c>System.Data.SqlClient</c> types. .NET's own
/// <c>System.Data</c> facade forwards the first family onward but sends the
/// other two to a <c>System.Data.SqlClient</c> assembly that does not exist,
/// and omits <c>SqlContext</c> / <c>SqlPipe</c> entirely. So every
/// <see cref="SqlAssemblyLoadContext"/> answers <c>System.Data</c> with a
/// substitute generated here — the facade's own forwarders, each pointed at
/// the assembly it actually resolves to, plus one forwarder per public type of
/// the embedded <c>Microsoft.SqlServer.Server</c> build (the
/// <c>SqlServerSimulator.ClrShim</c> project). Forwarding rather than copying
/// keeps <c>SqlInt32</c> and friends the very types the simulator's marshaller
/// handles.
/// </para>
/// <para>
/// Both assemblies load once per process into a non-collectible context every
/// registered assembly's collectible context shares, so dropping an assembly
/// never unloads them and their thread-static routine context is one per
/// thread. Nothing here is built until an assembly that references
/// <c>System.Data</c> or <c>Microsoft.SqlServer.Server</c> first loads.
/// </para>
/// </remarks>
internal static class ClrHost
{
    private const string ServerAssemblyName = "Microsoft.SqlServer.Server";
    private const string SqlClientNamespace = "System.Data.SqlClient";
    private const string SystemDataAssemblyName = "System.Data";

    private static readonly Lazy<HostContext> host = new(() => new HostContext());

    /// <summary>
    /// Whether an assembly referencing <paramref name="name"/> needs this host —
    /// the two names <see cref="Resolve"/> answers.
    /// </summary>
    public static bool Hosts(AssemblyName name) =>
        string.Equals(name.Name, SystemDataAssemblyName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name.Name, ServerAssemblyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The substitute for <paramref name="name"/>, or <see langword="null"/> to
    /// let the default context resolve it.
    /// </summary>
    public static Assembly? Resolve(AssemblyName name) =>
        string.Equals(name.Name, SystemDataAssemblyName, StringComparison.OrdinalIgnoreCase) ? host.Value.SystemData
        : string.Equals(name.Name, ServerAssemblyName, StringComparison.OrdinalIgnoreCase) ? host.Value.Server
        : null;

    /// <summary>
    /// Whether <paramref name="assembly"/> is the embedded
    /// <c>Microsoft.SqlServer.Server</c> build, without building it.
    /// </summary>
    public static bool IsServerAssembly(Assembly assembly) =>
        host.IsValueCreated && assembly == host.Value.Server;

    /// <summary>
    /// Opens a routine's <c>SqlContext</c> on this thread; dispose to close it.
    /// A procedure or trigger passes its <paramref name="pipe"/>; a function,
    /// aggregate or type member passes <see langword="null"/> and its routine
    /// sees no pipe. A trigger also passes what its <c>SqlTriggerContext</c>
    /// reports, and a routine that may read data the
    /// <paramref name="contextConnection"/> its commands run through.
    /// </summary>
    public static RoutineScope Enter(ClrPipeSink? pipe, (int Action, bool[] UpdatedColumns, string? EventData)? trigger = null, Simulation.ClrContextConnection? contextConnection = null) =>
        new(host.Value, pipe, trigger, contextConnection);

    /// <summary>The <see cref="Enter"/> / dispose pair around one routine call.</summary>
    public readonly struct RoutineScope : IDisposable
    {
        private readonly HostContext context;
        private readonly object? previous;

        internal RoutineScope(HostContext context, ClrPipeSink? pipe, (int Action, bool[] UpdatedColumns, string? EventData)? trigger, Simulation.ClrContextConnection? contextConnection)
        {
            this.context = context;
            (Func<ContextCommand, ContextResult>, Action<object, int>, Func<string>, string, bool)? data = contextConnection is null
                ? null
                : (contextConnection.Execute, contextConnection.Send, contextConnection.DatabaseName, contextConnection.ServerVersion, contextConnection.Safe);
            this.previous = pipe is null
                ? context.Enter(null, null, null, null, null, data)
                : context.Enter(pipe.Message, pipe.Start, pipe.Row, pipe.End, trigger, data);
        }

        public void Dispose() => this.context.Exit(this.previous);
    }

    /// <summary>
    /// The shared, non-collectible context holding the two substitute
    /// assemblies, and the bridge entry points bound out of the server one.
    /// </summary>
    internal sealed class HostContext : AssemblyLoadContext
    {
        public readonly Assembly Server;
        public readonly Assembly SystemData;
        public readonly Func<
            Action<string>?,
            Action<(string, SqlDbType, long, byte, byte)[]>?,
            Action<object?[]>?,
            Action?,
            (int, bool[], string?)?,
            (Func<ContextCommand, ContextResult>, Action<object, int>, Func<string>, string, bool)?,
            object?> Enter;
        public readonly Action<object?> Exit;

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026:RequiresUnreferencedCode",
            Justification = "The bridge type lives in an embedded assembly loaded at run time, outside the application's static closure, so trimming cannot remove its members.")]
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075:DynamicallyAccessedMembers",
            Justification = "The bridge type lives in an embedded assembly loaded at run time, outside the application's static closure, so trimming cannot remove its members.")]
        public HostContext()
            : base("SqlClr host", isCollectible: false)
        {
            using (var stream = typeof(ClrHost).Assembly.GetManifestResourceStream(ServerAssemblyName + ".dll")
                ?? throw new InvalidOperationException("The embedded Microsoft.SqlServer.Server assembly is missing from this build."))
            {
                this.Server = this.LoadFromStream(stream);
            }

            this.SystemData = this.LoadFromStream(new MemoryStream(BuildSystemDataShim(this.Server), writable: false));

            var bridge = this.Server.GetType(ServerAssemblyName + ".SimulatorBridge", throwOnError: true)!;
            this.Enter = bridge.GetMethod("Enter", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<
                    Action<string>?,
                    Action<(string, SqlDbType, long, byte, byte)[]>?,
                    Action<object?[]>?,
                    Action?,
                    (int, bool[], string?)?,
                    (Func<ContextCommand, ContextResult>, Action<object, int>, Func<string>, string, bool)?,
                    object?>>();
            this.Exit = bridge.GetMethod("Exit", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Action<object?>>();
        }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            string.Equals(assemblyName.Name, ServerAssemblyName, StringComparison.OrdinalIgnoreCase) ? this.Server : null;
    }

    /// <summary>
    /// Emits the substitute <c>System.Data</c>: a manifest-only assembly whose
    /// every type is a forwarder. The runtime's own facade supplies the list;
    /// each entry it can resolve forwards to the assembly the type really lives
    /// in, and the <c>Microsoft.SqlServer.Server</c> and
    /// <c>System.Data.SqlClient</c> namespaces — whose facade entries point at
    /// the missing client assembly — forward to <paramref name="server"/>
    /// instead, including the types the facade leaves out.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Enumerates the System.Data facade's forwarders by name to re-emit them; no member of the forwarded types is invoked.")]
    private static byte[] BuildSystemDataShim(Assembly server)
    {
        Type?[] forwarded;
        try
        {
            forwarded = Assembly.Load(new AssemblyName(SystemDataAssemblyName)).GetForwardedTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            // The Microsoft.SqlServer.Server entries cannot resolve; everything
            // else still arrives in Types.
            forwarded = partial.Types;
        }

        var types = new List<Type>();
        foreach (var type in forwarded)
        {
            if (type is not null && type.Namespace is not (ServerAssemblyName or SqlClientNamespace))
                types.Add(type);
        }

        foreach (var type in server.GetExportedTypes())
        {
            if (type.Namespace is ServerAssemblyName or SqlClientNamespace)
                types.Add(type);
        }

        // An enclosing type's row must exist before its nested types point at it.
        types.Sort((a, b) => NestingDepth(a).CompareTo(NestingDepth(b)));

        var metadata = new MetadataBuilder();
        _ = metadata.AddAssembly(
            metadata.GetOrAddString(SystemDataAssemblyName),
            new Version(4, 0, 0, 0),
            default,
            default,
            default,
            AssemblyHashAlgorithm.Sha1);
        _ = metadata.AddModule(0, metadata.GetOrAddString(SystemDataAssemblyName + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        _ = metadata.AddTypeDefinition(
            default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var references = new Dictionary<Assembly, AssemblyReferenceHandle>();
        var exported = new Dictionary<Type, ExportedTypeHandle>();
        foreach (var type in types)
        {
            if (type.DeclaringType is { } enclosing)
            {
                if (!exported.TryGetValue(enclosing, out var parent))
                    continue;
                exported[type] = metadata.AddExportedType(
                    TypeAttributes.NestedPublic, default, metadata.GetOrAddString(type.Name), parent, 0);
                continue;
            }

            if (!references.TryGetValue(type.Assembly, out var reference))
            {
                var name = type.Assembly.GetName();
                var token = name.GetPublicKeyToken();
                reference = metadata.AddAssemblyReference(
                    metadata.GetOrAddString(name.Name!),
                    name.Version ?? new Version(0, 0, 0, 0),
                    string.IsNullOrEmpty(name.CultureName) ? default : metadata.GetOrAddString(name.CultureName),
                    token is { Length: > 0 } ? metadata.GetOrAddBlob(token) : default,
                    default,
                    default);
                references[type.Assembly] = reference;
            }

            // 0x00200000 is ECMA-335's forwarder bit, which TypeAttributes does
            // not name.
            exported[type] = metadata.AddExportedType(
                (TypeAttributes)0x00200000,
                metadata.GetOrAddString(type.Namespace ?? ""),
                metadata.GetOrAddString(type.Name),
                reference,
                0);
        }

        var image = new BlobBuilder();
        _ = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            new BlobBuilder()).Serialize(image);
        return image.ToArray();
    }

    private static int NestingDepth(Type type)
    {
        var depth = 0;
        for (var enclosing = type.DeclaringType; enclosing is not null; enclosing = enclosing.DeclaringType)
            depth++;
        return depth;
    }
}

/// <summary>
/// The collectible context one registered assembly loads into. It answers
/// <c>System.Data</c> and <c>Microsoft.SqlServer.Server</c> from
/// <see cref="ClrHost"/> and leaves every other reference to the default
/// context.
/// </summary>
internal sealed class SqlAssemblyLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => ClrHost.Resolve(assemblyName);
}

/// <summary>
/// Where a CLR procedure's <c>SqlContext.Pipe</c> delivers what it sends —
/// the four callbacks the embedded pipe invokes, as framework delegate types.
/// </summary>
internal sealed class ClrPipeSink(
    Action<string> message,
    Action<(string, SqlDbType, long, byte, byte)[]> start,
    Action<object?[]> row,
    Action end)
{
    public readonly Action<string> Message = message;
    public readonly Action<(string, SqlDbType, long, byte, byte)[]> Start = start;
    public readonly Action<object?[]> Row = row;
    public readonly Action End = end;
}
