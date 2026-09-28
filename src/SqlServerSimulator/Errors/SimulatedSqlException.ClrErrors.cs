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
    internal static SimulatedSqlException ClrMethodNotFound(string methodName, string typeName, string assemblyName, byte state = 1) =>
        new($"Could not find method '{methodName}' for type '{typeName}' in assembly '{assemblyName}'", 6506, 16, state);

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
    /// Mimics SQL Server error 6522: a CLR routine or aggregate threw. The text
    /// carries the exception the way the server's host reports it — the
    /// <c>type: message</c> line, the type again, and the stack's frames in the
    /// user's code and the public <c>Microsoft.SqlServer.Server</c> surface,
    /// each line CRLF-ended (see <see cref="Clr.ClrExceptionReport"/>). The
    /// state is 1 for a procedure and 2 for a function, a table-valued
    /// function's init call and an aggregate — save a function that may read
    /// data or takes a <c>max</c>-typed parameter, whose throw is state 1
    /// (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ClrRoutineThrew(string routineName, string report, byte state) =>
        new($"A .NET Framework error occurred during execution of user-defined routine or aggregate \"{routineName}\": \r\n{report}.", 6522, 16, state);

    /// <summary>
    /// Mimics SQL Server error 6549: a CLR routine threw after its context
    /// connection ended, or left changed, the transaction the caller held on
    /// entry — 6522's report under a different wording, which the server
    /// follows by rolling the transaction back (probed 2026-09-28 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException ClrRoutineThrewEndingTransaction(string routineName, string report) =>
        new($"A .NET Framework error occurred during execution of user defined routine or aggregate '{routineName}': \r\n{report}. User transaction, if any, will be rolled back.", 6549, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 3991: a CLR routine returned after its context
    /// connection ended the transaction the caller held on entry — by a
    /// refused <c>COMMIT</c> or <c>ROLLBACK</c>, or an error that ends a
    /// transaction. The server rolls the transaction back and ends the batch.
    /// </summary>
    internal static SimulatedSqlException ClrContextTransactionEnded(string routineName) =>
        new($"The context transaction which was active before entering user defined routine, trigger or aggregate \"{routineName}\" has been ended inside of it, which is not allowed. Change application logic to enforce strict transaction nesting.", 3991, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 3992: a CLR routine returned with a different
    /// <c>@@TRANCOUNT</c> from the one the caller's transaction had on entry.
    /// The server rolls the transaction back and ends the batch.
    /// </summary>
    internal static SimulatedSqlException ClrTransactionCountChanged(string routineName, int entered, int left) =>
        new($"Transaction count has been changed from {entered} to {left} inside of user defined routine, trigger or aggregate \"{routineName}\". This is not allowed and user transaction will be rolled back. Change application logic to enforce strict transaction nesting.", 3992, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 3994: a CLR routine's context connection ran a
    /// <c>ROLLBACK</c> of a transaction the routine didn't start. The
    /// transaction counts as ended from then on.
    /// </summary>
    internal static SimulatedSqlException ClrRollbackRefused() =>
        new("User defined routine, trigger or aggregate tried to rollback a transaction that is not started in that CLR level. An exception will be thrown to prevent execution of rest of the user defined routine, trigger or aggregate.", 3994, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 3990: a CLR routine's context connection ran a
    /// <c>COMMIT</c> that would end a transaction the routine didn't start.
    /// The transaction counts as ended from then on.
    /// </summary>
    internal static SimulatedSqlException ClrCommitRefused() =>
        new("Transaction is not allowed to commit inside of a user defined routine, trigger or aggregate because the transaction is not started in that CLR level. Change application logic to enforce strict transaction nesting.", 3990, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 589: a SQLCLR function marked
    /// <c>SystemDataAccessKind.Read</c> but not <c>DataAccessKind.Read</c> read
    /// a user object through its context connection (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException RestrictedDataAccess() =>
        new("This statement has attempted to access data whose access is restricted by the assembly.", 589, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 6260: a CLR table-valued function's
    /// <c>FillRow</c> method threw, or produced a value its column cannot hold.
    /// The report has 6522's shape.
    /// </summary>
    internal static SimulatedSqlException ClrFillRowThrew(string report) =>
        new($"An error occurred while getting new row from user defined Table Valued Function : \r\n{report}.", 6260, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6567: <c>CREATE PROCEDURE … EXTERNAL NAME</c>
    /// bound a method whose return type is not one a procedure's status can
    /// come from.
    /// </summary>
    internal static SimulatedSqlException ClrProcedureReturnType() =>
        new("CREATE PROCEDURE failed because a CLR Procedure may only be defined on CLR methods that return either SqlInt32, System.Int32, System.Nullable<System.Int32>, void.", 6567, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 6580: a parameter is <c>OUTPUT</c> on one side
    /// of the binding and passed by value on the other. Real follows it with
    /// Msg 6552 for the same parameter.
    /// </summary>
    internal static SimulatedSqlException ClrOutputDeclarationMismatch(int ordinal) =>
        new($"Declarations do not match for parameter {ordinal}. .NET Framework reference and T-SQL OUTPUT parameter declarations must match.", 6580, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 155 state 37: a CLR procedure's <c>WITH</c>
    /// clause named an option only a T-SQL body takes.
    /// </summary>
    internal static SimulatedSqlException ExternalProcedureOptionRefused(string option) =>
        new($"'{option}' is not a recognized CREATE PROCEDURE option.", 155, 15, 37);

    /// <summary>
    /// Mimics SQL Server error 6530: <c>ALTER</c> would turn a T-SQL module
    /// into a CLR one. The reverse is Msg 2010.
    /// </summary>
    internal static SimulatedSqlException ClrAlterIncompatible(string name) =>
        new($"Cannot perform alter on '{name}' because it is an incompatible object type.", 6530, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 10306: the method a CLR table-valued function
    /// binds carries no <c>SqlFunctionAttribute.FillRowMethodName</c>.
    /// </summary>
    internal static SimulatedSqlException ClrTvfMissingFillRow() =>
        new("The SqlFunctionAttribute of the Init method for a CLR table-valued function must set the FillRowMethodName property.", 10306, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6208: a <c>FillRow</c> method's parameter count
    /// is not one more than the declared column count.
    /// </summary>
    internal static SimulatedSqlException ClrFillRowParameterCount() =>
        new("CREATE FUNCTION failed because the parameter count for the FillRow method should be one more than the SQL declaration for the table valued CLR function.", 6208, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6258: a <c>FillRow</c> out parameter does not
    /// bind to its declared column's type. Real's text has no space before the
    /// function name.
    /// </summary>
    internal static SimulatedSqlException ClrFillRowColumnMismatch(string functionName, int column) =>
        new($"Function signature of \"FillRow\" method (as designated by SqlFunctionAttribute.FillRowMethodName) does not match SQL declaration for table valued CLR function'{functionName}' due to column {column}.", 6258, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6514: a CLR table-valued function's result
    /// table declares a column kind a streaming function cannot return —
    /// the ANSI string types and the legacy large-object types (state 3),
    /// <c>IDENTITY</c> (state 2) and <c>timestamp</c> (state 1, named in
    /// capitals).
    /// </summary>
    internal static SimulatedSqlException ClrTvfColumnKindRefused(string kind, string columnName, byte state) =>
        new($"Cannot use '{kind}' column in the result table of a streaming user-defined function (column '{columnName}').", 6514, 16, state);

    /// <summary>
    /// Mimics SQL Server error 6526: a CLR table-valued function's result
    /// table declares <c>NOT NULL</c> or a <c>DEFAULT</c> on a column.
    /// </summary>
    internal static SimulatedSqlException ClrTvfColumnConstraintRefused(string constraint, string columnName) =>
        new($"Cannot use '{constraint}' constraint in the result table of a streaming user-defined function (column '{columnName}').", 6526, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6525: a CLR table-valued function's result
    /// table declares a table-level constraint.
    /// </summary>
    internal static SimulatedSqlException ClrTvfTableConstraintRefused(string constraint) =>
        new($"Cannot use '{constraint}' constraint in the result table of a streaming user-defined function.", 6525, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6556: <c>CREATE AGGREGATE</c> named a class the
    /// assembly does not contain. Real follows it with Msg 6597.
    /// </summary>
    internal static SimulatedSqlException ClrAggregateTypeNotFound(string typeName, string assemblyName) =>
        new($"CREATE AGGREGATE failed because it could not find type '{typeName}' in assembly '{assemblyName}'.", 6556, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6597, the closing line real sends after a
    /// <c>CREATE AGGREGATE</c> failure it has already described.
    /// </summary>
    internal static SimulatedSqlException ClrAggregateFailed() =>
        new("CREATE AGGREGATE failed.", 6597, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 6255: the aggregate's class does not carry
    /// <c>SqlUserDefinedAggregateAttribute</c>.
    /// </summary>
    internal static SimulatedSqlException ClrAggregateMissingAttribute(string typeName) =>
        new($"CREATE AGGREGATE failed because type \"{typeName}\" does not conform to the UDAGG specification: missing custom attribute \"Microsoft.SqlServer.Server.SqlUserDefinedAggregateAttribute\".", 6255, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6558: the aggregate's class lacks one of the
    /// four contract methods or declares it with the wrong shape — a
    /// <c>Terminate</c> returning another type than the declared one, or an
    /// <c>Accumulate</c> taking another number of arguments. Real follows it
    /// with Msg 6597.
    /// </summary>
    internal static SimulatedSqlException ClrAggregateMethodNonConforming(string typeName, string methodName) =>
        new($"CREATE AGGREGATE failed because type '{typeName}' does not conform to UDAGG specification due to method '{methodName}'.", 6558, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6225: a <c>Format.Native</c> aggregate declares
    /// a field that is not of a blittable value type.
    /// </summary>
    internal static SimulatedSqlException ClrNativeFormatField(string assemblyName, string typeName, string fieldName, string fieldType) =>
        new($"Type \"{assemblyName}.{typeName}\" is marked for native serialization, but field \"{fieldName}\" of type \"{assemblyName}.{typeName}\" is of type \"{fieldType}\" which is a non-value type. Native serialization types can only have fields of blittable types. If you wish to have a field of any other type, consider using different kind of serialization format, such as User Defined Serialization.", 6225, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 10726: a user-defined aggregate's parameter
    /// declares a default.
    /// </summary>
    internal static SimulatedSqlException ClrAggregateDefaultParameter() =>
        new("User defined aggregates do not support default parameters.", 10726, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 2809: <c>EXEC</c> named a CLR aggregate.
    /// </summary>
    internal static SimulatedSqlException ExecOfAggregate(string name) =>
        new($"The request for procedure '{name}' failed because '{name}' is a aggregate function object.", 2809, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11515: <c>sp_describe_first_result_set</c> met
    /// a CLR procedure, whose result sets only its code knows.
    /// </summary>
    internal static SimulatedSqlException DescribeFirstResultSetClrProcedure(string statement) =>
        new($"The metadata could not be determined because statement '{statement}' invokes a CLR procedure.  Consider using the WITH RESULT SETS clause to explicitly describe the result set.", 11515, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6500: the method a CLR trigger's
    /// <c>EXTERNAL NAME</c> binds returns a value.
    /// </summary>
    internal static SimulatedSqlException ClrTriggerReturnType(string methodName, string className, string assemblyName, string returnType) =>
        new($"CREATE TRIGGER failed because method '{methodName}' of class '{className}' in assembly '{assemblyName}' returns {returnType}, but CLR Triggers must return void.", 6500, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 6531: the method a CLR trigger's
    /// <c>EXTERNAL NAME</c> binds declares parameters.
    /// </summary>
    internal static SimulatedSqlException ClrTriggerTakesParameters(string methodName, string className, string assemblyName) =>
        new($"CREATE TRIGGER failed because the function '{methodName}' of class '{className}' of assembly '{assemblyName}' takes one or more parameters but CLR Triggers do not accept parameters.", 6531, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 10324: <c>WITH ENCRYPTION</c> on a CLR trigger.
    /// </summary>
    internal static SimulatedSqlException ClrTriggerEncryption() =>
        new("WITH ENCRYPTION option of CREATE TRIGGER is only applicable to T-SQL triggers and not to CLR triggers.", 10324, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 6267: <c>CREATE TYPE … EXTERNAL NAME</c> names
    /// an assembly the database doesn't hold.
    /// </summary>
    internal static SimulatedSqlException AssemblyNotFoundForType(string assemblyName) =>
        new($"Assembly \"{assemblyName}\" does not exist, or the user does not have permission to reference it.", 6267, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6556: <c>CREATE TYPE … EXTERNAL NAME</c> names
    /// a class the assembly lacks; followed by Msg 6597.
    /// </summary>
    internal static SimulatedSqlException ClrTypeClassNotFound(string className, string assemblyName) =>
        new($"CREATE TYPE failed because it could not find type '{className}' in assembly '{assemblyName}'.", 6556, 16, 1);

    /// <summary>Mimics SQL Server error 6597, closing a failed <c>CREATE TYPE</c>'s binding errors.</summary>
    internal static SimulatedSqlException ClrCreateTypeFailed() =>
        new("CREATE TYPE failed.", 6597, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6255: a CLR type's class carries no
    /// <c>SqlUserDefinedTypeAttribute</c>.
    /// </summary>
    internal static SimulatedSqlException ClrUdtMissingAttribute(string className) =>
        new($"CREATE TYPE failed because type \"{className}\" does not conform to the UDT specification: missing custom attribute \"Microsoft.SqlServer.Server.SqlUserDefinedTypeAttribute\".", 6255, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 6222: a <c>Format.Native</c> type holds a value
    /// type native serialization can't carry.
    /// </summary>
    internal static SimulatedSqlException ClrNativeFieldInvalid(string qualifiedClass, string fieldName) =>
        new($"Type \"{qualifiedClass}\" is marked for native serialization, but field \"{fieldName}\" of type \"{qualifiedClass}\" is not valid for native serialization.", 6222, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6229: a <c>Format.Native</c> class isn't
    /// <c>LayoutKind.Sequential</c>.
    /// </summary>
    internal static SimulatedSqlException ClrNativeNotSequential(string qualifiedClass) =>
        new($"Type \"{qualifiedClass}\" is marked for native serialization. It is not marked with \"LayoutKind.Sequential\". Native serialization requires the type to be marked with \"LayoutKind.Sequential\".", 6229, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6226: a <c>Format.UserDefined</c> type doesn't
    /// implement <c>IBinarySerialize</c>.
    /// </summary>
    internal static SimulatedSqlException ClrUdtNotBinarySerialize(string qualifiedClass) =>
        new($"Type \"{qualifiedClass}\" is marked for user-defined serialization, but does not implement the \"System.Data.Microsoft.SqlServer.Server.IBinarySerialize\" interface.", 6226, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6244: a <c>Format.UserDefined</c> type's
    /// <c>MaxByteSize</c> is outside -1 and 1–8000.
    /// </summary>
    internal static SimulatedSqlException ClrUdtSizeOutOfRange(int size, string qualifiedClass) =>
        new($"The size ({size}) for \"{qualifiedClass}\" is not in the valid range. Size must be -1 or a number between 1 and 8000.", 6244, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6577: a CLR type's class doesn't implement
    /// <c>INullable</c>; followed by Msg 6597.
    /// </summary>
    internal static SimulatedSqlException ClrUdtNotNullable(string className) =>
        new($"CREATE TYPE failed because type '{className}' does not conform to CLR type specification due to interface 'INullable'.", 6577, 16, 1);

    /// <summary>
    /// Mimics SQL Server errors 6557 (a missing static <c>Null</c>, state 7)
    /// and 6558 (a missing static <c>Parse</c>, state 1); followed by Msg 6597.
    /// </summary>
    internal static SimulatedSqlException ClrUdtNonConforming(string className, string memberKind, string memberName, byte state) =>
        new($"CREATE TYPE failed because type '{className}' does not conform to UDT specification due to {memberKind} '{memberName}'.", memberKind == "field" ? 6557 : 6558, 16, state);

    /// <summary>
    /// Mimics SQL Server error 8188: the class is already registered as
    /// another type. It ends the batch (probed 2026-09-28 against SQL Server
    /// 2025).
    /// </summary>
    internal static SimulatedSqlException ClrTypeAlreadyMapped(string className, string assemblyName) =>
        new($"There is already a SQL type for assembly type \"{className}\" on assembly \"{assemblyName}\". Only one SQL type can be mapped to a given assembly type. CREATE TYPE fails.", 8188, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 6598: <c>DROP ASSEMBLY</c> while a CLR type
    /// still maps one of its classes.
    /// </summary>
    internal static SimulatedSqlException DropAssemblyReferencedByType(string assemblyName, string typeName) =>
        new($"DROP ASSEMBLY failed because '{assemblyName}' is referenced by CLR type '{typeName}'.", 6598, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6235: bytes converted to a <c>Format.Native</c>
    /// type aren't the layout's width. It ends the batch (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ClrUdtLengthMismatch(int length, int fixedLength, string typeName) =>
        new($"Data serialization error. Length ({length}) is {(length < fixedLength ? "less" : "greater")} than fixed length ({fixedLength}) for type '{typeName}'.", 6235, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 6584: <c>type::member</c> named an instance
    /// property or field.
    /// </summary>
    internal static SimulatedSqlException ClrPropertyNotStatic(string memberName, string typeName, string assemblyName) =>
        new($"Property or field '{memberName}' for type '{typeName}' in assembly '{assemblyName}' is not static", 6584, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6200: a mutator method called where a value is
    /// only read.
    /// </summary>
    internal static SimulatedSqlException ClrMutatorInReadOnlyContext(string methodName, string typeName, string assemblyName) =>
        new($"Method \"{methodName}\" of type \"{typeName}\" in assembly \"{assemblyName}\" is marked as a mutator. Mutators cannot be used in the read-only portion of the query.", 6200, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6201: a <c>SET</c> calls a method not marked
    /// <c>IsMutator</c>.
    /// </summary>
    internal static SimulatedSqlException ClrNotMutator(string methodName, string typeName, string assemblyName) =>
        new($"Method \"{methodName}\" of type \"{typeName}\" in assembly \"{assemblyName}\" is not marked as a mutator. Only mutators can be used to update the value of a CLR type.", 6201, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5302: a mutator or property assignment on a
    /// NULL CLR type value. It ends the batch (probed 2026-09-28 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException ClrMutatorOnNull(string memberName, string target) =>
        new($"Mutator '{memberName}' on '{target}' cannot be called on a null value.", 5302, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 6207: a CLR type converted to a <c>binary(n)</c>
    /// wider than its value.
    /// </summary>
    internal static SimulatedSqlException ClrUdtToPaddedBinary(string typeName) =>
        new($"Error converting {typeName} to fixed length binary type. The result would be padded and cannot be converted back.", 6207, 16, 1);
}
