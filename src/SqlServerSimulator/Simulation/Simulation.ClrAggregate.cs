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
    /// Parses <c>CREATE AGGREGATE [schema.]name (@p type [, …]) RETURNS type
    /// EXTERNAL NAME assembly.[class]</c>, binds the class, and stores the
    /// aggregate among the schema's functions. Cursor on entry: the
    /// <c>AGGREGATE</c> word.
    /// </summary>
    /// <remarks>
    /// Probed 2026-09-28 against SQL Server 2025. The parameter list's
    /// parentheses are required, <c>EXTERNAL NAME</c> follows <c>RETURNS</c>
    /// with no <c>AS</c>, and names a class alone — a method segment is Msg
    /// 102 at its dot. A parameter default is Msg 10726. Binding reports, in
    /// order: a missing class (Msg 6556), a class without
    /// <c>SqlUserDefinedAggregate</c> (Msg 6255), a <c>Format.Native</c> class
    /// holding a reference-type field (Msg 6225), then the first of
    /// <c>Init</c> / <c>Accumulate</c> / <c>Merge</c> / <c>Terminate</c> that
    /// is missing or misshaped (Msg 6558, <c>Accumulate</c> taking another
    /// argument count and <c>Terminate</c> returning another type included),
    /// and last an <c>Accumulate</c> parameter whose type doesn't bind (Msg
    /// 6552). Msgs 6556 and 6558 are followed by Msg 6597. None of these
    /// errors names a procedure.
    /// </remarks>
    private static bool TryParseCreateAggregate(ParserContext context)
    {
        context.MoveNextRequired();
        var aggregateName = BatchContext.ParseObjectName(context);
        RejectQualifiedModuleName(aggregateName, "AGGREGATE");
        var schema = ResolveModuleSchema(context, aggregateName, isAlter: false);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var parameters = new List<UdfParameter>();
        var declarationErrors = new List<SimulatedSqlException>();
        while (true)
        {
            var parameter = ParseParameter(context, parameters.Count + 1, declarationErrors);
            if (parameter.Default is not null)
                throw SimulatedSqlException.ClrAggregateDefaultParameter();
            parameters.Add(parameter);
            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        if (HeldDeclarationErrors(declarationErrors) is { } held)
            throw held;

        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Returns })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var returnType = ParseFunctionReturnType(context, ordinal: 0, parameterName: "", out _, out _);

        if (context.Token is ReservedKeyword { Keyword: Keyword.As } asKeyword)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(asKeyword);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.External }
            || context.GetNextRequired() is not Name nameWord
            || !nameWord.Value.Equals("NAME", StringComparison.OrdinalIgnoreCase)
            || context.GetNextRequired() is not Name assemblyToken
            || context.GetNextRequired() is not Operator { Character: '.' }
            || context.GetNextRequired() is not Name classToken)
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '.' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.Batch.IsSkipping)
            return true;

        context.CurrentDatabase.RejectWriteWhenReadOnly();
        PermissionEnforcement.CheckCreateModule(context.Batch, "CREATE AGGREGATE", aggregateName.Leaf, schema);
        _ = ResolveModuleAlterTarget(context, schema, aggregateName, isAlter: false, createOrAlter: false, existingOfDeclaredKind: null);

        var className = classToken.Value;
        var (assembly, type) = ResolveClrClass(context, assemblyToken.Value, className, forAggregate: true);
        var (init, accumulate, terminate, serialization) = BindAggregateClass(assembly, type, className, aggregateName.Leaf, parameters, returnType);

        var aggregate = new ClrAggregateFunction(
            schema,
            aggregateName.Leaf,
            context.CurrentDatabase.AllocateObjectId(),
            [.. parameters],
            returnType,
            new ClrEntryPoint(assembly, className, methodName: null, type, method: null),
            init,
            accumulate,
            terminate,
            serialization,
            context.Batch.CurrentStatement.UtcNow);
        schema.Functions[aggregateName.Leaf] = aggregate;
        RecordSlotUndo(context, schema.Functions, aggregateName.Leaf, previous: null);
        return true;
    }

    /// <summary>
    /// Checks an aggregate class against the user-defined aggregate contract,
    /// answering its <c>Init</c>, <c>Accumulate</c> and <c>Terminate</c>
    /// methods; see <see cref="TryParseCreateAggregate"/> for the errors.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:DynamicallyAccessedMembers",
        Justification = "The type comes from an assembly registered from bytes at run time, outside the application's static closure, so trimming cannot affect its members.")]
    private static (MethodInfo Init, MethodInfo Accumulate, MethodInfo Terminate, (MethodInfo Write, MethodInfo Read)? Serialization) BindAggregateClass(
        SqlAssembly assembly, Type type, string className, string aggregateName, List<UdfParameter> parameters, SqlType returnType)
    {
        if (ClrAttributes.Find(type, ClrAttributes.SqlUserDefinedAggregate) is not { } attribute)
            throw SimulatedSqlException.ClrAggregateMissingAttribute(className);

        // Format.Native is 1 in the attribute's constructor argument.
        if (attribute.ConstructorArguments is [{ Value: 1 }])
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!field.FieldType.IsValueType)
                {
                    var fieldAssembly = field.FieldType.Assembly == typeof(object).Assembly ? "mscorlib" : field.FieldType.Assembly.GetName().Name;
                    throw SimulatedSqlException.ClrNativeFormatField(assembly.Name, type.FullName!, field.Name, $"{fieldAssembly}.{field.FieldType.FullName}");
                }
            }
        }

        const BindingFlags instanceMethods = BindingFlags.Instance | BindingFlags.Public;
        SimulatedSqlException NonConforming(string methodName) => SimulatedSqlException.Aggregate([
            SimulatedSqlException.ClrAggregateMethodNonConforming(className, methodName),
            SimulatedSqlException.ClrAggregateFailed()]);

        var init = type.GetMethod("Init", instanceMethods, Type.EmptyTypes);
        if (init is null || init.ReturnType != typeof(void))
            throw NonConforming("Init");

        MethodInfo? accumulate = null;
        foreach (var candidate in type.GetMethods(instanceMethods))
        {
            if (candidate.Name == "Accumulate" && candidate.GetParameters().Length == parameters.Count && candidate.ReturnType == typeof(void))
                accumulate = candidate;
        }
        if (accumulate is null)
            throw NonConforming("Accumulate");

        var merge = type.GetMethod("Merge", instanceMethods, [type]);
        if (merge is null || merge.ReturnType != typeof(void))
            throw NonConforming("Merge");

        var terminate = type.GetMethod("Terminate", instanceMethods, Type.EmptyTypes);
        if (terminate is null || !ClrTypeMarshaller.Matches(returnType, terminate.ReturnType))
            throw NonConforming("Terminate");

        var accumulateParameters = accumulate.GetParameters();
        for (var i = 0; i < accumulateParameters.Length; i++)
        {
            if (!ClrTypeMarshaller.Matches(parameters[i].Type, accumulateParameters[i].ParameterType))
                throw SimulatedSqlException.ClrParameterTypeMismatch("CREATE", aggregateName, "@" + parameters[i].Name);
        }

        return (init, accumulate, terminate, UserDefinedSerialization(type, attribute));
    }

    /// <summary>
    /// A <c>Format.UserDefined</c> aggregate's <c>IBinarySerialize.Write</c>
    /// and <c>Read</c>, through which real passes every group's state before
    /// <c>Terminate</c>; null for <c>Format.Native</c>, whose field-by-field
    /// copy loses nothing.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:DynamicallyAccessedMembers",
        Justification = "The type comes from an assembly registered from bytes at run time, outside the application's static closure, so trimming cannot affect its members.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2072:DynamicallyAccessedMembers",
        Justification = "The interface is the shim's IBinarySerialize, which the registered class implements; neither is in the application's static closure, so trimming cannot affect their methods.")]
    private static (MethodInfo Write, MethodInfo Read)? UserDefinedSerialization(Type type, CustomAttributeData attribute)
    {
        // Format.UserDefined is 2 in the attribute's constructor argument.
        if (attribute.ConstructorArguments is not [{ Value: 2 }])
            return null;
        foreach (var contract in type.GetInterfaces())
        {
            if (contract.FullName != "Microsoft.SqlServer.Server.IBinarySerialize")
                continue;
            var map = type.GetInterfaceMap(contract);
            MethodInfo? write = null;
            MethodInfo? read = null;
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == "Write")
                    write = map.TargetMethods[i];
                else if (map.InterfaceMethods[i].Name == "Read")
                    read = map.TargetMethods[i];
            }
            return write is not null && read is not null ? (write, read) : null;
        }
        return null;
    }
}
