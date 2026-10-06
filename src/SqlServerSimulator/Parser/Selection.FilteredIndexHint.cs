using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Whether an <c>INDEX</c> hint on <paramref name="source"/> names a
    /// filtered index the query's predicates don't confine it to — a plan
    /// real can't build, Msg 8622 (probed 2026-10-06 against SQL Server
    /// 2025). Real proves the filter from the query's top-level conjuncts over
    /// literals: <c>b &gt;= 6</c> or <c>b = 6</c> implies <c>b &gt; 5</c> over
    /// an integer column, a comparison implies <c>IS NOT NULL</c>, an
    /// <c>IN</c> list implies a filter listing all its values, and a
    /// variable or an <c>OR</c> implies nothing. A hint naming several indexes
    /// is planned whatever their filters.
    /// </summary>
    private static bool HintsUnimpliedFilteredIndex(BatchContext batch, FromSource source, int sourceIndex, FromSource[] sources, HeapTable table, List<BooleanExpression> conjuncts)
    {
        // Naming several indexes lets real intersect them, which it plans
        // whatever their filters (probed 2026-10-06).
        if (source.WrittenHints?.IndexArguments is not [var argument])
            return false;
        var index = table.Indexes.Find(candidate => argument.Name is { } name
            ? batch.CurrentDatabase.Collation.Equals(candidate.Name, name)
            : candidate.IndexId == argument.Id);
        return index?.Filter is { } filter && !FilterImplied(batch, filter, source, sourceIndex, sources, table, conjuncts);
    }

    private static bool FilterImplied(BatchContext batch, BooleanExpression filter, FromSource source, int sourceIndex, FromSource[] sources, HeapTable table, List<BooleanExpression> conjuncts)
    {
        var required = new List<BooleanExpression>();
        filter.CollectConjuncts(required);
        foreach (var condition in required)
        {
            // A filter shape the walk can't read is taken as proven, the
            // direction that never refuses a plan real builds.
            if (!ValueDomain.TryRead(condition, batch, IsFilterColumn, out var column, out var needed))
                continue;
            var domain = new ValueDomain();
            foreach (var conjunct in conjuncts)
            {
                if (ValueDomain.TryRead(conjunct, batch, expression => QueryColumn(expression) is { } c && ReferenceEquals(c, column) ? c : null, out _, out var offered))
                    domain.Intersect(offered, column.Type);
            }
            if (!domain.Implies(needed, column.Type))
                return false;
        }
        return true;

        HeapColumn? IsFilterColumn(Expression expression) =>
            expression is Reference reference && Array.FindIndex(table.Columns, c => batch.CurrentDatabase.Collation.Equals(c.Name, reference.ReferencedName.Leaf)) is >= 0 and var at
                ? table.Columns[at]
                : null;

        HeapColumn? QueryColumn(Expression expression)
        {
            if (expression is not Reference reference || TryResolveSourceColumn(sources, reference.ReferencedName) is not { } id || id.Source != sourceIndex)
                return null;
            var column = source.Columns[id.Column];
            return Array.Find(table.Columns, c => ReferenceEquals(c, column) || batch.CurrentDatabase.Collation.Equals(c.Name, column.Name));
        }
    }

    /// <summary>
    /// The values one predicate admits for a column — a finite set, an
    /// interval, or only <c>NULL</c> — and their intersection across a
    /// query's conjuncts.
    /// </summary>
    private sealed class ValueDomain
    {
        private List<SqlValue>? set;
        private SqlValue? low;
        private bool lowInclusive;
        private SqlValue? high;
        private bool highInclusive;
        private SqlValue? excluded;
        private bool notNull;
        private bool onlyNull;

        /// <summary>
        /// Reads <paramref name="predicate"/> as a constraint on one column
        /// <paramref name="columnOf"/> recognizes, against constants.
        /// </summary>
        public static bool TryRead(BooleanExpression predicate, BatchContext batch, Func<Expression, HeapColumn?> columnOf, out HeapColumn column, out ValueDomain domain)
        {
            column = null!;
            domain = new ValueDomain();
            if (predicate.TryGetNullTest(out var subject, out var isNotNull))
            {
                if (columnOf(subject) is not { } tested)
                    return false;
                column = tested;
                _ = isNotNull ? domain.notNull = true : domain.onlyNull = true;
                return true;
            }
            if (predicate.TryGetEqualityOperands(out var left, out var right))
            {
                if (!ColumnAgainstConstant(left, right, out column, out var value) && !ColumnAgainstConstant(right, left, out column, out value))
                    return false;
                domain.set = [value];
                domain.notNull = true;
                return true;
            }
            if (predicate.TryGetEqualityFamily(out var pairs))
            {
                var values = new List<SqlValue>(pairs.Count);
                HeapColumn? shared = null;
                foreach (var (pairLeft, pairRight) in pairs)
                {
                    if (!ColumnAgainstConstant(pairLeft, pairRight, out var pairColumn, out var value) || (shared is not null && !ReferenceEquals(shared, pairColumn)))
                        return false;
                    shared = pairColumn;
                    values.Add(value);
                }
                if (shared is null)
                    return false;
                column = shared;
                domain.set = values;
                domain.notNull = true;
                return true;
            }
            if (TryGetRange(predicate, out left, out var op, out right))
            {
                var flipped = false;
                if (!ColumnAgainstConstant(left, right, out column, out var bound))
                {
                    if (!ColumnAgainstConstant(right, left, out column, out bound))
                        return false;
                    flipped = true;
                }
                var effective = !flipped ? op : op switch
                {
                    RangeComparison.Greater => RangeComparison.Less,
                    RangeComparison.GreaterOrEqual => RangeComparison.LessOrEqual,
                    RangeComparison.Less => RangeComparison.Greater,
                    _ => RangeComparison.GreaterOrEqual,
                };
                _ = effective is RangeComparison.Greater or RangeComparison.GreaterOrEqual
                    ? (domain.low, domain.lowInclusive) = (bound, effective == RangeComparison.GreaterOrEqual)
                    : (domain.high, domain.highInclusive) = (bound, effective == RangeComparison.LessOrEqual);
                domain.notNull = true;
                return true;
            }
            if (predicate.TryGetBetweenOperands(out var tested2, out var lower, out var upper))
            {
                if (columnOf(tested2) is not { } between || !Constant(lower, between, out var lowValue) || !Constant(upper, between, out var highValue))
                    return false;
                column = between;
                (domain.low, domain.lowInclusive, domain.high, domain.highInclusive, domain.notNull) = (lowValue, true, highValue, true, true);
                return true;
            }
            if (predicate.TryGetComplement(out var complement) && complement.TryGetEqualityOperands(out left, out right)
                && (ColumnAgainstConstant(left, right, out column, out var excludedValue) || ColumnAgainstConstant(right, left, out column, out excludedValue)))
            {
                domain.excluded = excludedValue;
                domain.notNull = true;
                return true;
            }
            return false;

            // NOT (b <= 5) reads as b > 5.
            static bool TryGetRange(BooleanExpression predicate, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Expression? left, out RangeComparison op, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Expression? right)
            {
                if (predicate.TryGetRangeOperands(out left, out op, out right))
                    return true;
                if (!predicate.TryGetComplement(out var negated) || !negated.TryGetRangeOperands(out left, out op, out right))
                    return false;
                op = op switch
                {
                    RangeComparison.Greater => RangeComparison.LessOrEqual,
                    RangeComparison.GreaterOrEqual => RangeComparison.Less,
                    RangeComparison.Less => RangeComparison.GreaterOrEqual,
                    _ => RangeComparison.Greater,
                };
                return true;
            }

            bool ColumnAgainstConstant(Expression columnSide, Expression valueSide, out HeapColumn found, out SqlValue value)
            {
                found = columnOf(columnSide)!;
                value = default;
                return found is not null && Constant(valueSide, found, out value);
            }

            bool Constant(Expression expression, HeapColumn target, out SqlValue value)
            {
                value = default;
                if (!expression.IsWrittenConstant)
                    return false;
                try
                {
                    var raw = expression.Run(new RuntimeContext(static name => throw SimulatedSqlException.InvalidColumnName(name), batch));
                    if (raw.IsNull || (raw.Type.Category != target.Type.Category && !(IsNumeric(raw.Type) && IsNumeric(target.Type))))
                        return false;
                    value = raw.CoerceTo(target.Type);
                    return true;
                }
                catch (Exception error) when (error is SimulatedSqlException or OverflowException or InvalidCastException or NotSupportedException)
                {
                    return false;
                }
            }
        }

        private static bool IsNumeric(SqlType type) =>
            type.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate;

        /// <summary>Narrows this domain by <paramref name="other"/>'s constraint.</summary>
        public void Intersect(ValueDomain other, SqlType type)
        {
            this.notNull |= other.notNull;
            this.onlyNull |= other.onlyNull;
            if (other.set is { } values)
                this.set = this.set is null ? [.. values] : this.set.FindAll(value => values.Exists(candidate => Compare(candidate, value, type) == 0));
            if (other.low is { } low && (this.low is not { } current || Compare(low, current, type) is > 0 || (Compare(low, current, type) == 0 && !other.lowInclusive)))
                (this.low, this.lowInclusive) = (low, other.lowInclusive);
            if (other.high is { } high && (this.high is not { } currentHigh || Compare(high, currentHigh, type) is < 0 || (Compare(high, currentHigh, type) == 0 && !other.highInclusive)))
                (this.high, this.highInclusive) = (high, other.highInclusive);
            this.excluded ??= other.excluded;
        }

        /// <summary>Whether every value this domain admits satisfies <paramref name="needed"/>.</summary>
        public bool Implies(ValueDomain needed, SqlType type)
        {
            if (needed.onlyNull)
                return this.onlyNull;
            if (needed.notNull && !this.notNull)
                return false;
            if (this.set is { } values)
            {
                values = values.FindAll(this.WithinBounds);
                return values.TrueForAll(value => needed.Admits(value, type));
            }
            if (needed.set is not null)
                return false;
            if (needed.excluded is { } excludedValue)
                return (this.excluded is { } mine && Compare(mine, excludedValue, type) == 0) || !this.WithinBounds(excludedValue);
            if (needed.low is { } neededLow)
            {
                if (this.low is not { } low)
                    return false;
                var (bound, inclusive) = Inclusive(low, this.lowInclusive, up: true, type);
                var (neededBound, neededInclusive) = Inclusive(neededLow, needed.lowInclusive, up: true, type);
                var order = Compare(bound, neededBound, type);
                if (order < 0 || (order == 0 && inclusive && !neededInclusive))
                    return false;
            }
            if (needed.high is { } neededHigh)
            {
                if (this.high is not { } high)
                    return false;
                var (bound, inclusive) = Inclusive(high, this.highInclusive, up: false, type);
                var (neededBound, neededInclusive) = Inclusive(neededHigh, needed.highInclusive, up: false, type);
                var order = Compare(bound, neededBound, type);
                if (order > 0 || (order == 0 && inclusive && !neededInclusive))
                    return false;
            }
            return true;
        }

        private bool Admits(SqlValue value, SqlType type) =>
            (this.set is not { } values || values.Exists(candidate => Compare(candidate, value, type) == 0))
            && (this.excluded is not { } excludedValue || Compare(excludedValue, value, type) != 0)
            && this.WithinBounds(value);

        private bool WithinBounds(SqlValue value) =>
            (this.low is not { } low || (value.CompareTo(low) is var lowOrder && (lowOrder > 0 || (lowOrder == 0 && this.lowInclusive))))
            && (this.high is not { } high || (value.CompareTo(high) is var highOrder && (highOrder < 0 || (highOrder == 0 && this.highInclusive))));

        /// <summary>An integer bound written strictly, as the inclusive bound it means (<c>b &gt; 5</c> is <c>b &gt;= 6</c>).</summary>
        private static (SqlValue Bound, bool Inclusive) Inclusive(SqlValue bound, bool inclusive, bool up, SqlType type)
        {
            if (inclusive || type.Category != SqlTypeCategory.Integer)
                return (bound, inclusive);
            var stepped = bound.CoerceTo(SqlType.BigInt).AsInt64 + (up ? 1 : -1);
            try
            {
                return (SqlValue.FromInt64(stepped).CoerceTo(type), true);
            }
            catch (Exception error) when (error is OverflowException or SimulatedSqlException)
            {
                return (bound, inclusive);
            }
        }

        private static int Compare(SqlValue a, SqlValue b, SqlType type) => a.CoerceTo(type).CompareTo(b.CoerceTo(type));
    }
}
