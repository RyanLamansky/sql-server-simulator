namespace SqlServerSimulator;

// The legacy CREATE DEFAULT / CREATE RULE surface: the two statements, their
// DROP forms, the four binding procedures and a rule's enforcement. Every
// number, class and state below was observed raised by SQL Server 2025
// (probed 2026-09-26); the lines a binding procedure reports are in
// Simulation.SystemProcedureErrorSite.
partial class SimulatedSqlException
{
    /// <summary>Mimics SQL Server's Msg 160 — a <c>CREATE RULE</c> predicate naming no variable.</summary>
    internal static SimulatedSqlException RuleHasNoVariable() =>
        new("Rule does not contain a variable.", 160, 15, 1);

    /// <summary>Mimics SQL Server's Msg 161 — a <c>CREATE RULE</c> predicate naming two variables or more.</summary>
    internal static SimulatedSqlException RuleHasSeveralVariables() =>
        new("Rule contains more than one variable.", 161, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4105 — a schema-qualified function call in a
    /// <c>CREATE RULE</c> predicate, refused by its shape whether or not the
    /// function exists.
    /// </summary>
    internal static SimulatedSqlException UserFunctionNotAllowedInThisContext() =>
        new("User-defined functions, partition functions, and column references are not allowed in expressions in this context.", 4105, 16, 3);

    /// <summary>
    /// Mimics SQL Server's Msg 513 — a value written to a rule-bound column
    /// that the rule answers false for. The table is named schema-qualified.
    /// </summary>
    internal static SimulatedSqlException RuleViolation(string databaseName, string tableName, string columnName) =>
        new($"A column insert or update conflicts with a rule imposed by a previous CREATE RULE statement. The statement was terminated. The conflict occurred in database '{databaseName}', table '{tableName}', column '{columnName}'.", 513, 16, 0);

    /// <summary>
    /// Mimics SQL Server's Msg 1710 — a table variable's (or a multi-statement
    /// function's return table's) column declared with an alias type that has
    /// a rule or default bound.
    /// </summary>
    internal static SimulatedSqlException BoundAliasTypeInTableVariable(string typeName, bool hasRule) =>
        new($"Cannot use alias type with rule or default bound to it as a column type in table variable or return table definition in table valued function. Type '{typeName}' has a {(hasRule ? "rule" : "default")} bound to it.", 1710, 16, 1);

    /// <summary>Mimics SQL Server's Msg 3716 — <c>DROP DEFAULT</c> (state 3) or <c>DROP RULE</c> (state 1) of an object still bound.</summary>
    internal static SimulatedSqlException BoundObjectCannotBeDropped(string noun, string name) =>
        new($"The {noun} '{name}' cannot be dropped because it is bound to one or more column.", 3716, 16, noun == "default" ? (byte)3 : (byte)1);

    /// <summary>Mimics SQL Server's Msg 3717 — <c>DROP DEFAULT</c> naming a DEFAULT constraint.</summary>
    internal static SimulatedSqlException CannotDropDefaultConstraintWithDropDefault() =>
        new("Cannot drop a default constraint by DROP DEFAULT statement. Use ALTER TABLE to drop a constraint default.", 3717, 16, 1);

    /// <summary>Mimics SQL Server's Msg 3701 for <c>DROP DEFAULT</c> / <c>DROP RULE</c> of a name nothing holds.</summary>
    internal static SimulatedSqlException CannotDropBindableDoesNotExist(string noun, string name) =>
        new($"Cannot drop the {noun} '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>Mimics SQL Server's Msg 15016 — <c>sp_bindefault</c> naming no default.</summary>
    internal static SimulatedSqlException DefaultDoesNotExist(string name) =>
        new($"The default '{name}' does not exist.", 15016, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15017 — <c>sp_bindrule</c> naming no rule.</summary>
    internal static SimulatedSqlException RuleDoesNotExist(string name) =>
        new($"The rule '{name}' does not exist.", 15017, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15148 — a binding procedure's <c>@objname</c> naming neither a column nor an alias type.</summary>
    internal static SimulatedSqlException BindTargetDoesNotExist(string name) =>
        new($"The data type or table column '{name}' does not exist or you do not have permission.", 15148, 16, 1);

    /// <summary>Mimics SQL Server's Msg 4185 — a binding procedure's <c>@objname</c> naming a system type.</summary>
    internal static SimulatedSqlException ActionNotAllowedOnSystemType() =>
        new("This action cannot be performed on a system type.", 4185, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15100 / 15106 — a <c>@futureonly</c> other than <c>'futureonly'</c>.</summary>
    internal static SimulatedSqlException BindUsage(bool isRule) =>
        isRule
            ? new("Usage: sp_bindrule rulename, objectname [, 'futureonly']", 15106, 16, 1)
            : new("Usage: sp_bindefault defaultname, objectname [, 'futureonly']", 15100, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15101 — a default bound to a computed, sparse, rowversion, MAX, xml or CLR column.</summary>
    internal static SimulatedSqlException CannotBindDefaultToColumnKind() =>
        new("Cannot bind a default to a computed column, a sparse column, or to a column of the following data types: timestamp, varchar(max), nvarchar(max), varbinary(max), xml, or CLR type.", 15101, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15102 — a default bound to an identity column.</summary>
    internal static SimulatedSqlException CannotBindDefaultToIdentity() =>
        new("Cannot bind a default to an identity column.", 15102, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15103 — a default bound to a column holding a DEFAULT constraint.</summary>
    internal static SimulatedSqlException CannotBindDefaultOverConstraint() =>
        new("Cannot bind a default to a column created with or altered to have a default value.", 15103, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15107 — a rule bound to a computed, sparse, LOB, rowversion, xml or CLR column.</summary>
    internal static SimulatedSqlException CannotBindRuleToColumnKind() =>
        new("Cannot bind a rule to a computed column, a sparse column, or to a column of the following data types: text, ntext, image, timestamp, varchar(max), nvarchar(max), varbinary(max), xml, or user-defined data type.", 15107, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15236 / 15238 — unbinding a column with nothing of that kind bound.</summary>
    internal static SimulatedSqlException ColumnHasNothingBound(string name, bool isRule) =>
        isRule
            ? new($"Column '{name}' has no rule.", 15238, 16, 1)
            : new($"Column '{name}' has no default.", 15236, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15237 / 15239 — unbinding an alias type with nothing of that kind bound.</summary>
    internal static SimulatedSqlException TypeHasNothingBound(string name, bool isRule) =>
        isRule
            ? new($"User data type '{name}' has no rule.", 15239, 16, 1)
            : new($"User data type '{name}' has no default.", 15237, 16, 1);

}
