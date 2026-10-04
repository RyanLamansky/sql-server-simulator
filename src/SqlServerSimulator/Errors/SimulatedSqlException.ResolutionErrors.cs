using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    internal static SimulatedSqlException IdentifierTooLong(ReadOnlySpan<char> first128)
        => new($"The identifier that starts with '{first128}' is too long. Maximum length is 128.", 103, 15, 4);

    /// <summary>
    /// Mimics SQL Server error 103 for a transaction or savepoint name past 32
    /// characters: state 2 from SQL text, state 30 from a transaction-manager
    /// request (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException TransactionNameTooLong(string name, byte state = 2)
        => new($"The identifier that starts with '{(name.Length > 128 ? name[..128] : name)}' is too long. Maximum length is 32.", 103, 15, state);

    internal static SimulatedSqlException InvalidColumnName(string name) => new($"Invalid column name '{name}'.", 207, 16, 1);

    // Msg 207 renders only the leaf identifier — real SQL Server drops any
    // table / alias qualifier (probe-confirmed: `col.is_replicated` surfaces
    // as "Invalid column name 'is_replicated'.").
    internal static SimulatedSqlException InvalidColumnName(MultiPartName name) => InvalidColumnName(name.Leaf);

    /// <summary>
    /// The refusal a column reference takes where the statement offers no
    /// column scope at all — a <c>VALUES</c> constructor's cells being the
    /// reachable case. Real splits the two shapes: an unqualified name is
    /// Msg 207 on its leaf, a qualified one Msg 4104 on the whole dotted form
    /// (probed 2026-08-05: <c>INSERT INTO t (id, v) VALUES (zz.id, 1)</c> and
    /// <c>… VALUES (t.id, 1)</c> both report 4104, the target's own name
    /// included).
    /// </summary>
    internal static SimulatedSqlException UnboundColumnReference(MultiPartName name) =>
        name.Count > 1 ? MultiPartIdentifierCouldNotBeBound(name.ToString()) : InvalidColumnName(name.Leaf);

    /// <summary>
    /// Mimics SQL Server's Msg 1011: two FROM sources whose aliases (or a CTE's
    /// own name) are the same, naming the later spelling.
    /// </summary>
    internal static SimulatedSqlException CorrelationNameRepeated(string alias) =>
        new($"The correlation name '{alias}' is specified multiple times in a FROM clause.", 1011, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 1012: an alias that is the same as an unaliased
    /// table's name in the same FROM clause, whichever is written first.
    /// </summary>
    internal static SimulatedSqlException CorrelationNameMatchesTable(string alias, string table) =>
        new($"The correlation name '{alias}' has the same exposed name as table '{table}'.", 1012, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 1013: two unaliased objects in one FROM clause
    /// whose names end alike (<c>t</c>, <c>dbo.t</c>, <c>s.t</c>), naming the
    /// later as written and then the earlier.
    /// </summary>
    internal static SimulatedSqlException SameExposedNames(string later, string earlier) =>
        new($"The objects \"{later}\" and \"{earlier}\" in the FROM clause have the same exposed names. Use correlation names to distinguish them.", 1013, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 5333 — a <c>WHEN NOT MATCHED [BY TARGET]</c>
    /// condition naming anything but a source column: state 2 for a qualified
    /// name, 1 for a bare one, a name bound nowhere included (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MergeNotMatchedConditionOutOfScope(MultiPartName name) =>
        new($"The identifier '{name}' cannot be bound. Only source columns and columns in the clause scope are allowed in the 'WHEN NOT MATCHED' clause of a MERGE statement.", 5333, 16, name.Count == 1 ? (byte)1 : (byte)2);

    /// <summary>
    /// Mimics SQL Server's Msg 5334 — a <c>WHEN NOT MATCHED BY SOURCE</c>
    /// condition naming anything but a target column: state 2 for a qualified
    /// name, 1 for a bare one, a name bound nowhere included, while a
    /// target-qualified miss stays Msg 207 (probed 2026-09-28 against SQL Server
    /// 2025).
    /// </summary>
    internal static SimulatedSqlException MergeBySourceConditionOutOfScope(MultiPartName name) =>
        new($"The identifier '{name}' cannot be bound. Only target columns and columns in the clause scope are allowed in the 'WHEN NOT MATCHED BY SOURCE' clause of a MERGE statement.", 5334, 16, name.Count == 1 ? (byte)1 : (byte)2);

    /// <summary>
    /// Mimics SQL Server's Msg 5318: a MERGE whose source exposes the same name
    /// or alias as its target.
    /// </summary>
    internal static SimulatedSqlException MergeSourceAndTargetShareAName() =>
        new("In a MERGE statement, the source and target cannot have the same name or alias. Use different aliases for the source and target to ensure that they have unique names in the MERGE statement.", 5318, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 209 — fired when an unqualified column
    /// reference matches columns in more than one source after a JOIN.
    /// The fix is to add a qualifier (table or alias prefix) disambiguating
    /// which source the reference targets.
    /// </summary>
    internal static SimulatedSqlException AmbiguousColumnName(string name) =>
        new($"Ambiguous column name '{name}'.", 209, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8154 — an UPDATE or DELETE naming its target
    /// by a table its FROM clause reads more than once, each time under
    /// another alias (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException AmbiguousTable(string name) =>
        new($"The table '{name}' is ambiguous.", 8154, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 208, at state 1 — or 0 for a temp table's
    /// name, which real resolves in tempdb (probed 2026-10-01 against SQL
    /// Server 2025) — unless <paramref name="state"/> says otherwise.
    /// </summary>
    internal static SimulatedSqlException InvalidObjectName(MultiPartName name, byte? state = null) =>
        new($"Invalid object name '{name.Written}'.", 208, 16, state ?? (byte)(name.Leaf.StartsWith('#') ? 0 : 1));

    internal static SimulatedSqlException MustDeclareScalarVariable(string name) => new($"Must declare the scalar variable \"@{name}\".", 137, 15, 2);

    /// <summary>
    /// Msg 137 at state 1: the target of a <c>SET</c>, which real checks once
    /// the whole statement has parsed, after its right-hand side's own errors
    /// (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MustDeclareSetTarget(string name) => new($"Must declare the scalar variable \"@{name}\".", 137, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 137 for a table variable read as a scalar —
    /// <c>SELECT @t</c>, <c>SET @t = 1</c>, <c>@t.a</c> — which real reports
    /// at class 16 state 1 where an undeclared name is class 15 state 2
    /// (probed 2026-09-24 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException TableVariableUsedAsScalar(string name) => new($"Must declare the scalar variable \"@{name}\".", 137, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 1087 — fired when a DML target or FROM source
    /// references a table-variable name (<c>@t</c>) that hasn't been
    /// <c>DECLARE</c>d in the current batch. Note the <c>@</c> prefix is
    /// included in the wording (probe-confirmed: <c>"Must declare the table
    /// variable \"@t\"."</c>). Class 15 State 2 — mirrors the
    /// <see cref="MustDeclareScalarVariable"/> shape since both are
    /// missing-variable errors at parse / bind time.
    /// </summary>
    internal static SimulatedSqlException MustDeclareTableVariable(string name) =>
        new($"Must declare the table variable \"{name}\".", 1087, 15, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 213 — a positional value list measured against
    /// the table definition rather than against a column list the statement
    /// wrote itself. Raised by an <c>INSERT</c> with no column list whose
    /// <c>VALUES</c> tuple or source <c>SELECT</c> is the wrong width, and by
    /// an <c>OUTPUT … INTO</c> whose projection doesn't match its target.
    /// The column-list forms report Msg 109 / 110 (VALUES) or Msg 120 / 121
    /// (SELECT) instead.
    /// </summary>
    internal static SimulatedSqlException ColumnCountDoesNotMatchTableDefinition() =>
        new("Column name or number of supplied values does not match table definition.", 213, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 134 — fired when a <c>DECLARE</c> names a
    /// variable that already exists in the batch (either a previous
    /// <c>DECLARE</c> or a SqlClient parameter of the same name —
    /// probe-confirmed parameters and declared variables share a
    /// namespace).
    /// </summary>
    internal static SimulatedSqlException VariableAlreadyDeclared(string name) =>
        new($"The variable name '@{name}' has already been declared. Variable names must be unique within a query batch or stored procedure.", 134, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 141 — fired when a <c>SELECT</c> mixes
    /// variable assignment (<c>@v = expr</c>) with non-assignment
    /// projection elements in the same projection list.
    /// </summary>
    internal static SimulatedSqlException SelectAssignmentMixedWithRetrieval() =>
        new("A SELECT statement that assigns a value to a variable must not be combined with data-retrieval operations.", 141, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 326 — a dotted name that reads both ways at
    /// once: a spatial column's property (<c>Location.Lat</c>) and a column of
    /// a source aliased with the same word. Probe-confirmed wording, which
    /// names the whole dotted identifier and then both readings.
    /// </summary>
    internal static SimulatedSqlException AmbiguousSpatialPropertyOrColumn(string qualifier, string leaf) =>
        new($"Multi-part identifier '{qualifier}.{leaf}' is ambiguous. Both columns '{qualifier}' and '{qualifier}.{leaf}' exist.", 326, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4104: a multi-part identifier that binds
    /// nowhere in scope — an OUTPUT clause naming neither the
    /// INSERTED/DELETED virtual tables nor the MERGE source alias, or a
    /// non-<c>APPLY</c> FROM source's argument naming a sibling source
    /// (see <c>Selection.RejectSiblingReferences</c>).
    /// </summary>
    internal static SimulatedSqlException MultiPartIdentifierCouldNotBeBound(string name) =>
        new($"The multi-part identifier \"{name}\" could not be bound.", 4104, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4121 — fired when a schema-qualified function
    /// call (<c>SELECT schema.fn(x)</c>) names something that isn't a known
    /// UDF / aggregate / column. Verbatim wording probe-confirmed against
    /// SQL Server 2025: the message references the dotted name in the
    /// "or the user-defined function or aggregate" slot and shows the schema
    /// qualifier in the "column" slot. Distinct from Msg 195 ("not a
    /// recognized built-in function name") which fires for bare 1-part
    /// calls.
    /// </summary>
    internal static SimulatedSqlException CannotFindUserDefinedFunction(MultiPartName name) =>
        new($"Cannot find either column \"{name.ImmediateQualifier}\" or the user-defined function or aggregate \"{name}\", or the name is ambiguous.", 4121, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 313 — fired by a function/procedure call that
    /// supplies fewer arguments than the parameter list. Probe-confirmed that
    /// this fires even when omitted parameters have declared defaults — the
    /// <c>DEFAULT</c> keyword is the only legal omission path.
    /// </summary>
    internal static SimulatedSqlException InsufficientArgumentsToFunction(string name, byte state = 2) =>
        new($"An insufficient number of arguments were supplied for the procedure or function {name}.", 313, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 8144 — fired by a function/procedure call that
    /// supplies more arguments than the parameter list. Wording probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException TooManyArgumentsToFunction(string name, byte state = 2) =>
        new($"Procedure or function {name} has too many arguments specified.", 8144, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 8146 — arguments passed to a routine that
    /// declares no parameters; <c>sp_executesql</c> with an empty declaration
    /// string leaves the name empty, hence the double space, and reports state
    /// 1 where an <c>EXEC</c> of a procedure reports state 2. Probe-confirmed
    /// against SQL Server 2025 (2026-09-24, 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException ArgumentsSuppliedToParameterlessRoutine(string name, byte state = 1) =>
        new($"Procedure {name} has no parameters and arguments were supplied.", 8146, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 8178 — an <c>sp_executesql</c> parameter that
    /// the declaration string declares but no argument supplies. An explicit
    /// <c>NULL</c> counts as supplied; OUTPUT parameters need supplying like
    /// any other; and where several are missing, the <em>first declared</em>
    /// one is named. The quoted query is the two argument strings verbatim —
    /// the declarations parenthesized, then the statement, with whatever
    /// spacing they were written with. Wording, severity and state all
    /// probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ParameterizedQueryExpectsParameter(
        string parameterDefinitions, string statement, string parameterName) =>
        new($"The parameterized query '({parameterDefinitions}){statement}' expects the parameter "
            + $"'{parameterName}', which was not supplied.", 8178, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4124 — an <c>sp_executesql</c> declaration
    /// string with text after the parenthesized list it is parsed as, such as
    /// <c>N'@p int) select (1'</c>. Probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException BatchParametersNotValid() =>
        new("The parameters supplied for the batch are not valid.", 4124, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 217 — fired when scalar UDF / proc / trigger /
    /// view recursion exceeds the 32-level cap (probe-confirmed verbatim).
    /// Backed by <see cref="SimulatedDbConnection.NestingLevel"/>; the call
    /// site checks the depth before incrementing. It acts as under
    /// <c>XACT_ABORT</c> whatever the option says: uncaught it ends the batch
    /// and rolls the transaction back, caught it dooms it (probed 2026-10-02
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MaximumNestingLevelExceeded() =>
        new("Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).", 217, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Mimics SQL Server's Msg 1005 — a numbered procedure's <c>;N</c> outside
    /// 1 to 32767, the number as written and its line in the text.
    /// </summary>
    internal static SimulatedSqlException InvalidProcedureNumber(int line, string written) =>
        new($"Line {line}: Invalid procedure number ({written}). Must be between 1 and 32767.", 1005, 15, 1);

    /// <summary>Mimics SQL Server's Msg 2730 — <c>CREATE PROCEDURE p;N</c> before <c>p</c> (its number 1) exists.</summary>
    internal static SimulatedSqlException NumberedProcedureWithoutGroupOne(string name, short groupNumber) =>
        new($"Cannot create procedure '{name}' with a group number of {groupNumber} because a procedure with the same name and a group number of 1 does not currently exist in the database. Must execute CREATE PROCEDURE '{name}';1 first.", 2730, 11, 1);

    /// <summary>Mimics SQL Server's Msg 2004 — <c>CREATE PROCEDURE p;N</c> over a number the group already holds.</summary>
    internal static SimulatedSqlException ProcedureGroupNumberTaken(string name, short groupNumber) =>
        new($"Procedure '{name}' has already been created with group number {groupNumber}. Create procedure with an unused group number.", 2004, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 2812 — EXEC named a stored procedure that
    /// doesn't exist. Distinct error number from Msg 208 / 3701; the State
    /// (62) is probe-confirmed. Wording mirrors real SQL Server verbatim.
    /// </summary>
    internal static SimulatedSqlException CouldNotFindStoredProcedure(string name) =>
        new($"Could not find stored procedure '{name}'.", 2812, 16, 62);

    /// <summary>
    /// Mimics SQL Server error 8199: <c>EXEC @v</c> names its procedure through
    /// a variable that isn't a character string. Probe-confirmed against SQL
    /// Server 2025 (2026-09-23), Class 16, State 1.
    /// </summary>
    internal static SimulatedSqlException ExecuteProcedureNameNotString() =>
        new("In EXECUTE <procname>, procname can only be a literal or variable of type char, varchar, nchar, or nvarchar.", 8199, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 201 — EXEC failed to supply a required
    /// parameter (either a named argument referenced an unknown parameter,
    /// leaving a required one un-supplied, or the call simply omitted a
    /// parameter that has no default). The State (4) and verbatim wording
    /// were probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ProcedureExpectsParameter(string procedureName, string parameterName, byte state = 4) =>
        new($"Procedure or function '{procedureName}' expects parameter '@{parameterName}', which was not supplied.", 201, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 8145 — an EXEC named an argument the procedure
    /// has no parameter for, every required parameter being supplied (a
    /// missing one is Msg 201 instead). Probed 2026-09-23 against SQL Server
    /// 2025.
    /// </summary>
    internal static SimulatedSqlException NotAParameterForProcedure(string parameterName, string procedureName) =>
        new($"@{parameterName} is not a parameter for procedure {procedureName}.", 8145, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8143 — a single EXEC supplied the same named
    /// argument twice. State 1, exact wording probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException ParameterSuppliedMultipleTimes(string parameterName) =>
        new($"Parameter '@{parameterName}' was supplied multiple times.", 8143, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8162 — a variable passed <c>OUTPUT</c> to a
    /// procedure or <c>sp_executesql</c> parameter not declared
    /// <c>OUTPUT</c>. The call doesn't run (probed 2026-10-02 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException ParameterNotDeclaredOutput(string parameterName) =>
        new($"The formal parameter \"@{parameterName}\" was not declared as an OUTPUT parameter, but the actual parameter passed in requested output.", 8162, 16, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 179 — an <c>EXEC</c>, <c>EXEC … AT</c> or
    /// <c>sp_executesql</c> argument that is a constant marked <c>OUTPUT</c>
    /// (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ConstantPassedAsOutput() =>
        new("Cannot use the OUTPUT option when passing a constant to a stored procedure.", 179, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 119 — an EXEC mixed positional and named
    /// arguments incorrectly: once a <c>@name = value</c> appeared, every
    /// following argument must also be in that form. State 1 / class 15;
    /// verbatim wording probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException MustPassParameterAsNamed() =>
        new("Must pass parameter number 2 and subsequent parameters as '@name = value'. After the form '@name = value' has been used, all subsequent parameters must be passed in the form '@name = value'.", 119, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 487 — a module's <c>WITH</c> clause named an
    /// option its grammar parses but the module kind doesn't take (e.g.
    /// <c>WITH RETURNS NULL ON NULL INPUT</c> on an inline TVF, <c>WITH
    /// RECOMPILE</c> on a view). <paramref name="statement"/> is
    /// <c>FUNCTION</c> / <c>PROCEDURE</c> / <c>VIEW</c> / <c>TRIGGER</c>.
    /// Verbatim wording probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException InvalidOptionForCreateStatement(string statement, byte state) =>
        new($"An invalid option was specified for the statement \"CREATE/ALTER {statement}\".", 487, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 195 for a module's <c>WITH</c> clause naming an
    /// option its grammar doesn't recognize, echoed as written (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException OptionNotRecognized(string option) =>
        new($"'{option}' is not a recognized option.", 195, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 10796: <c>SCHEMABINDING</c> on a procedure or
    /// trigger that isn't natively compiled, or <c>NATIVE_COMPILATION</c>
    /// without it — state 1 for those, 2 for a function (probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException SchemaBindingRequiresNativeCompilation(byte state) =>
        new("The SCHEMABINDING option is supported only for natively compiled modules, and is required for those modules.", 10796, 15, state);

    /// <summary>
    /// Mimics SQL Server's Msg 4514 — fired at <c>CREATE FUNCTION</c> when an
    /// inline TVF's body projects an unnamed column. Distinct from
    /// <c>SELECT INTO</c>'s Msg 1038 (different wording, different error
    /// number). The 1-based column position is embedded in the message.
    /// </summary>
    internal static SimulatedSqlException InlineTvfMissingColumnName(int columnPosition) =>
        new($"CREATE FUNCTION failed because a column name is not specified for column {columnPosition}.", 4514, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4506 — fired at <c>CREATE VIEW</c> /
    /// <c>CREATE FUNCTION</c> when the body projects two columns with the
    /// same name. Probe-confirmed wording embeds both the column name and
    /// the object name (the literal "view or function" text comes verbatim
    /// from SQL Server since views and inline TVFs share the projection-
    /// uniqueness rule).
    /// </summary>
    internal static SimulatedSqlException DuplicateColumnInViewOrFunction(string columnName, string objectName) =>
        new($"Column names in each view or function must be unique. Column name '{columnName}' in view or function '{objectName}' is specified more than once.", 4506, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4511 — fired at <c>CREATE VIEW</c> when the
    /// body projects an unnamed column (an expression without an <c>AS</c>
    /// alias). Distinct from inline TVF's Msg 4514 ("CREATE FUNCTION
    /// failed...") and SELECT INTO's Msg 1038. The 1-based column position
    /// is embedded in the message.
    /// </summary>
    internal static SimulatedSqlException CreateViewMissingColumnName(int columnPosition) =>
        new($"Create View or Function failed because no column name was specified for column {columnPosition}.", 4511, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4403 — DML through a view whose body has an
    /// aggregate / DISTINCT / GROUP BY / PIVOT / UNPIVOT, none of which
    /// preserve a 1:1 row correspondence with the underlying base. The same
    /// wording fires for INSERT, UPDATE, and DELETE through such a view —
    /// the simulator collapses these into one factory matching SQL Server's
    /// uniform message. Probe-confirmed verbatim against SQL Server 2025
    /// (2026-05-12). Through a derived table named as a joined write's target
    /// (<paramref name="derivedTable"/>) it is Msg 4418 (probed 2026-10-01).
    /// </summary>
    internal static SimulatedSqlException CannotUpdateNonUpdatableView(string viewName, bool derivedTable = false) =>
        derivedTable
            ? new($"Derived table '{viewName}' is not updatable because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.", 4418, 16, 1)
            : new($"Cannot update the view or function '{viewName}' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.", 4403, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4405 — DML through a view whose body has
    /// multiple base tables (JOIN form) and the modification affects more
    /// than one of them: a DELETE always, an UPDATE or INSERT whose columns
    /// land in two. Probe-confirmed verbatim wording against SQL Server 2025.
    /// An UPDATE through a derived table named as a joined write's target
    /// (<paramref name="derivedTable"/>) is Msg 4420, where a DELETE through
    /// one stays Msg 4405 (probed 2026-10-01).
    /// </summary>
    internal static SimulatedSqlException ViewUpdateAffectsMultipleTables(string viewName, bool derivedTable = false) =>
        derivedTable
            ? new($"Derived table '{viewName}' is not updatable because the modification affects multiple base tables.", 4420, 16, 1)
            : new($"View or function '{viewName}' is not updatable because the modification affects multiple base tables.", 4405, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4426 — a DELETE through a view or CTE whose body
    /// a <c>UNION</c> tops, named as the statement wrote it (probed 2026-10-01
    /// against SQL Server 2025); through a derived table named as a joined
    /// write's target (<paramref name="derivedTable"/>) it is Msg 4417.
    /// </summary>
    internal static SimulatedSqlException ViewWithUnionNotUpdatable(string viewName, bool derivedTable = false) =>
        derivedTable
            ? new($"Derived table '{viewName}' is not updatable because the definition contains a UNION operator.", 4417, 16, 1)
            : new($"View '{viewName}' is not updatable because the definition contains a UNION operator.", 4426, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 414 (<paramref name="isDelete"/> false) and Msg
    /// 415 — a joined UPDATE or DELETE whose target is a view carrying an
    /// <c>INSTEAD OF</c> trigger for the action, the view named bare (probed
    /// 2026-10-01 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException InsteadOfViewInJoin(string viewName, bool isDelete) =>
        isDelete
            ? new($"DELETE is not allowed because the statement updates view \"{viewName}\" which participates in a join and has an INSTEAD OF DELETE trigger.", 415, 16, 1)
            : new($"UPDATE is not allowed because the statement updates view \"{viewName}\" which participates in a join and has an INSTEAD OF UPDATE trigger.", 414, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4406 — INSERT or UPDATE through a view
    /// touched a projection column that's not a direct column reference
    /// (an arithmetic expression, function call, CAST, etc.). The view as
    /// a whole may still be updatable (other projections can be direct
    /// refs), and DELETE through such a view works fine — the rejection
    /// is per-touched-column. Probe-confirmed verbatim against SQL Server
    /// 2025. Through a derived table named as a joined write's target
    /// (<paramref name="derivedTable"/>) it is Msg 4421 (probed 2026-10-01).
    /// </summary>
    internal static SimulatedSqlException ViewDmlTouchesDerivedField(string viewName, bool derivedTable = false) =>
        derivedTable
            ? new($"Derived table '{viewName}' is not updatable because a column of the derived table is derived or constant.", 4421, 16, 1)
            : new($"Update or insert of view or function '{viewName}' failed because it contains a derived or constant field.", 4406, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 550 — INSERT or UPDATE through a view with
    /// (or spanning) <c>WITH CHECK OPTION</c> would leave a row not visible
    /// in the view. Triggers a per-row check after the row is constructed
    /// (INSERT) or computed (UPDATE) against every CHECK OPTION-bearing
    /// level in the view chain; any miss raises this. Probe-confirmed
    /// verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ViewCheckOptionViolation() =>
        new("The attempted insert or update failed because the target view either specifies WITH CHECK OPTION or spans a view that specifies WITH CHECK OPTION and one or more rows resulting from the operation did not qualify under the CHECK OPTION constraint.", 550, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 2020 — <c>sys.dm_sql_referenced_entities</c>
    /// reached a reference it couldn't resolve, so the column detail it reported
    /// may be short. Probe-confirmed verbatim (including the double space before
    /// "Before rerunning"), and probe-confirmed to follow the rows rather than
    /// replace them: the DMV hands back what it found and then raises.
    /// </summary>
    internal static SimulatedSqlException DependencyReportMayBeIncomplete(string entityName) =>
        new($"The dependencies reported for entity \"{entityName}\" might not include references to all columns. This is either because the entity references an object that does not exist or because of an error in one or more statements in the entity.  Before rerunning the query, ensure that there are no errors in the entity and that all objects referenced by the entity exist.", 2020, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 128 — a name stood where the context offers no
    /// column scope at all, so the answer is neither Msg 207 nor Msg 4104 but
    /// its own refusal. <paramref name="name"/> is the identifier as written
    /// (dotted qualifiers included, brackets stripped) and real double-quotes
    /// it. Probe-confirmed as <c>PRINT</c>'s answer to a bare or qualified
    /// name, whatever the batch's other scopes hold.
    /// </summary>
    internal static SimulatedSqlException NameNotPermittedInThisContext(string name) =>
        new($"The name \"{name}\" is not permitted in this context. Valid expressions are constants, constant expressions, and (in some contexts) variables. Column names are not permitted.", 128, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 1046 — a subquery stood where only a scalar
    /// expression may. The full-text predicate raises it because real classifies
    /// that predicate as a rowset construct; <c>PRINT</c>'s operand raises it
    /// for a genuine subquery at any depth, a wrapping function call included.
    /// </summary>
    internal static SimulatedSqlException SubqueriesNotAllowedInThisContext() =>
        new("Subqueries are not allowed in this context. Only scalar expressions are allowed.", 1046, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 215 — a parenthesized list followed a FROM
    /// source that isn't a function, and no alias stood between them for real to
    /// read the parens as the legacy table-hint form. The whole list is bound as
    /// an argument list first, so each name inside it reports its own Msg 207
    /// ahead of this one. Probe-confirmed verbatim, the object named as the FROM
    /// clause wrote it.
    /// </summary>
    internal static SimulatedSqlException ParametersSuppliedForNonFunction(string objectName) =>
        new($"Parameters supplied for object '{objectName}' which is not a function. If the parameters are intended as a table hint, a WITH keyword is required.", 215, 16, 1);
}
