using System.Xml;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// The statement-level OPTION (…) clause, and the checks its hints and the
// inline join hints make once the statement's queries have parsed.
partial class Selection
{
    /// <summary>The join algorithms a join hint names, as a set.</summary>
    [Flags]
    internal enum JoinAlgorithms : byte
    {
        None = 0,
        Loop = 1,
        Hash = 2,
        Merge = 4,
    }

    /// <summary>
    /// What a statement's <c>OPTION</c> clause settled that the checks run
    /// after it read; the hints with no modeled effect leave nothing here.
    /// </summary>
    internal sealed class OptionClause
    {
        /// <summary>The join algorithms the clause's join hints allow; none when it names none.</summary>
        public JoinAlgorithms Joins;

        /// <summary><c>FORCE ORDER</c>, which withholds the inline join hints' Msg 8625.</summary>
        public bool ForceOrder;

        public int? MaxDop;
        public int? Fast;
        public int? MaxRecursion;
        public decimal? MinGrantPercent;
        public decimal? MaxGrantPercent;
        public bool Label;

        /// <summary>The variables the <c>OPTIMIZE FOR</c> clauses named, for Msg 4131.</summary>
        public HashSet<string>? OptimizedVariables;

        /// <summary>The <c>TABLE HINT</c> clauses, each with its object as written.</summary>
        public List<(string Written, TableHintInfo Hints)>? TableHints;

        /// <summary><c>HASH GROUP</c>, which a grouped CLR aggregate can't be planned under (Msg 8622).</summary>
        public bool HashGroup;
    }

    /// <summary>
    /// One query specification's FROM clause and filters, recorded as it
    /// parses for the checks the statement's hints make once its
    /// <c>OPTION</c> clause has parsed (see <see cref="ParserContext.JoinHintSites"/>).
    /// </summary>
    internal sealed class JoinHintSite(FromSource[] sources, JoinSpec[] joins, List<BooleanExpression> filters)
    {
        public readonly FromSource[] Sources = sources;
        public readonly JoinSpec[] Joins = joins;

        /// <summary>The WHERE and HAVING conjunct roots, which can supply a join's equality.</summary>
        public readonly List<BooleanExpression> Filters = filters;
    }

    /// <summary>
    /// Parses the trailing <c>OPTION (hint [, …])</c> clause, validating each
    /// hint's grammar and arguments as real does (probed 2026-10-05 against
    /// SQL Server 2025). <c>MAXRECURSION</c> overrides every in-scope CTE's
    /// recursion limit, <c>RECOMPILE</c> and <c>USE HINT</c> set their
    /// statement flags, and the join, <c>FORCE ORDER</c> and <c>TABLE HINT</c>
    /// hints are returned for <see cref="SettleStatementHints"/>. A Query
    /// Store hint clause parses here too. Cursor on entry: the <c>OPTION</c>
    /// keyword; on exit, the token after its <c>)</c>.
    /// </summary>
    internal static OptionClause ParseOptionClause(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var clause = new OptionClause();
        while (true)
        {
            context.MoveNextRequired();
            ConsumeOneOptionHint(context, clause);
            switch (context.Token)
            {
                case Operator { Character: ')' }:
                    context.MoveNextOptional();
                    return clause;
                case Operator { Character: ',' }:
                    continue;
                case Name:
                    // A word straight after a whole hint is read on, and the
                    // token after it named (`maxdop 1 recompile)` is near ')').
                    context.MoveNextRequired();
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
    }

    /// <summary>
    /// Consumes one <c>OPTION</c> hint, leaving the cursor on the token after
    /// it. A hint word real doesn't know, or one missing its second word, is
    /// Msg 102 on the hint word; <c>PARAMETERIZATION</c>, a plan-guide-only
    /// hint, is among them.
    /// </summary>
    private static void ConsumeOneOptionHint(ParserContext context, OptionClause clause)
    {
        var hint = context.Token ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        var source = hint.Source;
        if (source.Length > 40)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        Span<char> upper = stackalloc char[source.Length];
        _ = source.ToUpperInvariant(upper);
        switch (upper)
        {
            case "CONCAT":
                ExpectSecondWord(context, hint, "UNION");
                return;
            case "DISABLE":
                // DISABLE EXTERNALPUSHDOWN / SCALEOUTEXECUTION need PolyBase,
                // which real reports at the second word.
                context.MoveNextRequired();
                throw SimulatedSqlException.SyntaxErrorNear(context);
            case "EXPAND":
                ExpectSecondWord(context, hint, "VIEWS");
                return;
            case "FAST":
                clause.Fast = Repeated(clause.Fast, ReadOptionInteger(context, hint), "fast");
                context.MoveNextRequired();
                return;
            case "FORCE":
                if (!IsWord(context.GetNextRequired(), "ORDER"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                clause.ForceOrder = true;
                context.MoveNextRequired();
                return;
            case "HASH":
                clause.HashGroup |= ReadJoinSecondWord(context, hint, clause, JoinAlgorithms.Hash, alsoUnion: true, alsoGroup: true);
                return;
            case "IGNORE_NONCLUSTERED_COLUMNSTORE_INDEX":
                context.MoveNextRequired();
                return;
            case "KEEP":
            case "KEEPFIXED":
                ExpectSecondWord(context, hint, "PLAN");
                return;
            case "LABEL":
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(hint);
                switch (context.GetNextRequired())
                {
                    case Literal { Value: { IsNull: false } label } when SqlType.IsStringCategory(label.Type):
                        break;
                    case Literal or Numeric:
                        throw SimulatedSqlException.SyntaxErrorNear(hint);
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                if (clause.Label)
                    throw SimulatedSqlException.LabelHintRepeated().PinLine(15);
                clause.Label = true;
                context.MoveNextRequired();
                return;
            case "LOOP":
                _ = ReadJoinSecondWord(context, hint, clause, JoinAlgorithms.Loop, alsoUnion: false, alsoGroup: false);
                return;
            case "MAXDOP":
                var maxDop = ReadOptionInteger(context, hint);
                if (maxDop > 32_767)
                    throw SimulatedSqlException.IndexMaxDopOutOfRange(context.Token.Source.ToString());
                clause.MaxDop = Repeated(clause.MaxDop, maxDop, "maxdop");
                context.MoveNextRequired();
                return;
            case "MAXRECURSION":
                var limit = ReadOptionInteger(context, hint);
                if (limit > 32_767)
                    throw SimulatedSqlException.MaxRecursionOutOfRange(context.Token.Source.ToString());
                clause.MaxRecursion = Repeated(clause.MaxRecursion, limit, "maxrecursion");
                if (context.CteBindings is { } bindings)
                {
                    foreach (var binding in bindings.Values)
                        binding.MaxRecursion = limit;
                }
                context.MoveNextRequired();
                return;
            case "MAX_GRANT_PERCENT":
                clause.MaxGrantPercent = ReadGrantPercent(context, hint, clause.MaxGrantPercent, "max_grant_percent");
                return;
            case "MERGE":
                _ = ReadJoinSecondWord(context, hint, clause, JoinAlgorithms.Merge, alsoUnion: true, alsoGroup: false);
                return;
            case "MIN_GRANT_PERCENT":
                clause.MinGrantPercent = ReadGrantPercent(context, hint, clause.MinGrantPercent, "min_grant_percent");
                return;
            case "NO_PERFORMANCE_SPOOL":
                context.MoveNextRequired();
                return;
            case "OPTIMIZE":
                ConsumeOptimizeFor(context, clause);
                return;
            case "ORDER":
                ExpectSecondWord(context, hint, "GROUP");
                return;
            case "QUERYTRACEON":
                _ = ReadOptionInteger(context, hint);
                context.MoveNextRequired();
                return;
            case "RECOMPILE":
                context.Batch.CurrentStatement.Recompiles = true;
                context.MoveNextRequired();
                return;
            case "REMOTE":
                if (IsWord(context.GetNextRequired(), "JOIN"))
                    throw SimulatedSqlException.NotARecognizedJoinOption(hint.Source.ToString());
                throw SimulatedSqlException.SyntaxErrorNear(hint);
            case "ROBUST":
                ExpectSecondWord(context, hint, "PLAN");
                return;
            case "TABLE":
                ConsumeTableHintClause(context, hint, clause);
                return;
            case "USE":
                var checkpoint = context.SaveCheckpoint();
                var second = context.GetNextRequired();
                if (IsWord(second, "HINT"))
                {
                    context.RestoreCheckpoint(checkpoint);
                    ConsumeUseHint(context);
                    return;
                }
                if (!IsWord(second, "PLAN"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                ConsumeUsePlan(context);
                return;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    private static bool IsWord(Token? token, string word) =>
        token is not null && token.Source.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Requires the hint's second word, Msg 102 on the hint word otherwise, and steps past it.</summary>
    private static void ExpectSecondWord(ParserContext context, Token hint, string word)
    {
        if (!IsWord(context.GetNextRequired(), word))
            throw SimulatedSqlException.SyntaxErrorNear(hint);
        context.MoveNextRequired();
    }

    /// <summary>The <c>JOIN</c>, <c>UNION</c> or <c>GROUP</c> after a hint's algorithm word, answering whether it was <c>GROUP</c>.</summary>
    private static bool ReadJoinSecondWord(ParserContext context, Token hint, OptionClause clause, JoinAlgorithms algorithm, bool alsoUnion, bool alsoGroup)
    {
        var second = context.GetNextRequired();
        var group = alsoGroup && IsWord(second, "GROUP");
        if (IsWord(second, "JOIN"))
            clause.Joins |= algorithm;
        else if (!(alsoUnion && IsWord(second, "UNION")) && !group)
            throw SimulatedSqlException.SyntaxErrorNear(hint);
        context.MoveNextRequired();
        return group;
    }

    /// <summary>
    /// The integer argument a hint takes, left under the cursor. A literal of
    /// another kind — a string, a decimal, an integer past <c>int</c> — is
    /// Msg 102 on the hint word; anything else (a sign, a variable, a binary
    /// literal) Msg 102 on itself.
    /// </summary>
    private static int ReadOptionInteger(ParserContext context, Token hint)
    {
        return context.GetNextRequired() switch
        {
            Numeric { Value: { IsNull: false } value } when value.Type == SqlType.Int32 => value.AsInt32,
            Literal { Value: var binary } when !SqlType.IsStringCategory(binary.Type) => throw SimulatedSqlException.SyntaxErrorNear(context),
            Numeric or Literal or Operator { Character: ')' or ',' } => throw SimulatedSqlException.SyntaxErrorNear(hint),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    /// <summary>A repeated hint must repeat its value (Msg 1042 otherwise).</summary>
    private static int Repeated(int? earlier, int value, string hintName) =>
        earlier is { } previous && previous != value ? throw SimulatedSqlException.ConflictingOptimizerHints(hintName) : value;

    /// <summary>
    /// <c>MIN_GRANT_PERCENT</c> / <c>MAX_GRANT_PERCENT = n</c>: an integer or
    /// decimal literal from 0 to 100, real answering a value outside it, or a
    /// second different one, with Msg 1042.
    /// </summary>
    private static decimal ReadGrantPercent(ParserContext context, Token hint, decimal? earlier, string hintName)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(hint);
        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } value } || value.Type.Category == SqlTypeCategory.Approximate)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var percent = value.CoerceTo(SqlType.GetDecimal(38, 10)).AsDecimal;
        if (percent is < 0 or > 100 || (earlier is { } previous && previous != percent))
            throw SimulatedSqlException.ConflictingOptimizerHints(hintName);
        context.MoveNextRequired();
        return percent;
    }

    /// <summary>
    /// <c>OPTIMIZE FOR UNKNOWN</c> or <c>OPTIMIZE FOR (@v {UNKNOWN | = literal} [, …])</c>.
    /// Each variable must be declared (Msg 137), named once across the clauses
    /// (Msg 4131), and given a literal (Msg 320) its type takes — Msg 206 for
    /// a type it can't convert from, Msg 4132 for a value that fails to
    /// convert (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    private static void ConsumeOptimizeFor(ParserContext context, OptionClause clause)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var next = context.GetNextRequired();
        if (IsWord(next, "UNKNOWN"))
        {
            context.MoveNextRequired();
            return;
        }
        if (next is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        while (true)
        {
            if (context.GetNextRequired() is not AtPrefixedString variable)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var name = variable.Value;
            if (!context.Batch.Variables.TryGetValue(name, out var slot))
                throw SimulatedSqlException.MustDeclareScalarVariable(name);
            if (!(clause.OptimizedVariables ??= new(BatchContext.VariableNameComparer)).Add(name))
                throw SimulatedSqlException.OptimizeForVariableRepeated("@" + name);
            var after = context.GetNextRequired();
            if (IsWord(after, "UNKNOWN"))
            {
                context.MoveNextRequired();
            }
            else if (after is Operator { Character: '=' })
            {
                var negative = context.GetNextRequired() is Operator { Character: '-' };
                if (negative)
                    context.MoveNextRequired();
                // A minus takes a number or a currency literal; before a
                // string, a binary literal or NULL it is a syntax error.
                var value = context.Token switch
                {
                    Numeric { Value: var number } => number,
                    Literal { Value: var literal } when !negative || literal.Type is MoneySqlType => literal,
                    Literal or ReservedKeyword { Keyword: Keyword.Null } when negative => throw SimulatedSqlException.SyntaxErrorNear(context),
                    ReservedKeyword { Keyword: Keyword.Null } => SqlValue.Null(slot.DeclaredType),
                    _ => throw SimulatedSqlException.OptimizeForValueNotLiteral("@" + name),
                };
                if (!value.IsNull)
                    CheckOptimizeForValue(negative ? Negate(value) : value, slot.DeclaredType, "@" + name);
                context.MoveNextRequired();
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            switch (context.Token)
            {
                case Operator { Character: ')' }:
                    context.MoveNextRequired();
                    return;
                case Operator { Character: ',' }:
                    continue;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
    }

    private static SqlValue Negate(SqlValue value) =>
        value.Type == SqlType.Int32 ? SqlValue.FromInt32(-value.AsInt32) : SqlValue.FromDouble(-value.CoerceTo(SqlType.Float).AsDouble);

    private static void CheckOptimizeForValue(SqlValue value, SqlType declared, string variable)
    {
        try
        {
            _ = value.CoerceTo(declared);
        }
        catch (SimulatedSqlException error) when (error.Number is 206 or 529)
        {
            throw SimulatedSqlException.OperandTypeClash(value.Type, declared);
        }
        catch (Exception error) when (error is SimulatedSqlException or OverflowException)
        {
            throw SimulatedSqlException.OptimizeForValueNotConvertible(variable);
        }
    }

    /// <summary>
    /// <c>TABLE HINT (object [, hint …])</c>: the object as written and the
    /// hints, checked against the statement's sources by
    /// <see cref="SettleStatementHints"/>; a second clause for one object is
    /// Msg 8720.
    /// </summary>
    private static void ConsumeTableHintClause(ParserContext context, Token hint, OptionClause clause)
    {
        if (!IsWord(context.GetNextRequired(), "HINT") || context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(hint);
        var written = new System.Text.StringBuilder();
        while (true)
        {
            if (context.GetNextRequired() is not Name part)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            _ = written.Append(part.Value);
            if (context.GetNextRequired() is not Operator { Character: '.' })
                break;
            _ = written.Append('.');
        }
        var info = default(TableHintInfo);
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            ConsumeOneTableHint(context, ref info, legacyForm: false);
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        ValidateHintCombinations(info);
        var objectName = written.ToString();
        clause.TableHints ??= [];
        if (clause.TableHints.Exists(earlier => context.Batch.CurrentDatabase.Collation.Equals(earlier.Written, objectName)))
            throw SimulatedSqlException.TableHintClauseRepeated(objectName);
        clause.TableHints.Add((objectName, info));
        context.MoveNextRequired();
    }

    /// <summary>
    /// <c>USE PLAN N'xml'</c>: an empty plan is Msg 8695, and a document whose
    /// root isn't a <c>ShowPlanXML</c> element fails the showplan schema with
    /// Msg 6913 (probed 2026-10-05 against SQL Server 2025). Cursor on entry:
    /// <c>PLAN</c>.
    /// </summary>
    private static void ConsumeUsePlan(ParserContext context)
    {
        if (context.GetNextRequired() is not Literal { Value: { IsNull: false } plan } || !SqlType.IsStringCategory(plan.Type))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var text = plan.AsString;
        if (string.IsNullOrWhiteSpace(text))
            throw SimulatedSqlException.MalformedUsePlan();
        string? root = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            if (reader.MoveToContent() == XmlNodeType.Element)
                root = reader.LocalName;
        }
        catch (XmlException)
        {
        }
        if (root is not (null or "ShowPlanXML"))
            throw SimulatedSqlException.UsePlanFailsShowplanSchema(root);
        context.MoveNextRequired();
    }

    /// <summary>
    /// The <c>OPTION</c> clause an <c>UPDATE</c>, <c>DELETE</c>,
    /// <c>INSERT … VALUES</c> or <c>MERGE</c> may close with, after its
    /// <c>WHERE</c>, its row list or its last <c>WHEN</c> clause, and the
    /// statement's hint checks, which run whether or not one was written.
    /// </summary>
    internal static void ParseOptionalDmlOptionClause(ParserContext context)
    {
        OptionClause? clause = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Option })
        {
            clause = ParseOptionClause(context);
            context.SimpleParameterizationBlocked = true;
        }
        SettleStatementHints(context, clause);
    }

    /// <summary>
    /// The checks a statement's hints make once all of it has parsed, in the
    /// order real raises them (probed 2026-10-05 against SQL Server 2025):
    /// an inline join hint the <c>OPTION</c> join hints don't include is
    /// Msg 1042 (Msg 1071 for an inline <c>REMOTE</c>); a <c>TABLE HINT</c>
    /// must name a source as the query exposes it (Msg 8723), carry no
    /// semantic hint the source's own <c>WITH</c> clause lacks (Msg 8722), and
    /// pass the index-hint checks; a join whose hinted algorithms can't be
    /// built from its predicates is Msg 8622; and an inline join hint sends
    /// Msg 8625 unless <c>FORCE ORDER</c> fixes the order itself.
    /// </summary>
    internal static void SettleStatementHints(ParserContext context, OptionClause? clause)
    {
        var sites = context.JoinHintSites;
        context.JoinHintSites = null;
        var groupsClrAggregate = context.GroupsClrAggregate;
        context.GroupsClrAggregate = false;
        // A view body binding inside the statement settles its own query
        // first; what its holders keep is the enclosing statement's to judge.
        if (context.BodySeekShapeHolders is { } holders && !context.Batch.BindsModuleDefinition)
        {
            foreach (var holder in holders)
                holder.BodySeekShape = null;
            context.BodySeekShapeHolders = null;
        }
        var enforced = context.JoinOrderEnforced;
        context.JoinOrderEnforced = false;
        if (Simulation.QueryStoreHintFor(context.Batch) is { } storeHint)
            clause = ApplyQueryStoreHint(context, clause, storeHint, sites);

        if (clause is { Joins: not JoinAlgorithms.None } && sites is not null)
        {
            foreach (var site in sites)
            {
                foreach (var join in site.Joins)
                {
                    if (join.Remote)
                        throw SimulatedSqlException.JoinAlgorithmWithRemoteJoin();
                    if (join.Algorithm != JoinAlgorithms.None && (clause.Joins & join.Algorithm) == 0)
                        throw SimulatedSqlException.ConflictingOptimizerHints("JOIN", @class: 16);
                }
            }
        }

        if (clause?.TableHints is { } tableHints)
            ValidateOptionTableHints(context, tableHints, sites);

        // A CLR aggregate under GROUP BY can't be hashed — a scalar or a
        // windowed one can (probed 2026-10-07 against SQL Server 2025).
        if (clause is { HashGroup: true } && groupsClrAggregate && OptimizerChecksRun(context.Batch))
            throw SimulatedSqlException.ForceSeekPlanInfeasible();

        if (sites is not null && OptimizerChecksRun(context.Batch))
        {
            foreach (var site in sites)
                ValidateJoinAlgorithms(site, clause?.Joins ?? JoinAlgorithms.None);
        }

        // A TABLE HINT's forced-seek check records its site again.
        context.JoinHintSites = null;
        if (enforced && clause is not { ForceOrder: true })
            SendJoinOrderEnforced(context.Batch);
    }

    /// <summary>
    /// The statement's <c>OPTION</c> clause with the Query Store hint for the
    /// query it is applied over it, as real compiles the statement (probed
    /// 2026-10-06 against SQL Server 2025): the hint's <c>MAXRECURSION</c>
    /// replaces the statement's own, its join hints and <c>FORCE ORDER</c>
    /// steer the plan, and its <c>RECOMPILE</c> compiles the statement every
    /// time it runs. Join hints that leave no plan don't fail the statement:
    /// it compiles without the hint, which records the failure.
    /// </summary>
    private static OptionClause? ApplyQueryStoreHint(ParserContext context, OptionClause? clause, QueryStoreHint hint, List<JoinHintSite>? sites)
    {
        var hinted = hint.Clause;
        var joins = hinted.Joins != JoinAlgorithms.None ? hinted.Joins : clause?.Joins ?? JoinAlgorithms.None;
        if (hinted.Joins != JoinAlgorithms.None && sites is not null && OptimizerChecksRun(context.Batch))
        {
            try
            {
                foreach (var site in sites)
                    ValidateJoinAlgorithms(site, joins);
            }
            catch (SimulatedSqlException refused) when (refused.Number == 8622)
            {
                Simulation.NoteQueryStoreHintFailure(context.Batch, hint, refused.Number);
                return clause;
            }
        }

        if (hinted.MaxRecursion is { } limit && context.CteBindings is { } bindings)
        {
            foreach (var binding in bindings.Values)
                binding.MaxRecursion = limit;
        }
        if (hint.Recompiles)
            context.Batch.CurrentStatement.Recompiles = true;
        var merged = clause ?? new OptionClause();
        merged.Joins = joins;
        merged.ForceOrder |= hinted.ForceOrder;
        merged.MaxRecursion = hinted.MaxRecursion ?? merged.MaxRecursion;
        return merged;
    }

    /// <summary>
    /// Sends Msg 8625 for the statement <paramref name="batch"/> is settling,
    /// as real does whenever it compiles one (probed 2026-10-06 against SQL
    /// Server 2025): a batch's compile sends it ahead of everything the batch
    /// runs — untaken branches and a <c>SET NOEXEC ON</c> batch included —
    /// for each hinted statement it compiles, and the statement sends nothing
    /// as it runs; one the compile deferred, one carrying <c>OPTION
    /// (RECOMPILE)</c> and one reading a table variable compile as they run
    /// and send it then; a batch or module body running on a plan compiled
    /// before sends it only for those last two. A view or function body
    /// inlined into a statement hands the warning to that statement, which
    /// sends it on its own line, and a module body binding at <c>CREATE</c>
    /// sends none.
    /// </summary>
    internal static void SendJoinOrderEnforced(BatchContext batch)
    {
        if (batch.Connection.InlinedBodyDepth > 0)
        {
            batch.Parser.JoinOrderEnforced = true;
            return;
        }
        if ((batch.CreateTimeBinding && !batch.CompilingForRun) || batch.DefiningModuleSchema is not null)
            return;
        var statement = batch.CurrentStatement;
        var compilesAsItRuns = statement.Recompiles || (statement.ReadsTableVariable && batch.CurrentDatabase.CompatibilityLevel >= CompatibilityLevel.Sql150);
        if (batch.CompilingForRun)
        {
            if (!statement.BindsDeferredSource && !compilesAsItRuns)
            {
                (batch.CompileMessages ??= []).Add(batch.InfoMessage(@class: 0, state: 0, SimulatedSqlException.JoinOrderEnforcedMessageNumber, SimulatedSqlException.JoinOrderEnforcedMessage));
                _ = (batch.JoinOrderWarnedStatements ??= []).Add(statement.StartIndex);
            }
            return;
        }
        if (batch.IsSkipping)
            return;
        if (batch.JoinOrderWarnedStatements is { } warned ? !warned.Contains(statement.StartIndex) : compilesAsItRuns)
            batch.AppendInfoError(@class: 0, state: 0, SimulatedSqlException.JoinOrderEnforcedMessageNumber, SimulatedSqlException.JoinOrderEnforcedMessage);
    }

    /// <summary>
    /// Whether the optimizer's refusals (Msg 8622) are raised here: while a
    /// batch compiles or runs, but not while a module body binds at
    /// <c>CREATE</c>, nor after the batch has met a binder error.
    /// </summary>
    private static bool OptimizerChecksRun(BatchContext batch) =>
        !(batch.IsSkipping ? !batch.CompilingForRun : batch.CreateTimeBinding)
        && batch.CreateTimeBindErrors is not { Count: > 0 };

    /// <summary>
    /// Records a query specification for <see cref="SettleStatementHints"/>.
    /// </summary>
    /// <remarks>
    /// Nothing is recorded for a block whose statement can't need it — no
    /// inline join hint, no forced access path, a command text that never
    /// spells <c>OPTION</c> and no Query Store hint that could apply — since
    /// every query block parsed passes here.
    /// </remarks>
    private static void RecordJoinHintSite(ParserContext context, FromSource[] sources, JoinSpec[] joins, List<BooleanExpression> filters)
    {
        if (!context.CommandMentionsOption
            && context.Batch.CurrentDatabase.QueryStoreData.Hints.Count == 0
            && !Array.Exists(joins, static join => join.Algorithm != JoinAlgorithms.None)
            && !Array.Exists(sources, static source => source.ForcedAccessPath is not null))
        {
            return;
        }
        (context.JoinHintSites ??= []).Add(new JoinHintSite(sources, joins, [.. filters]));
    }

    private static void ValidateOptionTableHints(ParserContext context, List<(string Written, TableHintInfo Hints)> tableHints, List<JoinHintSite>? sites)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        foreach (var (written, hints) in tableHints)
        {
            JoinHintSite? owner = null;
            var index = -1;
            foreach (var site in sites ?? [])
            {
                // An alias is the exposed name even spelled as the table's own.
                index = Array.FindIndex(site.Sources, source => collation.Equals(source.UnaliasedName is not null ? source.WrittenObjectName : source.Qualifier, written));
                if (index >= 0)
                {
                    owner = site;
                    break;
                }
            }
            if (owner is null)
                throw SimulatedSqlException.TableHintObjectNotInQuery(written);

            var source = owner.Sources[index];
            if (hints.FirstSemanticHint is { } semantic && source.WrittenHints?.FirstSemanticHint is null)
                throw SimulatedSqlException.SemanticTableHintMismatch(semantic, written).PinLine(12);
            if (source.BackingTable is not { } table)
                continue;
            ValidateIndexHintArguments(collation, hints, table, written);
            ValidateForceSeekColumns(collation, hints, table);
            if (hints.ForceSeek || hints.IndexArguments is { Count: > 1 } || (hints.ForceScan && hints.IndexArguments is { Count: > 0 }))
            {
                source.ForcedAccessPath = hints;
                ValidateForcedSeeks(context, owner.Sources, owner.Joins, owner.Filters, projections: null, soleSubqueryColumn: null);
            }
        }
    }

    /// <summary>
    /// Msg 8622 for a join whose allowed algorithms — its inline hint's, else
    /// the <c>OPTION</c> clause's — can't be built: a hash join needs an
    /// equality between the two sides among the join's <c>ON</c> conjuncts or
    /// the query's filters, a merge join too unless it is a full outer join,
    /// and neither seeks a <c>FORCESEEK</c> source on its inner side; a nested
    /// loop builds anything. <c>APPLY</c>, whose decorrelation decides, is
    /// left alone.
    /// </summary>
    private static void ValidateJoinAlgorithms(JoinHintSite site, JoinAlgorithms optionJoins)
    {
        List<BooleanExpression>? filterConjuncts = null;
        for (var level = 1; level <= site.Joins.Length && level < site.Sources.Length; level++)
        {
            var join = site.Joins[level - 1];
            if (join.Kind is JoinKind.CrossApply or JoinKind.OuterApply)
                continue;
            var allowed = join.Algorithm != JoinAlgorithms.None ? join.Algorithm : optionJoins;
            if (allowed == JoinAlgorithms.None || (allowed & JoinAlgorithms.Loop) != 0)
                continue;

            if (filterConjuncts is null)
            {
                filterConjuncts = [];
                foreach (var filter in site.Filters)
                    filter.CollectConjuncts(filterConjuncts);
            }
            var right = level + join.GroupCount;
            var equi = false;
            if (join.OnPredicate is { } on)
            {
                var onConjuncts = new List<BooleanExpression>();
                on.CollectConjuncts(onConjuncts);
                equi = onConjuncts.Exists(conjunct => IsCrossSideEquality(conjunct, site.Sources, level, right));
            }
            equi = equi || filterConjuncts.Exists(conjunct => IsCrossSideEquality(conjunct, site.Sources, level, right));
            var seeksInner = site.Sources[level].ForcedAccessPath is { ForceSeek: true };
            var feasible = !seeksInner
                && (((allowed & JoinAlgorithms.Hash) != 0 && equi)
                    || ((allowed & JoinAlgorithms.Merge) != 0 && (equi || join.Kind == JoinKind.Full)));
            if (!feasible)
                throw SimulatedSqlException.ForceSeekPlanInfeasible();
        }
    }

    /// <summary>
    /// Whether <paramref name="conjunct"/> equates an expression over the
    /// sources left of <paramref name="level"/> alone with one over the
    /// sources in <c>[level, rightEnd)</c> alone, each reading some column.
    /// </summary>
    private static bool IsCrossSideEquality(BooleanExpression conjunct, FromSource[] sources, int level, int rightEnd)
    {
        if (!conjunct.TryGetEqualityOperands(out var a, out var b))
            return false;
        var sideA = OperandSide(a, sources, level, rightEnd);
        var sideB = OperandSide(b, sources, level, rightEnd);
        return (sideA, sideB) is (-1, 1) or (1, -1);
    }

    /// <summary>-1 for an operand reading the left side alone, 1 for the right alone, 0 otherwise.</summary>
    private static int OperandSide(Expression operand, FromSource[] sources, int level, int rightEnd)
    {
        var left = false;
        var right = false;
        var other = false;
        operand.VisitColumnReferences(name =>
        {
            var (source, _) = FindSourceColumn(sources, name);
            if (source < 0 || source >= rightEnd)
                other = true;
            else if (source < level)
                left = true;
            else
                right = true;
        });
        return other || left == right ? 0 : left ? -1 : 1;
    }
}
