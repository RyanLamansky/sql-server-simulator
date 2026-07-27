namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 6544: the bytes supplied to
    /// <c>CREATE ASSEMBLY … FROM</c> are not a loadable pure-IL managed
    /// assembly. The trailing detail sentence is a separate format argument
    /// on the real message, which is why it reads as its own line.
    /// </summary>
    internal static SimulatedSqlException AssemblyMalformed(string verb, string assemblyName, string detail) =>
        new($"{verb} ASSEMBLY for assembly '{assemblyName}' failed because assembly '{assemblyName}' is malformed or not a pure .NET assembly. {detail}", 6544, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6218: the assembly did not pass verification.
    /// The simulator raises this for the static <c>SAFE</c> checks that have
    /// no more specific message — P/Invoke declarations and references into
    /// framework namespaces a <c>SAFE</c> assembly may not reach.
    /// </summary>
    internal static SimulatedSqlException AssemblyFailedVerification(string verb, string assemblyName, string detail) =>
        new($"{verb} ASSEMBLY for assembly '{assemblyName}' failed because assembly '{assemblyName}' failed verification. Check if the referenced assemblies are up-to-date and trusted (for external_access or unsafe) to execute in the database. CLR Verifier error messages if any will follow this message{detail}", 6218, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6211: a <c>SAFE</c> assembly declares a
    /// mutable static field. Real SQL Server rejects these at
    /// <c>CREATE ASSEMBLY</c> because a writable static is shared process-wide
    /// state; only <c>initonly</c> / <c>literal</c> statics are allowed.
    /// </summary>
    internal static SimulatedSqlException AssemblyMutableStaticField(string verb, string typeName, string permissionSet, string assemblyName, string fieldName) =>
        new($"{verb} ASSEMBLY failed because type '{typeName}' in {permissionSet} assembly '{assemblyName}' has a static field '{fieldName}'. Attributes of static fields in {permissionSet} assemblies must be marked  readonly in Visual C#, ReadOnly in Visual Basic, or initonly in Visual C++ and intermediate language.", 6211, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6503: an assembly the candidate references is
    /// not one the server hosts. Real SQL Server resolves every
    /// <c>AssemblyRef</c> against its own catalog of .NET Framework
    /// assemblies plus the registered user assemblies.
    /// </summary>
    internal static SimulatedSqlException ReferencedAssemblyNotInCatalog(string referenceName) =>
        new($"Assembly '{referenceName}' was not found in the SQL catalog.", 6503, 16, 12);

    /// <summary>
    /// Mimics SQL Server error 6246: <c>CREATE ASSEMBLY</c> named an assembly
    /// that is already registered in the database.
    /// </summary>
    internal static SimulatedSqlException AssemblyAlreadyExists(string assemblyName, string databaseName) =>
        new($"Assembly \"{assemblyName}\" already exists in database \"{databaseName}\".", 6246, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6285: the supplied bytes carry the same module
    /// MVID as an assembly already registered under a different name.
    /// </summary>
    internal static SimulatedSqlException AssemblyDuplicateMvid(string verb, string existingName) =>
        new($"{verb} ASSEMBLY failed because the source assembly is, according to MVID, identical to an assembly that is already registered under the name \"{existingName}\".", 6285, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 10343: <c>clr strict security</c> is on, so a
    /// <c>SAFE</c> / <c>EXTERNAL_ACCESS</c> assembly must be signed or
    /// explicitly trusted. Severity 14, not 16.
    /// </summary>
    internal static SimulatedSqlException AssemblyBlockedByStrictSecurity(string assemblyName) =>
        new($"CREATE or ALTER ASSEMBLY for assembly '{assemblyName}' with the SAFE or EXTERNAL_ACCESS option failed because the 'clr strict security' option of sp_configure is set to 1. Microsoft recommends that you sign the assembly with a certificate or asymmetric key that has a corresponding login with UNSAFE ASSEMBLY permission. Alternatively, you can trust the assembly using sp_add_trusted_assembly.", 10343, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 6263: a CLR routine was invoked while the
    /// <c>clr enabled</c> configuration option is 0. Note this gates
    /// <em>execution</em> only — <c>CREATE ASSEMBLY</c> itself succeeds with
    /// the option off (probe-confirmed).
    /// </summary>
    internal static SimulatedSqlException ClrExecutionDisabled() =>
        new("Execution of user code in the .NET Framework is disabled. Enable \"clr enabled\" configuration option.", 6263, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6590: <c>DROP ASSEMBLY</c> named an assembly
    /// still referenced by a CLR module.
    /// </summary>
    internal static SimulatedSqlException DropAssemblyHasDependent(string assemblyName, string objectName) =>
        new($"DROP ASSEMBLY failed because '{assemblyName}' is referenced by object '{objectName}'.", 6590, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6528: the assembly named by an
    /// <c>EXTERNAL NAME</c> clause is not registered in the current database.
    /// </summary>
    internal static SimulatedSqlException AssemblyNotFoundInDatabase(string assemblyName, string databaseName) =>
        new($"Assembly '{assemblyName}' was not found in the SQL catalog of database '{databaseName}'.", 6528, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6505: the type named by an
    /// <c>EXTERNAL NAME</c> clause does not exist in the assembly. State 2.
    /// </summary>
    internal static SimulatedSqlException ClrTypeNotFound(string typeName, string assemblyName) =>
        new($"Could not find Type '{typeName}' in assembly '{assemblyName}'.", 6505, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 6506: the method named by an
    /// <c>EXTERNAL NAME</c> clause does not exist on the type. Real SQL
    /// Server's text has no terminating period.
    /// </summary>
    internal static SimulatedSqlException ClrMethodNotFound(string methodName, string typeName, string assemblyName) =>
        new($"Could not find method '{methodName}' for type '{typeName}' in assembly '{assemblyName}'", 6506, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6550: the T-SQL parameter list and the CLR
    /// method's parameter list differ in length. State 2.
    /// </summary>
    internal static SimulatedSqlException ClrParameterCountMismatch(string statement) =>
        new($"{statement} failed because parameter counts do not match.", 6550, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 6551: the declared <c>RETURNS</c> type does not
    /// map to the CLR method's return type. State 2.
    /// </summary>
    internal static SimulatedSqlException ClrReturnTypeMismatch(string statement, string routineName) =>
        new($"{statement} for \"{routineName}\" failed because T-SQL and CLR types for return value do not match.", 6551, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 6552: a declared parameter's T-SQL type does
    /// not map to the CLR method's corresponding parameter type. State 3.
    /// </summary>
    internal static SimulatedSqlException ClrParameterTypeMismatch(string statement, string routineName, string parameterName) =>
        new($"{statement} for \"{routineName}\" failed because T-SQL and CLR types for parameter \"{parameterName}\" do not match.", 6552, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 6522: the CLR routine threw. Real SQL Server
    /// appends the exception type, message and stack trace; the simulator
    /// reproduces the type-and-message head, which is the part callers assert
    /// on.
    /// </summary>
    internal static SimulatedSqlException ClrRoutineThrew(string routineName, Exception inner) =>
        new($"A .NET Framework error occurred during execution of user-defined routine or aggregate \"{routineName}\": {Environment.NewLine}{inner.GetType().FullName}: {inner.Message}.", 6522, 16, 1);
}
