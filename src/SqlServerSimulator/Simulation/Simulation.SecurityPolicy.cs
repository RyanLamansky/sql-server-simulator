using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// CREATE / ALTER / DROP SECURITY POLICY, and the compile of one security
// predicate for the statements that apply it. Every refusal, its number,
// state and order was probed 2026-10-04 against SQL Server 2025; see
// docs/claude/row-level-security.md.
partial class Simulation
{
    private enum PredicateClauseVerb
    {
        Add,
        Alter,
        Drop,
    }

    /// <summary>One <c>ADD</c> / <c>ALTER</c> / <c>DROP</c> predicate clause as written.</summary>
    private sealed class PredicateClause(
        PredicateClauseVerb verb, SecurityPredicateKind kind, MultiPartName? function,
        Expression[] arguments, string[] argumentDefinitions, MultiPartName target, BlockOperation? operation)
    {
        public readonly PredicateClauseVerb Verb = verb;
        public readonly SecurityPredicateKind Kind = kind;
        public readonly MultiPartName? Function = function;
        public readonly Expression[] Arguments = arguments;
        public readonly string[] ArgumentDefinitions = argumentDefinitions;
        public readonly MultiPartName Target = target;
        public readonly BlockOperation? Operation = operation;
    }

    /// <summary>
    /// Parses <c>CREATE SECURITY POLICY name [ADD … [, …]] [WITH (STATE = …,
    /// SCHEMABINDING = …)] [NOT FOR REPLICATION]</c>. Cursor on entry: the
    /// <c>SECURITY</c> word. A policy may carry no predicate at all. A word
    /// after the name that begins no clause ends the statement, so <c>DROP
    /// FILTER …</c> there is a statement of its own (Msg 343).
    /// </summary>
    private static bool TryParseCreateSecurityPolicy(ParserContext context)
    {
        if (!AtPolicyWord(context))
            return false;
        context.MoveNextRequired();
        var policyName = ParsePolicyName(context, "CREATE SECURITY POLICY");
        context.MoveNextOptional();

        var clauses = new List<PredicateClause>();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add })
            ParsePredicateClauses(context, clauses, "CREATE");
        else if (context.Token is UnquotedString)
            throw SimulatedSqlException.SyntaxErrorNear(context.Token);
        bool? state = null, schemaBinding = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            (state, schemaBinding) = ParsePolicyOptions(context, allowSchemaBinding: true);
        var notForReplication = TryConsumeNotForReplication(context);

        // Real binds the policy as the batch compiles — before any statement
        // ahead of it runs, and in an IF branch never taken — so every binding
        // error stops the batch whole; the permission checks, the name's
        // uniqueness and the one-enabled-policy rule wait for the run.
        var batch = context.Batch;
        if (!batch.TryResolveSchema(policyName, out var schema))
            throw SimulatedSqlException.SecurityPolicySchemaMissing(policyName.ImmediateQualifier ?? Database.DefaultSchemaName);
        var database = schema.Database;
        var isEnabled = state ?? true;
        var isSchemaBound = schemaBinding ?? true;
        var qualifiedName = $"{schema.Name}.{policyName.Leaf}";
        var predicates = new List<SecurityPredicate>();
        foreach (var clause in clauses)
        {
            var predicate = BindPredicate(batch, schema.Database, clause, predicates.Count + 1, qualifiedName, isSchemaBound);
            if (FindOverlap(predicates, predicate) is not null)
                throw SimulatedSqlException.SecurityPredicateAlreadyDefined(KindWord(predicate.Kind), TargetName(database, predicate.Target), qualifiedName, state: 1);
            predicates.Add(predicate);
        }
        if (batch.IsSkipping)
            return true;

        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HoldsDatabasePermission(batch, database, "ALTER ANY SECURITY POLICY"))
            throw SimulatedSqlException.CreateSecurityPolicyDenied(database.Name);
        if (!PermissionEnforcement.HasSchemaAlter(batch, schema))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
        RequireSelectOnPredicateFunctions(batch, database, predicates);
        if (schema.HasNameInSharedNamespace(policyName.Leaf))
            throw SimulatedSqlException.NameTakenEndingOnlyStatement(policyName.Leaf, state: 8).EndingBatch();
        if (isEnabled)
            RejectSecondEnabledPolicy(database, null, qualifiedName, predicates);

        var now = batch.CurrentStatement.UtcNow;
        var policy = new SecurityPolicy(schema, policyName.Leaf, context.CurrentDatabase.AllocateObjectId(), now,
            new SecurityPolicyState(isEnabled, isSchemaBound, notForReplication, [.. predicates], predicates.Count + 1));
        if (!schema.SecurityPolicies.TryAdd(policyName.Leaf, policy))
            throw SimulatedSqlException.NameTakenEndingOnlyStatement(policyName.Leaf, state: 8);
        RecordSlotUndo<SecurityPolicy>(context, schema.SecurityPolicies, policyName.Leaf, null);
        TouchPredicateTargets(context, predicates, now);
        batch.Connection.Simulation.DeclaresSecurityPolicies = true;
        RecordDdlEvent(context, "CREATE_SECURITY_POLICY", schema.Name, policyName.Leaf, "SECURITY POLICY");
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SECURITY POLICY name</c> followed either by a list of
    /// <c>ADD</c> / <c>ALTER</c> / <c>DROP</c> predicate clauses or by
    /// <c>WITH (STATE = …)</c> alone: <c>SCHEMABINDING</c> and <c>NOT FOR
    /// REPLICATION</c> are fixed at <c>CREATE</c>, and a <c>WITH</c> after a
    /// clause list begins a statement of its own. Cursor on entry: the
    /// <c>SECURITY</c> word.
    /// </summary>
    private static bool TryParseAlterSecurityPolicy(ParserContext context)
    {
        if (!AtPolicyWord(context))
            return false;
        context.MoveNextRequired();
        var policyName = ParsePolicyName(context, "ALTER SECURITY POLICY");
        context.MoveNextRequired();

        var clauses = new List<PredicateClause>();
        bool? state = null;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Add or Keyword.Alter or Keyword.Drop }:
                ParsePredicateClauses(context, clauses, "ALTER");
                break;
            case ReservedKeyword { Keyword: Keyword.With }:
                (state, _) = ParsePolicyOptions(context, allowSchemaBinding: false);
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context.Token);
        }

        // Bound as the batch compiles, as CREATE's predicates are.
        var batch = context.Batch;
        if (!batch.TryResolveSchema(policyName, out var schema) || !schema.SecurityPolicies.TryGetValue(policyName.Leaf, out var policy))
            throw SimulatedSqlException.SecurityPolicyObjectNotFound(policyName.ToString(), state: 2);
        var database = schema.Database;
        var qualifiedName = $"{schema.Name}.{policy.Name}";
        if (state is null)
            _ = ApplyPredicateClauses(batch, database, policy, qualifiedName, clauses, out _);
        if (batch.IsSkipping)
            return true;

        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HoldsDatabasePermission(batch, database, "ALTER ANY SECURITY POLICY")
            || !PermissionEnforcement.HasObjectAlter(batch, database, policy.ObjectId, policy.SchemaId))
        {
            throw SimulatedSqlException.SecurityPolicyObjectNotFound(policyName.ToString(), state: 7).EndingBatch();
        }

        var before = policy.State;
        SecurityPolicyState after;
        if (state is { } enable)
        {
            if (enable && !before.IsEnabled)
                RejectSecondEnabledPolicy(database, policy, qualifiedName, before.Predicates);
            after = new SecurityPolicyState(enable, before.IsSchemaBound, before.NotForReplication, before.Predicates, before.NextPredicateId);
        }
        else
        {
            after = ApplyPredicateClauses(batch, database, policy, qualifiedName, clauses, out var added);
            RequireSelectOnPredicateFunctions(batch, database, added);
            if (before.IsEnabled)
                RejectSecondEnabledPolicy(database, policy, qualifiedName, added);
        }

        var now = batch.CurrentStatement.UtcNow;
        var modified = policy.ModifyDate;
        policy.State = after;
        policy.ModifyDate = now;
        RecordDdlUndo(context, () =>
        {
            policy.State = before;
            policy.ModifyDate = modified;
        });
        TouchPredicateTargets(context, after.Predicates, now);
        RecordDdlEvent(context, "ALTER_SECURITY_POLICY", schema.Name, policy.Name, "SECURITY POLICY");
        return true;
    }

    /// <summary>
    /// Parses <c>DROP SECURITY POLICY [IF EXISTS] name [, …]</c>. Cursor on
    /// entry: the <c>SECURITY</c> word. Dropping takes
    /// <c>ALTER ANY SECURITY POLICY</c> and <c>ALTER</c> on the policy's
    /// schema; without them the policy reads as missing, class 14 rather than
    /// a missing one's 11.
    /// </summary>
    private static bool TryParseDropSecurityPolicy(ParserContext context)
    {
        if (!AtPolicyWord(context))
            return false;
        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ifExists = true;
            context.MoveNextRequired();
        }
        var names = new List<MultiPartName>();
        while (true)
        {
            names.Add(BatchContext.ParseObjectName(context));
            if (context.GetNextOptional() is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }

        if (context.Batch.IsSkipping)
            return true;

        var batch = context.Batch;
        foreach (var name in names)
        {
            if (!batch.TryResolveSchema(name, out var schema))
            {
                if (ifExists)
                    continue;
                throw SimulatedSqlException.CannotDropSecurityPolicy(name.ToString(), permission: false);
            }
            RejectDropOfOtherKind(schema, name, "SECURITY POLICY");
            if (!schema.SecurityPolicies.TryGetValue(name.Leaf, out var policy))
            {
                if (ifExists)
                    continue;
                throw SimulatedSqlException.CannotDropSecurityPolicy(name.ToString(), permission: false);
            }
            schema.Database.RejectWriteWhenReadOnly();
            if (!PermissionEnforcement.HoldsDatabasePermission(batch, schema.Database, "ALTER ANY SECURITY POLICY")
                || !PermissionEnforcement.HasDropAuthority(batch, schema, policy.ObjectId))
            {
                throw SimulatedSqlException.CannotDropSecurityPolicy(policy.Name, permission: true);
            }
            if (!schema.SecurityPolicies.TryRemove(name.Leaf, out var dropped))
                continue;
            RecordSlotUndo(context, schema.SecurityPolicies, name.Leaf, dropped);
            TouchPredicateTargets(context, dropped.State.Predicates, batch.CurrentStatement.UtcNow);
            RecordDdlEvent(context, "DROP_SECURITY_POLICY", schema.Name, dropped.Name, "SECURITY POLICY");
        }
        return true;
    }

    /// <summary>
    /// Raises Msg 343 for the <c>FILTER</c> / <c>BLOCK</c> word a <c>DROP</c>
    /// or <c>ALTER</c> predicate clause written after a <c>CREATE SECURITY
    /// POLICY</c> leaves where an object type belongs: real reads the clause
    /// as a statement of its own naming an unknown object type. False for any
    /// other word, which the caller's syntax error answers.
    /// </summary>
    private static bool RejectPredicateClauseWord(ParserContext context) =>
        context.Token is UnquotedString { Value: var word } && (BuiltInToken.Equals(word, "FILTER") || BuiltInToken.Equals(word, "BLOCK"))
            ? throw SimulatedSqlException.UnknownObjectType(word)
            : false;

    private static bool AtPolicyWord(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is Name { Value: var word } && BuiltInToken.Equals(word, "POLICY"))
            return true;
        context.RestoreCheckpoint(checkpoint);
        return false;
    }

    /// <summary>
    /// The policy's name, the cursor on its first part and left on its last: a
    /// database-qualified one is Msg 166 and a server-qualified one Msg 117.
    /// </summary>
    private static MultiPartName ParsePolicyName(ParserContext context, string statement)
    {
        var name = BatchContext.ParseObjectName(context);
        if (name.Count > 3)
            throw SimulatedSqlException.TooManyNamePrefixes(name, 2);
        if (name.Count == 3)
            throw SimulatedSqlException.SecurityPolicyNameDatabaseQualified(statement).PinLine(12);
        return name;
    }

    /// <summary>
    /// Parses one or more comma-separated predicate clauses, the cursor on the
    /// first clause's verb; leaves it past the last. <paramref name="verb"/>
    /// is the statement's own, which a CREATE limits to <c>ADD</c>.
    /// </summary>
    private static void ParsePredicateClauses(ParserContext context, List<PredicateClause> clauses, string verb)
    {
        while (true)
        {
            var clauseVerb = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Add } => PredicateClauseVerb.Add,
                ReservedKeyword { Keyword: Keyword.Alter } when verb == "ALTER" => PredicateClauseVerb.Alter,
                ReservedKeyword { Keyword: Keyword.Drop } when verb == "ALTER" => PredicateClauseVerb.Drop,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            clauses.Add(ParsePredicateClause(context, clauseVerb, verb));
            if (context.Token is not Operator { Character: ',' })
                return;
            context.MoveNextRequired();
        }
    }

    private static PredicateClause ParsePredicateClause(ParserContext context, PredicateClauseVerb clauseVerb, string statementVerb)
    {
        var kind = context.GetNextRequired() switch
        {
            Name { Value: var word } when BuiltInToken.Equals(word, "FILTER") => SecurityPredicateKind.Filter,
            Name { Value: var word } when BuiltInToken.Equals(word, "BLOCK") => SecurityPredicateKind.Block,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        if (context.GetNextRequired() is not Name { Value: var predicateWord } || !BuiltInToken.Equals(predicateWord, "PREDICATE"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        MultiPartName? function = null;
        var arguments = new List<Expression>();
        var definitions = new List<string>();
        if (clauseVerb != PredicateClauseVerb.Drop)
        {
            function = BatchContext.ParseObjectName(context);
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ')' })
            {
                while (true)
                {
                    var start = context.Token!.StartIndex;
                    Expression argument;
                    using (ParserScope.Enter(ref context.InScalarDefinition, true))
                        argument = Expression.Parse(context);
                    var hasVariable = false;
                    argument.Walk((node, _) =>
                    {
                        hasVariable |= node is VariableReference;
                        return !hasVariable;
                    });
                    if (hasVariable)
                        throw SimulatedSqlException.VariablesNotAllowedInSecurityPolicy(statementVerb);
                    arguments.Add(argument);
                    definitions.Add(context.CanonicalDefinitionFrom(start, predicate: false) is { } canonical
                        ? canonical[1..^1]
                        : context.SourceTextFrom(start));
                    if (context.Token is Operator { Character: ')' })
                        break;
                    if (context.Token is not Operator { Character: ',' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                }
            }
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var target = BatchContext.ParseObjectName(context);
        if (target.Count > 3)
            throw SimulatedSqlException.TooManyNamePrefixes(target, 2);
        if (target.Count == 3)
        {
            var clause = $"{(clauseVerb == PredicateClauseVerb.Drop ? "DROP" : "ADD/ALTER")} {KindWord(kind)} PREDICATE";
            throw SimulatedSqlException.SecurityPolicyNameDatabaseQualified(clause).PinLine(12);
        }
        context.MoveNextOptional();

        BlockOperation? operation = null;
        if (context.Token is Name { Value: var timing } && (BuiltInToken.Equals(timing, "AFTER") || BuiltInToken.Equals(timing, "BEFORE")))
        {
            if (kind == SecurityPredicateKind.Filter)
                throw SimulatedSqlException.FilterPredicateTakesNoOperation();
            var after = BuiltInToken.Equals(timing, "AFTER");
            operation = (after, context.GetNextRequired()) switch
            {
                (true, ReservedKeyword { Keyword: Keyword.Insert }) => BlockOperation.AfterInsert,
                (true, ReservedKeyword { Keyword: Keyword.Update }) => BlockOperation.AfterUpdate,
                (false, ReservedKeyword { Keyword: Keyword.Update }) => BlockOperation.BeforeUpdate,
                (false, ReservedKeyword { Keyword: Keyword.Delete }) => BlockOperation.BeforeDelete,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextOptional();
        }
        return new PredicateClause(clauseVerb, kind, function, [.. arguments], [.. definitions], target, operation);
    }

    /// <summary>
    /// Parses <c>WITH ( option = ON | OFF [, …] )</c>, the cursor on
    /// <c>WITH</c> and left past the <c>)</c>. An option written twice is Msg
    /// 1039; <c>SCHEMABINDING</c> is a <c>CREATE</c> option only.
    /// </summary>
    private static (bool? State, bool? SchemaBinding) ParsePolicyOptions(ParserContext context, bool allowSchemaBinding)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        bool? state = null, schemaBinding = null;
        context.MoveNextRequired();
        while (true)
        {
            if (context.Token is not Name { Value: var option })
                throw SimulatedSqlException.SyntaxErrorNear(context.Token);
            var isState = BuiltInToken.Equals(option, "STATE");
            if (!isState && !(allowSchemaBinding && BuiltInToken.Equals(option, "SCHEMABINDING")))
                throw SimulatedSqlException.SyntaxErrorNear(context.Token);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var value = context.GetNextRequired() switch
            {
                ReservedKeyword { Keyword: Keyword.On } => true,
                ReservedKeyword { Keyword: Keyword.Off } => false,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context.Token),
            };
            if (isState)
            {
                if (state is not null)
                    throw SimulatedSqlException.OptionSpecifiedMoreThanOnce("STATE");
                state = value;
            }
            else
            {
                if (schemaBinding is not null)
                    throw SimulatedSqlException.OptionSpecifiedMoreThanOnce("SCHEMABINDING");
                schemaBinding = value;
            }
            context.MoveNextRequired();
            if (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                continue;
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return (state, schemaBinding);
        }
    }

    /// <summary>
    /// The policy state an <c>ALTER SECURITY POLICY</c>'s clauses leave, in
    /// written order: an <c>ALTER</c> or <c>DROP</c> names a predicate the
    /// policy held before the statement — kind, table and operation exactly —
    /// and an <c>ADD</c> clashes with one it still holds (Msg 33262 state 2)
    /// or one the statement itself added or dropped (state 1).
    /// </summary>
    private static SecurityPolicyState ApplyPredicateClauses(BatchContext batch, Database database, SecurityPolicy policy, string qualifiedName, List<PredicateClause> clauses, out List<SecurityPredicate> added)
    {
        var before = policy.State;
        var kept = new List<SecurityPredicate>(before.Predicates);
        var touched = new List<SecurityPredicate>();
        added = [];
        var nextId = before.NextPredicateId;
        foreach (var clause in clauses)
        {
            if (clause.Verb == PredicateClauseVerb.Add)
            {
                var predicate = BindPredicate(batch, database, clause, nextId, qualifiedName, before.IsSchemaBound);
                if (FindOverlap(kept, predicate) is not null)
                    throw SimulatedSqlException.SecurityPredicateAlreadyDefined(KindWord(predicate.Kind), TargetName(database, predicate.Target), qualifiedName, state: 2);
                if (FindOverlap(touched, predicate) is not null)
                    throw SimulatedSqlException.SecurityPredicateAlreadyDefined(KindWord(predicate.Kind), TargetName(database, predicate.Target), qualifiedName, state: 1);
                nextId++;
                touched.Add(predicate);
                added.Add(predicate);
                continue;
            }

            var target = ResolvePredicateTarget(database, clause.Target);
            var index = target is null ? -1 : kept.FindIndex(existing =>
                ReferenceEquals(existing.Target, target) && existing.Kind == clause.Kind && existing.Operation == clause.Operation);
            if (index < 0 || !before.Predicates.Contains(kept[index]))
                throw SimulatedSqlException.SecurityPolicyHasNoPredicateOn(qualifiedName, target is null ? clause.Target.ToString() : TargetName(database, target));
            var existing = kept[index];
            kept.RemoveAt(index);
            touched.Add(existing);
            if (clause.Verb == PredicateClauseVerb.Alter)
                added.Add(BindPredicate(batch, database, clause, existing.Id, qualifiedName, before.IsSchemaBound));
        }
        kept.AddRange(added);
        kept.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return new SecurityPolicyState(before.IsEnabled, before.IsSchemaBound, before.NotForReplication, [.. kept], nextId);
    }

    /// <summary>
    /// Binds one <c>ADD</c> or <c>ALTER</c> clause: the function resolves to an
    /// inline table-valued one, schema bound under a schema-bound policy and
    /// named in two parts there; the target to a user table or a schema-bound
    /// view; the arguments to the target's columns, one per parameter.
    /// </summary>
    private static SecurityPredicate BindPredicate(BatchContext batch, Database database, PredicateClause clause, int id, string qualifiedPolicyName, bool schemaBound)
    {
        var functionName = clause.Function!.Value;
        var written = functionName.ToString();
        UserDefinedFunction? found = null;
        if ((functionName.Count < 3 || database.Collation.Equals(functionName[0], database.Name))
            && database.Schemas.TryGetValue(functionName.Count >= 2 ? functionName[functionName.Count - 2] : Database.DefaultSchemaName, out var functionSchema))
        {
            _ = functionSchema.Functions.TryGetValue(functionName.Leaf, out found);
        }
        if (found is not InlineTableValuedFunction function)
            throw SimulatedSqlException.PredicateFunctionNotInlineTableValued(written);
        if (schemaBound && functionName.Count != 2)
            throw SimulatedSqlException.CannotSchemaBindInvalidName("security policy", qualifiedPolicyName, written);
        if (schemaBound && !function.IsSchemaBound)
            throw SimulatedSqlException.CannotSchemaBindNotSchemaBound("security policy", qualifiedPolicyName, $"{function.Schema.Name}.{function.Name}");

        var targetName = clause.Target;
        if (BatchContext.IsLocalTempName(targetName.Leaf) || BatchContext.IsGlobalTempName(targetName.Leaf))
            throw SimulatedSqlException.SecurityPredicateOnTemporaryObject();
        var target = ResolvePredicateTarget(database, targetName)
            ?? throw SimulatedSqlException.SecurityPolicyObjectNotFound(targetName.ToString(), state: 1);
        if (schemaBound && targetName.Count != 2 && target is HeapTable or View)
            throw SimulatedSqlException.CannotSchemaBindInvalidName("security policy", qualifiedPolicyName, targetName.ToString());
        HeapColumn[] columns;
        switch (target)
        {
            case HeapTable table:
                if (clause.Kind == SecurityPredicateKind.Block && table.IsHistoryTable)
                    throw SimulatedSqlException.BlockPredicateOnHistoryTable(TargetName(database, table));
                if (table.DependentIndexedViews.Count != 0)
                {
                    var view = table.DependentIndexedViews[0];
                    throw SimulatedSqlException.SecurityPolicyTableInIndexedView(qualifiedPolicyName, TargetName(database, table), $"{view.Schema.Name}.{view.Name}");
                }
                columns = table.Columns;
                break;
            case View view when view.IsSchemaBound || schemaBound:
                if (!view.IsSchemaBound)
                    throw SimulatedSqlException.CannotSchemaBindNotSchemaBound("security policy", qualifiedPolicyName, $"{view.Schema.Name}.{view.Name}");
                if (clause.Kind == SecurityPredicateKind.Block)
                    throw SimulatedSqlException.BlockPredicateTargetKind($"{view.Schema.Name}.{view.Name}");
                columns = view.OutputColumns;
                break;
            default:
                throw SimulatedSqlException.SecurityPredicateTargetKind(targetName.ToString());
        }

        SqlType ResolveColumnType(MultiPartName name)
        {
            foreach (var column in columns)
            {
                if (database.Collation.Equals(column.Name, name.Leaf))
                    return column.Type;
            }
            throw SimulatedSqlException.InvalidColumnName(name);
        }
        foreach (var argument in clause.Arguments)
            _ = argument.GetSqlType(batch, ResolveColumnType);
        var qualifiedFunction = $"{function.Schema.Name}.{function.Name}";
        if (clause.Arguments.Length > function.Parameters.Length)
            throw SimulatedSqlException.TooManyArgumentsToFunction(qualifiedFunction, state: 3).PinLine(1);
        if (clause.Arguments.Length < function.Parameters.Length)
            throw SimulatedSqlException.InsufficientArgumentsToFunction(qualifiedFunction, state: 3).PinLine(1);

        var spelledFunction = functionName.Count >= 2
            ? $"[{Bracketed(functionName[functionName.Count - 2])}].[{Bracketed(functionName.Leaf)}]"
            : $"[{Bracketed(functionName.Leaf)}]";
        var definition = $"({spelledFunction}({string.Join(",", clause.ArgumentDefinitions)}))";
        return new SecurityPredicate(
            id, target, clause.Kind, clause.Operation,
            function.Schema.Name, function.Name, clause.Arguments, definition, schemaBound);

        static string Bracketed(string part) => part.Replace("]", "]]", StringComparison.Ordinal);
    }

    /// <summary>
    /// The table, view or other object a predicate's target names in
    /// <paramref name="database"/>, or null for none — a system object
    /// included.
    /// </summary>
    private static SchemaObject? ResolvePredicateTarget(Database database, MultiPartName name)
    {
        var schemaName = name.Count >= 2 ? name[name.Count - 2] : Database.DefaultSchemaName;
        return database.Schemas.TryGetValue(schemaName, out var schema) && schema.TryFindInSharedNamespace(name.Leaf, out var found)
            ? found
            : null;
    }

    /// <summary>
    /// The predicate in <paramref name="predicates"/> that guards what
    /// <paramref name="candidate"/> does — the same kind on the same target,
    /// and for a block predicate an operation either guards — or null.
    /// </summary>
    private static SecurityPredicate? FindOverlap(List<SecurityPredicate> predicates, SecurityPredicate candidate) =>
        predicates.Find(existing =>
            ReferenceEquals(existing.Target, candidate.Target)
            && existing.Kind == candidate.Kind
            && (existing.Operation is null || candidate.Operation is null || existing.Operation == candidate.Operation));

    /// <summary>
    /// Raises Msg 33264 when a target among <paramref name="predicates"/>
    /// already takes predicates from an enabled policy other than
    /// <paramref name="policy"/>.
    /// </summary>
    private static void RejectSecondEnabledPolicy(Database database, SecurityPolicy? policy, string qualifiedName, IEnumerable<SecurityPredicate> predicates)
    {
        foreach (var predicate in predicates)
        {
            foreach (var (_, schema) in database.Schemas)
            {
                foreach (var (_, other) in schema.SecurityPolicies)
                {
                    if (ReferenceEquals(other, policy) || !other.State.IsEnabled)
                        continue;
                    if (Array.Exists(other.State.Predicates, existing => ReferenceEquals(existing.Target, predicate.Target)))
                        throw SimulatedSqlException.SecurityPolicyTableAlreadyReferenced(qualifiedName, TargetName(database, predicate.Target), $"{other.Schema.Name}.{other.Name}");
                }
            }
        }
    }

    /// <summary>
    /// The pair of Msg 229 a principal without <c>SELECT</c> on a predicate's
    /// function earns from the statement adding the predicate.
    /// </summary>
    private static void RequireSelectOnPredicateFunctions(BatchContext batch, Database database, List<SecurityPredicate> predicates)
    {
        foreach (var predicate in predicates)
        {
            if (database.Schemas.TryGetValue(predicate.FunctionSchema, out var schema)
                && schema.Functions.TryGetValue(predicate.FunctionName, out var function)
                && PermissionEnforcement.SchemaObjectDenial(batch, "SELECT", function) is not null)
            {
                throw SimulatedSqlException.PredicateFunctionSelectDenied(function.Name, database.Name, schema.Name);
            }
        }
    }

    /// <summary>
    /// Advances the <c>modify_date</c> of each table a policy statement put a
    /// predicate on or took one from, as real does.
    /// </summary>
    private static void TouchPredicateTargets(ParserContext context, IEnumerable<SecurityPredicate> predicates, DateTime now)
    {
        foreach (var predicate in predicates)
        {
            var target = predicate.Target;
            var modified = target.ModifyDate;
            if (modified == now)
                continue;
            target.ModifyDate = now;
            RecordDdlUndo(context, () => target.ModifyDate = modified);
        }
    }

    /// <summary>
    /// Raises Msg 33266 when a table the view reads carries a predicate of any
    /// security policy, enabled or not.
    /// </summary>
    private void RejectIndexOverSecurityPolicyTable(BatchContext batch, View view, string qualifiedViewName)
    {
        var tables = new HashSet<HeapTable>();
        this.CollectViewBaseTables(batch, view, tables, []);
        var database = view.Schema.Database;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, policy) in schema.SecurityPolicies)
            {
                foreach (var predicate in policy.State.Predicates)
                {
                    if (predicate.Target is HeapTable table && tables.Contains(table))
                        throw SimulatedSqlException.IndexedViewOverSecurityPolicyTable(qualifiedViewName, TargetName(database, table));
                }
            }
        }
    }

    private static string KindWord(SecurityPredicateKind kind) => kind == SecurityPredicateKind.Filter ? "FILTER" : "BLOCK";

    private static string TargetName(Database database, SchemaObject target)
    {
        foreach (var (name, schema) in database.Schemas)
        {
            if (schema.SchemaId == target.SchemaId)
                return $"{name}.{target.Name}";
        }
        return target.Name;
    }

    /// <summary>
    /// Compiles <paramref name="predicate"/> for the statements of
    /// <paramref name="batch"/>: resolves its function and the target's
    /// columns as they stand — for a non-schema-bound policy that is where a
    /// dropped or reshaped function, or a dropped column, fails with its own
    /// binding error and Msg 33512, and where the reading principal needs
    /// <c>SELECT</c> on the function — then parses the function's body once.
    /// </summary>
    internal SecurityPredicateRunner CompileSecurityPredicate(BatchContext batch, SecurityPredicate predicate, long schemaVersion)
    {
        var target = predicate.Target;
        var table = target as HeapTable;
        var targetColumns = table?.Columns ?? ((View)target).OutputColumns;
        var database = batch.DatabaseFor(target);
        UserDefinedFunction? found = null;
        if (database.Schemas.TryGetValue(predicate.FunctionSchema, out var functionSchema))
            _ = functionSchema.Functions.TryGetValue(predicate.FunctionName, out found);
        if (found is not InlineTableValuedFunction function)
        {
            throw SimulatedSqlException.FollowedByPredicateBindingFailure(
                SimulatedSqlException.InvalidObjectName(new MultiPartName(predicate.FunctionSchema).WithAddedPart(predicate.FunctionName)), target.Name, state: 0);
        }
        var qualifiedFunction = $"{function.Schema.Name}.{function.Name}";
        if (function.Parameters.Length != predicate.Arguments.Length)
        {
            var countError = function.Parameters.Length > predicate.Arguments.Length
                ? SimulatedSqlException.InsufficientArgumentsToFunction(qualifiedFunction, state: 3).PinLine(12)
                : SimulatedSqlException.TooManyArgumentsToFunction(qualifiedFunction, state: 3).PinLine(12);
            throw SimulatedSqlException.FollowedByPredicateBindingFailure(countError, target.Name, state: 2);
        }
        foreach (var argument in predicate.Arguments)
        {
            argument.VisitColumnReferences(name =>
            {
                if (!Array.Exists(targetColumns, column => database.Collation.Equals(column.Name, name.Leaf)))
                    throw SimulatedSqlException.FollowedByPredicateBindingFailure(SimulatedSqlException.InvalidColumnName(name).PinLine(1), target.Name, state: 2);
            });
        }

        var connection = batch.Connection;
#pragma warning disable CA2100, CA2000 // function.BodyText is the function's pre-validated stored body, not external input; the runner's batch holds the command, whose Dispose releases nothing
        var bodyCommand = new SimulatedDbCommand(this, connection) { CommandText = function.BodyText };
#pragma warning restore CA2100, CA2000
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var slots = new VariableSlot[function.Parameters.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            var parameter = function.Parameters[i];
            slots[i] = variables[parameter.Name] = new VariableSlot(parameter.Type, declaredMaxLength: parameter.DeclaredMaxLength, SqlValue.Null(parameter.Type), parameter: null) { SpelledNumeric = parameter.SpelledNumeric };
        }
        var inner = new BatchContext(bodyCommand, variables, new UdfFrame(SqlType.Int32))
        {
            SuppressDiagnosticsResolution = true,
            BindsModuleDefinition = true,
            SuppressesRowSecurity = true,
        };
        inner.AdoptStatementFreezeFrom(batch);
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.QuotedIdentifiers = function.UsesQuotedIdentifier;
        connection.AnsiNulls = function.UsesAnsiNulls;
        var scope = ModuleDatabaseScope.Enter(connection, function.Schema.Database, bindsIdentity: false);
        Selection body;
        try
        {
            var parser = inner.Parser;
            parser.MoveNextRequired();
            body = ParseInlineTvfBody(parser, function, new MultiPartName(function.Schema.Name).WithAddedPart(function.Name));
        }
        finally
        {
            scope.Exit();
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
            inner.ReleaseStatementSchemaLocks();
        }
        return new SecurityPredicateRunner(batch, function, inner, body, slots, predicate.Arguments, table, targetColumns, database.Collation, schemaVersion);
    }
}
