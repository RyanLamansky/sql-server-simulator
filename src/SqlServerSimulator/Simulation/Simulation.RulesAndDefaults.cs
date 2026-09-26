using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The legacy CREATE DEFAULT / CREATE RULE objects: the two statements, their
// DROP forms, the four procedures that bind them to columns and alias types,
// and a bound rule's enforcement. Probed 2026-09-26 against SQL Server 2025.
partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE DEFAULT name AS expr</c> / <c>CREATE RULE name AS
    /// predicate</c>. Cursor on entry: the <c>DEFAULT</c> / <c>RULE</c> keyword.
    /// Either must be its batch's only statement (Msg 111), and every error
    /// from the name on names the object as its procedure, as a module's does.
    /// </summary>
    /// <remarks>
    /// Both expressions have no column scope — a name is Msg 128, a subquery
    /// Msg 1046 — and a rule's predicate reads exactly one variable (Msg 160
    /// for none, 161 for more) and calls no schema-qualified function
    /// (Msg 4105, whether or not the function exists). A default may not name
    /// a variable at all: it is undeclared, Msg 137.
    /// </remarks>
    private static bool TryParseCreateDefaultOrRule(ParserContext context, bool isRule)
    {
        if (context.Batch.BlockDepth > 0 || context.Batch.HasDispatchedStatement)
            throw SimulatedSqlException.MustBeFirstStatementInBatch(isRule ? "CREATE RULE" : "CREATE DEFAULT");

        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = BatchContext.ParseObjectName(context);
        context.Batch.ErrorProcedureName = name.Leaf;
        var schema = ResolveModuleSchema(context, name, isAlter: false);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        Expression? defaultExpression = null;
        BooleanExpression? rulePredicate = null;
        if (isRule)
        {
            var savedScalarOnly = context.ScalarOnlyOperand;
            var savedScalarOnlyReference = context.ScalarOnlyColumnReference;
            context.ScalarOnlyOperand = true;
            context.ScalarOnlyColumnReference = null;
            context.RuleVariables = [];
            try
            {
                rulePredicate = BooleanExpression.Parse(context);
                if (context.ScalarOnlyColumnReference is not null)
                    throw Expression.ScalarOnlyOperandError(context);
                if (context.RuleVariables.Count != 1)
                    throw context.RuleVariables.Count == 0 ? SimulatedSqlException.RuleHasNoVariable() : SimulatedSqlException.RuleHasSeveralVariables();
            }
            finally
            {
                context.ScalarOnlyOperand = savedScalarOnly;
                context.ScalarOnlyColumnReference = savedScalarOnlyReference;
                context.RuleVariables = null;
            }
        }
        else
        {
            defaultExpression = ParseDefaultClauseExpression(context);
        }

        RejectStatementAfterModuleBody(context, name.Leaf);
        if (context.Batch.IsSkipping)
            return true;

        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
        if (schema.HasNameInSharedNamespace(name.Leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(name.Leaf, state: 3);

        var objectId = context.CurrentDatabase.AllocateObjectId();
        var createDate = context.Batch.CurrentStatement.UtcNow;
        var definition = context.Command.CommandText;
        if (rulePredicate is not null)
        {
            _ = schema.Rules.TryAdd(name.Leaf, new RuleObject(schema, name.Leaf, objectId, createDate, definition, rulePredicate));
            RecordSlotUndo<RuleObject>(context, schema.Rules, name.Leaf, null);
            RecordDdlEvent(context, "CREATE_RULE", schema.Name, name.Leaf, "RULE");
        }
        else
        {
            _ = schema.Defaults.TryAdd(name.Leaf, new DefaultObject(schema, name.Leaf, objectId, createDate, definition, defaultExpression!));
            RecordSlotUndo<DefaultObject>(context, schema.Defaults, name.Leaf, null);
            RecordDdlEvent(context, "CREATE_DEFAULT", schema.Name, name.Leaf, "DEFAULT");
        }
        return true;
    }

    /// <summary>
    /// Drops one <c>DROP DEFAULT</c> / <c>DROP RULE</c> list entry. A name held
    /// by another kind is Msg 3705, a DEFAULT constraint's name under
    /// <c>DROP DEFAULT</c> Msg 3717, a missing one Msg 3701 unless
    /// <c>IF EXISTS</c>, and an object still bound to a column or alias type
    /// Msg 3716.
    /// </summary>
    private static void DropOneBindable(ParserContext context, MultiPartName name, bool ifExists, bool isRule)
    {
        if (context.Batch.IsSkipping)
            return;
        var noun = isRule ? "rule" : "default";
        var leaf = name.Leaf;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is not null)
        {
            RejectDropOfOtherKind(schema, name, isRule ? "RULE" : "DEFAULT");
            if (!isRule && !schema.Defaults.ContainsKey(leaf) && HasDefaultConstraintNamed(schema, leaf))
                throw SimulatedSqlException.CannotDropDefaultConstraintWithDropDefault();
        }

        BindableObject? existing = schema is null ? null
            : isRule ? schema.Rules.GetValueOrDefault(leaf)
            : schema.Defaults.GetValueOrDefault(leaf);
        if (existing is null)
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropBindableDoesNotExist(noun, leaf);
        }

        schema!.Database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasDropAuthority(context.Batch, schema, existing.ObjectId))
            throw SimulatedSqlException.DropObjectPermissionDenied(noun, leaf);
        if (existing.IsBound())
            throw SimulatedSqlException.BoundObjectCannotBeDropped(noun, leaf);

        if (existing is RuleObject rule)
        {
            _ = schema.Rules.TryRemove(leaf, out _);
            RecordSlotUndo(context, schema.Rules, leaf, rule);
            RecordDdlEvent(context, "DROP_RULE", schema.Name, leaf, "RULE");
        }
        else
        {
            _ = schema.Defaults.TryRemove(leaf, out _);
            RecordSlotUndo(context, schema.Defaults, leaf, (DefaultObject)existing);
            RecordDdlEvent(context, "DROP_DEFAULT", schema.Name, leaf, "DEFAULT");
        }
    }

    private static bool HasDefaultConstraintNamed(Schema schema, string leaf)
    {
        foreach (var table in schema.HeapTables.Values)
        {
            foreach (var column in table.Columns)
            {
                if (column.DefaultConstraint is { } constraint && schema.Database.Collation.Equals(constraint.Name, leaf))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// <c>sp_bindefault @defname, @objname [, @futureonly]</c> and
    /// <c>sp_bindrule @rulename, @objname [, @futureonly]</c>. <c>@objname</c>
    /// with a dot names a column (<c>table.column</c>, schema optional), else an
    /// alias type. Binding to a type also rebinds the columns declared with it
    /// that still carry the type's previous binding, unless
    /// <c>@futureonly</c> is <c>'futureonly'</c>; a column bound later takes
    /// the type's. The binding is transactional, and each outcome prints real's
    /// confirmation from the line of its source that prints it.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpBind(BatchContext batch, string procedureName, bool isRule)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var objectParameter = isRule ? "rulename" : "defname";
        var (objectName, targetName, futureOnlyText) = ParseBindingArgs(arguments, procedureName, objectParameter, "objname", "futureonly");
        if (objectName is null)
            throw SimulatedSqlException.ProcedureExpectsParameter(procedureName, objectParameter);
        if (targetName is null)
            throw SimulatedSqlException.ProcedureExpectsParameter(procedureName, "objname");
        var futureOnly = futureOnlyText is not null;
        if (futureOnly && !BuiltInToken.Equals(futureOnlyText, "futureonly"))
            throw SimulatedSqlException.BindUsage(isRule);

        var database = batch.CurrentDatabase;
        BindableObject? bound = null;
        if (ObjectId.TryParseObjectName(objectName, out var boundName) && batch.TryResolveSchema(boundName, out var boundSchema))
            bound = isRule ? boundSchema.Rules.GetValueOrDefault(boundName.Leaf) : boundSchema.Defaults.GetValueOrDefault(boundName.Leaf);
        if (bound is null)
            throw isRule ? SimulatedSqlException.RuleDoesNotExist(objectName) : SimulatedSqlException.DefaultDoesNotExist(objectName);

        var target = ResolveBindTarget(batch, targetName);
        if (target.Column is { } column)
        {
            if (isRule)
            {
                if (!CanTakeRule(column))
                    throw SimulatedSqlException.CannotBindRuleToColumnKind();
                BindRule(batch, column, (RuleObject)bound);
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 194, 15514, "Rule bound to table column."));
            }
            else
            {
                if (!CanTakeDefault(column))
                    throw SimulatedSqlException.CannotBindDefaultToColumnKind();
                if (column.Identity is not null)
                    throw SimulatedSqlException.CannotBindDefaultToIdentity();
                if (column.DefaultConstraint is not null)
                    throw SimulatedSqlException.CannotBindDefaultOverConstraint();
                BindDefault(batch, column, (DefaultObject)bound);
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 197, 15511, "Default bound to column."));
            }
            yield break;
        }

        var alias = target.AliasType!;
        if (isRule)
        {
            var previous = alias.BoundRule;
            alias.BoundRule = (RuleObject)bound;
            RecordDdlUndo(batch, () => alias.BoundRule = previous);
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 247, 15515, "Rule bound to data type."));
            if (futureOnly)
                yield break;
            foreach (var typed in ColumnsOfAliasType(database, alias))
            {
                if (ReferenceEquals(typed.BoundRule, previous) && CanTakeRule(typed))
                    BindRule(batch, typed, (RuleObject)bound);
            }
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 302, 15516, "The new rule has been bound to column(s) of the specified user data type."));
        }
        else
        {
            var previous = alias.BoundDefault;
            alias.BoundDefault = (DefaultObject)bound;
            RecordDdlUndo(batch, () => alias.BoundDefault = previous);
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 250, 15512, "Default bound to data type."));
            if (futureOnly)
                yield break;
            foreach (var typed in ColumnsOfAliasType(database, alias))
            {
                if (ReferenceEquals(typed.BoundDefault, previous) && typed.DefaultConstraint is null && typed.Identity is null && CanTakeDefault(typed))
                    BindDefault(batch, typed, (DefaultObject)bound);
            }
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 303, 15513, "The new default has been bound to columns(s) of the specified user data type."));
        }
    }

    /// <summary>
    /// <c>sp_unbindefault @objname [, @futureonly]</c> and
    /// <c>sp_unbindrule @objname [, @futureonly]</c>, the inverse of
    /// <see cref="InvokeSpBind"/>: unbinding a type also unbinds the columns
    /// declared with it that carry the type's binding, unless
    /// <c>@futureonly</c> is <c>'futureonly'</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpUnbind(BatchContext batch, string procedureName, bool isRule)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (targetName, futureOnlyText, _) = ParseBindingArgs(arguments, procedureName, "objname", "futureonly", null);
        if (targetName is null)
            throw SimulatedSqlException.ProcedureExpectsParameter(procedureName, "objname");
        var futureOnly = futureOnlyText is not null && BuiltInToken.Equals(futureOnlyText, "futureonly");

        var target = ResolveBindTarget(batch, targetName);
        if (target.Column is { } column)
        {
            if (isRule ? column.BoundRule is null : column.BoundDefault is null)
                throw SimulatedSqlException.ColumnHasNothingBound(targetName, isRule);
            if (isRule)
                BindRule(batch, column, null);
            else
                BindDefault(batch, column, null);
            batch.Connection.PendingMessages.Enqueue(isRule
                ? SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 111, 15522, "Rule unbound from table column.")
                : SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 115, 15519, "Default unbound from table column."));
            yield break;
        }

        var alias = target.AliasType!;
        var previous = (isRule ? (BindableObject?)alias.BoundRule : alias.BoundDefault)
            ?? throw SimulatedSqlException.TypeHasNothingBound(targetName, isRule);
        if (isRule)
        {
            alias.BoundRule = null;
            RecordDdlUndo(batch, () => alias.BoundRule = (RuleObject)previous);
        }
        else
        {
            alias.BoundDefault = null;
            RecordDdlUndo(batch, () => alias.BoundDefault = (DefaultObject)previous);
        }
        batch.Connection.PendingMessages.Enqueue(isRule
            ? SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 150, 15523, "Rule unbound from data type.")
            : SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 161, 15520, "Default unbound from data type."));
        if (futureOnly)
            yield break;
        foreach (var typed in ColumnsOfAliasType(batch.CurrentDatabase, alias))
        {
            if (isRule && ReferenceEquals(typed.BoundRule, previous))
                BindRule(batch, typed, null);
            else if (!isRule && ReferenceEquals(typed.BoundDefault, previous))
                BindDefault(batch, typed, null);
        }
        batch.Connection.PendingMessages.Enqueue(isRule
            ? SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 203, 15524, "Columns of the specified user data type had their rules unbound.")
            : SimulatedSqlException.SystemProcedureMessage(batch, procedureName, 216, 15521, "Columns of the specified user data type had their defaults unbound."));
    }

    /// <summary>
    /// The positional-or-named arguments of the binding procedures, NULL and
    /// <c>DEFAULT</c> both reading as absent.
    /// </summary>
    private static (string? First, string? Second, string? Third) ParseBindingArgs(
        List<ProcArgument> arguments, string procedureName, string firstName, string secondName, string? thirdName)
    {
        var values = new string?[3];
        string?[] names = [firstName, secondName, thirdName];
        var positional = 0;
        foreach (var arg in arguments)
        {
            var slot = arg.Name is null
                ? positional++
                : Array.FindIndex(names, name => name is not null && BuiltInToken.Equals(arg.Name, name));
            if (slot < 0 || slot >= names.Length || names[slot] is null)
                throw SimulatedSqlException.InvalidProcedureParameters(procedureName);
            values[slot] = CatalogStringArg(arg);
        }
        return (values[0], values[1], values[2]);
    }

    /// <summary>
    /// What a binding procedure's <c>@objname</c> names: with a dot, a column
    /// of a table (<c>table.column</c> or <c>schema.table.column</c>), falling
    /// back to a schema-qualified alias type; without one, an alias type. A
    /// built-in type is Msg 4185 and anything else Msg 15148.
    /// </summary>
    private static (HeapColumn? Column, AliasType? AliasType) ResolveBindTarget(BatchContext batch, string written)
    {
        if (!ObjectId.TryParseObjectName(written, out var name))
            throw SimulatedSqlException.BindTargetDoesNotExist(written);
        if (name.Count >= 2)
        {
            var tableName = new MultiPartName(name[0]);
            for (var i = 1; i < name.Count - 1; i++)
                tableName = tableName.WithAddedPart(name[i]);
            if (batch.TryResolveTable(tableName, out var table))
            {
                foreach (var column in table.Columns)
                {
                    if (batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf))
                        return (column, null);
                }
            }
        }
        if (name.Count <= 2 && batch.TryResolveAliasType(name, out var alias))
            return (null, alias);
        if (name.Count == 1 && SqlType.IsSystemTypeName(name.Leaf))
            throw SimulatedSqlException.ActionNotAllowedOnSystemType();
        throw SimulatedSqlException.BindTargetDoesNotExist(written);
    }

    private static IEnumerable<HeapColumn> ColumnsOfAliasType(Database database, AliasType alias)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var column in table.Columns)
                {
                    if (ReferenceEquals(column.AliasType, alias))
                        yield return column;
                }
            }
        }
    }

    // Real's refusal lists for sp_bindefault (Msg 15101) and sp_bindrule
    // (Msg 15107): computed and sparse columns, rowversion, the MAX types, xml
    // and the CLR types; a rule refuses the legacy LOB types too.
    private static bool CanTakeDefault(HeapColumn column) =>
        column.Computed is null && !column.IsSparse
        && column.Type is not (RowVersionSqlType or XmlSqlType or SpatialSqlType or HierarchyIdSqlType)
        && column.MaxLength != SqlType.MaxLengthSentinel;

    private static bool CanTakeRule(HeapColumn column) => CanTakeDefault(column) && !column.Type.IsLob;

    private static void BindDefault(BatchContext batch, HeapColumn column, DefaultObject? bound)
    {
        var (previousDefault, previousBound) = (column.Default, column.BoundDefault);
        column.Default = bound?.Expression;
        column.BoundDefault = bound;
        RecordDdlUndo(batch, () => (column.Default, column.BoundDefault) = (previousDefault, previousBound));
    }

    private static void BindRule(BatchContext batch, HeapColumn column, RuleObject? bound)
    {
        var previous = column.BoundRule;
        column.BoundRule = bound;
        RecordDdlUndo(batch, () => column.BoundRule = previous);
    }

    /// <summary>
    /// Takes the alias type's bound default and rule onto a column just
    /// declared with it, the way real's column creation copies them.
    /// </summary>
    internal static void InheritAliasTypeBindings(HeapColumn column)
    {
        if (column.AliasType is not { } alias)
            return;
        if (alias.BoundDefault is { } boundDefault && column.Default is null && column.Identity is null && CanTakeDefault(column))
        {
            column.Default = boundDefault.Expression;
            column.BoundDefault = boundDefault;
        }
        if (alias.BoundRule is { } boundRule && CanTakeRule(column))
            column.BoundRule = boundRule;
    }

    /// <summary>
    /// Raises Msg 513 when the rule bound to <paramref name="table"/>'s column
    /// at <paramref name="ordinal"/> answers false for the value being written
    /// there; UNKNOWN passes, as a CHECK constraint's does.
    /// </summary>
    private static void EnforceRule(HeapTable table, SqlValue[] values, int ordinal, BatchContext batch)
    {
        var column = table.Columns[ordinal];
        if (column.BoundRule is not { } rule)
            return;
        var value = values[ordinal];
        if (rule.Predicate.Run(new RuntimeContext(_ => value, batch)) == false)
        {
            throw SimulatedSqlException.RuleViolation(
                DatabaseNameFor(table), SchemaQualifiedName(table, table.OwningDatabase), column.Name);
        }
    }
}
