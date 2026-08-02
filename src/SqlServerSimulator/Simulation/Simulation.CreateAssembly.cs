using System.Reflection;
using SqlServerSimulator.Clr;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses
    /// <c>CREATE ASSEMBLY name [AUTHORIZATION owner] FROM 0x… [WITH PERMISSION_SET = {SAFE|EXTERNAL_ACCESS|UNSAFE}]</c>.
    /// Cursor on entry: the <c>ASSEMBLY</c> word (matched by the CREATE
    /// dispatcher).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes are validated by <see cref="ClrAssemblyMetadata.Verify"/>
    /// before anything is loaded, so a rejected candidate never gets to run a
    /// module initializer. Registration itself does not load the assembly at
    /// all — that happens lazily on first invocation.
    /// </para>
    /// <para>
    /// Real SQL Server accepts <c>FROM '&lt;path&gt;'</c> as well; the
    /// simulator has no server-side filesystem to read from, so only the
    /// binary-literal form is modeled.
    /// </para>
    /// <para>
    /// <c>clr enabled</c> does <em>not</em> gate this statement on real SQL
    /// Server — probe-confirmed, <c>CREATE ASSEMBLY</c> succeeds with the
    /// option off and only execution raises Msg 6263. The simulator's
    /// host-level <see cref="EnableClr"/> gate is stricter on purpose: it
    /// governs whether foreign bytes may be registered at all.
    /// </para>
    /// </remarks>
    private static bool TryParseCreateAssembly(ParserContext context)
    {
        context.MoveNextRequired();
        var assemblyName = BatchContext.ParseObjectName(context).Leaf;

        context.MoveNextRequired();
        if (context.Token is Name authorizationWord && authorizationWord.Value.Equals("AUTHORIZATION", StringComparison.OrdinalIgnoreCase))
        {
            // AUTHORIZATION <owner> parses and is discarded — schema/assembly
            // ownership by a named principal isn't modeled, so every assembly
            // is owned by dbo.
            context.MoveNextRequired();
            _ = BatchContext.ParseObjectName(context);
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var contentToken = context.GetNextRequired();
        if (contentToken is not Literal { Value: { IsNull: false } content } || content.Type is not VarbinarySqlType)
        {
            throw contentToken is Literal
                ? new NotSupportedException("CREATE ASSEMBLY ... FROM '<path>' is not modeled — the simulator has no server-side filesystem. Supply the assembly as a binary literal (FROM 0x...).")
                : SimulatedSqlException.SyntaxErrorNear(context);
        }

        var permissionSet = AssemblyPermissionSet.Safe;
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Name permissionWord
                || !permissionWord.Value.Equals("PERMISSION_SET", StringComparison.OrdinalIgnoreCase))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            permissionSet = context.GetNextRequired() switch
            {
                Name { Value: var word } when word.Equals("SAFE", StringComparison.OrdinalIgnoreCase) => AssemblyPermissionSet.Safe,
                Name { Value: var word } when word.Equals("EXTERNAL_ACCESS", StringComparison.OrdinalIgnoreCase) => AssemblyPermissionSet.ExternalAccess,
                Name { Value: var word } when word.Equals("UNSAFE", StringComparison.OrdinalIgnoreCase) => AssemblyPermissionSet.Unsafe,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        // Database-scope CREATE ASSEMBLY gate (Msg 262 state 1) — probe-confirmed
        // ahead of the assembly-content checks.
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.CreateAssembly))
            throw SimulatedSqlException.DatabasePermissionDenied("CREATE ASSEMBLY", context.CurrentDatabase.Name);

        RegisterAssembly(context, assemblyName, content.AsBytes, permissionSet);
        return true;
    }

    /// <summary>
    /// Validates and records one assembly. Shared by <c>CREATE ASSEMBLY</c> and
    /// any other registration path.
    /// </summary>
    private static void RegisterAssembly(ParserContext context, string assemblyName, byte[] content, AssemblyPermissionSet permissionSet)
    {
        if (!context.Batch.Connection.Simulation.EnableClr)
        {
            throw new NotSupportedException(
                $"CREATE ASSEMBLY is disabled. Registering '{assemblyName}' would load and run its code inside this process, so it requires an explicit opt-in: set EnableClr on the Simulation (new Simulation {{ EnableClr = true }}).");
        }

        var database = context.CurrentDatabase;
        if (database.Assemblies.ContainsKey(assemblyName))
            throw SimulatedSqlException.AssemblyAlreadyExists(assemblyName, database.Name);

        ClrAssemblyMetadata.Verify(content, assemblyName, permissionSet, "CREATE");

        var identity = ClrAssemblyMetadata.ReadIdentity(content, "CREATE", assemblyName);
        foreach (var existing in database.Assemblies.Values)
        {
            if (ClrAssemblyMetadata.ReadIdentity(existing.Content, "CREATE", existing.Name).Mvid == identity.Mvid)
                throw SimulatedSqlException.AssemblyDuplicateMvid("CREATE", existing.Name);
        }

        _ = database.Assemblies.TryAdd(assemblyName, new SqlAssembly(
            assemblyName,
            database.AllocateAssemblyId(),
            content,
            permissionSet,
            principalId: 1,
            context.Batch.CurrentStatement.UtcNow));
    }

    /// <summary>
    /// Parses the <c>EXTERNAL NAME assembly.[class].method</c> tail of a
    /// <c>CREATE FUNCTION</c>, binds the named method, and registers the
    /// resulting <see cref="ClrScalarFunction"/>. Cursor on entry: the
    /// <c>EXTERNAL</c> word.
    /// </summary>
    /// <remarks>
    /// Binding happens here rather than at first call so the Msg 6505 / 6506 /
    /// 6550 / 6551 / 6552 diagnostics fire at CREATE time, matching real SQL
    /// Server. The class segment is commonly bracketed
    /// (<c>asm.[Namespace.Class].Method</c>) because the namespace-qualified
    /// form contains dots; the object-name parser keeps a bracketed segment
    /// whole, so both spellings land in one part.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "The target assembly is registered from bytes at run time, so it is outside the application's static closure and trimming cannot affect its members.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Assembly.GetType resolves against bytes supplied at run time by CREATE ASSEMBLY; the type name comes from the EXTERNAL NAME clause, and trimming cannot reach into an assembly outside the application's static closure.")]
    private static bool ParseClrScalarTail(
        ParserContext context,
        Schema schema,
        MultiPartName functionName,
        List<UdfParameter> parameters,
        SqlType returnType,
        bool isSchemaBound,
        bool isAlter,
        bool createOrAlter)
    {
        if (context.GetNextRequired() is not Name nameWord || !nameWord.Value.Equals("NAME", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var externalName = BatchContext.ParseObjectName(context);
        if (externalName.Count != 3)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        CheckModuleDdlPermission(
            context, "CREATE FUNCTION", functionName, schema, isAlter, createOrAlter,
            schema.Functions.GetValueOrDefault(functionName.Leaf));

        var replaced = ResolveFunctionAlterTarget<ClrScalarFunction>(context, schema, functionName, isAlter, createOrAlter);

        var database = context.CurrentDatabase;
        var assemblyName = externalName[0];
        var className = externalName[1];
        var methodName = externalName[2];

        if (!database.Assemblies.TryGetValue(assemblyName, out var assembly))
            throw SimulatedSqlException.AssemblyNotFoundInDatabase(assemblyName, database.Name);

        if (!context.Batch.Connection.Simulation.EnableClr)
            throw SimulatedSqlException.ClrExecutionDisabled();

        var type = assembly.Load().GetType(className)
            ?? throw SimulatedSqlException.ClrTypeNotFound(className, assemblyName);
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw SimulatedSqlException.ClrMethodNotFound(methodName, className, assemblyName);

        var methodParameters = method.GetParameters();
        if (methodParameters.Length != parameters.Count)
            throw SimulatedSqlException.ClrParameterCountMismatch("CREATE FUNCTION");

        if (!ClrTypeMarshaller.Matches(returnType, method.ReturnType))
            throw SimulatedSqlException.ClrReturnTypeMismatch("CREATE FUNCTION", functionName.Leaf);

        for (var i = 0; i < methodParameters.Length; i++)
        {
            if (!ClrTypeMarshaller.Matches(parameters[i].Type, methodParameters[i].ParameterType))
                throw SimulatedSqlException.ClrParameterTypeMismatch("CREATE FUNCTION", functionName.Leaf, "@" + parameters[i].Name);
        }

        var clrFunction = new ClrScalarFunction(
            schema,
            functionName.Leaf,
            replaced?.ObjectId ?? database.AllocateObjectId(),
            [.. parameters],
            returnType,
            assembly,
            className,
            methodName,
            method,
            replaced?.CreateDate ?? context.Batch.CurrentStatement.UtcNow)
        {
            IsSchemaBound = isSchemaBound,
        };
        if (replaced is not null)
            clrFunction.ModifyDate = context.Batch.CurrentStatement.UtcNow;
        schema.Functions[functionName.Leaf] = clrFunction;
        return true;
    }

    /// <summary>
    /// Parses <c>DROP ASSEMBLY [IF EXISTS] name [, …] [WITH NO DEPENDENTS]</c>.
    /// Cursor on entry: the <c>ASSEMBLY</c> word (matched by the DROP
    /// dispatcher). An assembly still bound by a CLR routine raises Msg 6590
    /// unless <c>WITH NO DEPENDENTS</c> was given.
    /// </summary>
    private static bool TryParseDropAssembly(ParserContext context)
    {
        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                return false;
            ifExists = true;
            context.MoveNextRequired();
        }

        List<string> names = [];
        while (true)
        {
            names.Add(BatchContext.ParseObjectName(context).Leaf);
            context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }

        var checkDependents = true;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Name noWord || !noWord.Value.Equals("NO", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not Name dependentsWord || !dependentsWord.Value.Equals("DEPENDENTS", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            checkDependents = false;
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        foreach (var name in names)
        {
            if (!database.Assemblies.TryGetValue(name, out var assembly))
            {
                if (ifExists)
                    continue;
                throw SimulatedSqlException.AssemblyNotFoundInDatabase(name, database.Name);
            }

            if (checkDependents && FindClrDependent(database, assembly) is string dependent)
                throw SimulatedSqlException.DropAssemblyHasDependent(name, dependent);

            if (database.Assemblies.TryRemove(name, out var removed))
                removed.Unload();
        }

        return true;
    }

    /// <summary>
    /// The name of the first CLR routine bound to <paramref name="assembly"/>,
    /// or <see langword="null"/> when nothing references it.
    /// </summary>
    private static string? FindClrDependent(Database database, SqlAssembly assembly)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var function in schema.Functions.Values)
            {
                if (function is ClrScalarFunction clr && clr.Assembly == assembly)
                    return clr.Name;
            }
        }

        return null;
    }
}
