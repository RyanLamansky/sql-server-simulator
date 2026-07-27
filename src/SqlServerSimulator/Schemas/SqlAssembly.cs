using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using SqlServerSimulator.Clr;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The <c>PERMISSION_SET</c> a <see cref="SqlAssembly"/> was registered with.
/// The numeric values are the ones <c>sys.assemblies.permission_set</c>
/// projects; <see cref="SqlAssembly.PermissionSetDescription"/> maps them to
/// the <c>permission_set_desc</c> strings (note the <c>_ACCESS</c> suffix on
/// all three, which real SQL Server carries even for <c>SAFE</c>).
/// </summary>
internal enum AssemblyPermissionSet : byte
{
    Safe = 1,
    ExternalAccess = 2,
    Unsafe = 3,
}

/// <summary>
/// One registered CLR assembly — the <c>CREATE ASSEMBLY</c> unit. Assemblies
/// are database-scoped (not schema-scoped), so they live in
/// <see cref="Database.Assemblies"/> keyed by name rather than in a
/// <see cref="Schema"/>, and carry an <c>assembly_id</c> rather than an
/// <c>object_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// The raw bytes are retained verbatim so <c>sys.assembly_files.content</c>
/// round-trips exactly what <c>CREATE ASSEMBLY … FROM 0x…</c> supplied. The
/// executable form is materialized lazily on first invocation into a
/// dedicated collectible <see cref="AssemblyLoadContext"/>, so
/// <c>DROP ASSEMBLY</c> can <see cref="Unload"/> it and a subsequent
/// re-<c>CREATE</c> of the same name starts from a clean load context rather
/// than resurrecting the old types.
/// </para>
/// <para>
/// <strong>Never read custom attributes off the loaded assembly.</strong> A
/// SQLCLR assembly built against .NET Framework decorates its routines with
/// <c>Microsoft.SqlServer.Server.SqlFunctionAttribute</c>, which type-forwards
/// to <c>System.Data.SqlClient</c> — an assembly that does not exist on
/// modern .NET, so any <c>GetCustomAttributes</c> call throws
/// <see cref="FileNotFoundException"/>. Method binding goes through the
/// <c>EXTERNAL NAME</c> triple instead, which names the type and method
/// directly and needs no attribute resolution.
/// </para>
/// </remarks>
internal sealed class SqlAssembly(
    string name,
    int assemblyId,
    byte[] content,
    AssemblyPermissionSet permissionSet,
    int principalId,
    DateTime createDate)
{
    public readonly string Name = name;
    public readonly int AssemblyId = assemblyId;

    /// <summary>
    /// The verbatim bytes supplied to <c>CREATE ASSEMBLY … FROM</c>, surfaced
    /// unchanged by <c>sys.assembly_files.content</c>.
    /// </summary>
    public readonly byte[] Content = content;

    public readonly AssemblyPermissionSet PermissionSet = permissionSet;

    /// <summary>Owning database principal — <c>dbo</c> (1) unless
    /// <c>AUTHORIZATION</c> named another.</summary>
    public readonly int PrincipalId = principalId;

    public readonly DateTime CreateDate = createDate;
    public DateTime ModifyDate = createDate;

    /// <summary>
    /// The assembly's simple name / version / culture / public key token as
    /// the manifest declares them, in the lowercase
    /// <c>name, version=…, culture=…, publickeytoken=…, processorarchitecture=msil</c>
    /// shape <c>sys.assemblies.clr_name</c> projects.
    /// </summary>
    public readonly string ClrName = ClrAssemblyMetadata.BuildClrName(content);

    private readonly Lock loadLock = new();
    private AssemblyLoadContext? loadContext;
    private Assembly? loaded;

    /// <summary>
    /// Materializes (once) and returns the executable form. The load context
    /// is collectible so <see cref="Unload"/> can release it.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The assembly is supplied as bytes at run time by CREATE ASSEMBLY, so it is not part of the application's static closure and trimming cannot remove anything it needs.")]
    public Assembly Load()
    {
        lock (this.loadLock)
        {
            if (this.loaded is not null)
                return this.loaded;

            var context = new AssemblyLoadContext($"SqlAssembly:{this.Name}", isCollectible: true);
            try
            {
                this.loaded = context.LoadFromStream(new MemoryStream(this.Content, writable: false));
            }
            catch
            {
                context.Unload();
                throw;
            }

            this.loadContext = context;
            return this.loaded;
        }
    }

    /// <summary>
    /// Releases the load context, if one was materialized. Called by
    /// <c>DROP ASSEMBLY</c>. Unloading is cooperative — the CLR reclaims the
    /// context once no managed references into it survive — so this only
    /// starts the process; correctness never depends on it completing.
    /// </summary>
    public void Unload()
    {
        lock (this.loadLock)
        {
            this.loadContext?.Unload();
            this.loadContext = null;
            this.loaded = null;
        }
    }

    /// <summary>The <c>sys.assemblies.permission_set_desc</c> string.</summary>
    public string PermissionSetDescription => this.PermissionSet switch
    {
        AssemblyPermissionSet.Safe => "SAFE_ACCESS",
        AssemblyPermissionSet.ExternalAccess => "EXTERNAL_ACCESS",
        _ => "UNSAFE_ACCESS",
    };
}
