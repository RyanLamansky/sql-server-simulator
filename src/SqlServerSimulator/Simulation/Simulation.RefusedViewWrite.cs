using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    // Real binds a write through a view it can't write through as it binds any
    // other, refusing the view only once the names have bound (probed
    // 2026-10-19 against SQL Server 2025): a DELETE reports its FROM and WHERE
    // names, then Msg 4403 / 4405; an UPDATE its FROM and WHERE names alone,
    // the refusal only when they bound; an INSERT its source's names and its
    // column list's, then the refusal — Msg 4406 whenever the source failed,
    // whatever the view's shape. Neither reads its OUTPUT clause. A statement
    // whose binder report is being gathered reads on past the refusal here.

    /// <summary>
    /// Records <paramref name="refusal"/>, a <c>DELETE</c>'s or
    /// <c>UPDATE</c>'s of the view it targets, in the binder report being
    /// gathered and reads the rest of the statement against the view's
    /// columns, entered on the token after the view's name — false, having
    /// read nothing, when no report is being gathered.
    /// </summary>
    private static bool ReadPastRefusedViewWrite(ParserContext context, MultiPartName name, View view, SimulatedSqlException refusal, bool isUpdate)
    {
        if (context.Batch.BindErrors is not { } report
            || !(isUpdate ? report.RecordViewRefusal(refusal, BindClause.SetTarget, unlessPreceded: true) : report.RecordViewRefusal(refusal)))
        {
            return false;
        }
        _ = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false);
        if (isUpdate)
            _ = PeekSetTargets(context);
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            SkipOutputClause(context);
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.From }:
                var from = Selection.PreParseMutationFrom(context, fromCursor: true) ?? throw refusal;
                FromSource[] sources = [.. from.Sources];
                _ = ParseJoinedViewTargetWhere(context, from.After, sources, [.. from.Joins], Selection.ColumnTypeResolverFor(sources));
                break;
            case ReservedKeyword { Keyword: Keyword.Where }:
                report.EnterClause(context.Token, BindClause.Where);
                context.MoveNextRequired();
                if (!SkipCurrentOf(context))
                    _ = Selection.ParseAndBindPredicate(context, RefusedViewColumnTypes(context.CurrentDatabase, name, view));
                break;
        }
        SkipOptionClause(context);
        RejectStrayToken(context);
        return true;
    }

    /// <summary>
    /// Reads an <c>INSERT</c> through a view it can't write through for the
    /// binder report being gathered, entered on the token after the view's
    /// name: its column list against the view's columns, then its source, then
    /// the refusal — Msg 4406 when the source failed or the list names a
    /// derived column, <paramref name="refusal"/> or the view's own refusal
    /// else — none at all when <paramref name="namesOnly"/>, for a view whose
    /// refusals real reports only past names that bound — and none through a
    /// CTE whose source failed. Null when no report is being gathered.
    /// </summary>
    private static SimulatedNonQuery? ReadPastRefusedViewInsert(ParserContext context, View view, MultiPartName name, SimulatedSqlException? refusal = null, bool namesOnly = false)
    {
        if (context.Batch.BindErrors is not { } report || !report.Covers(context.Token))
            return null;
        report.OpenScope(context.Token);
        var collation = context.CurrentDatabase.Collation;
        var derived = view.DerivedOutputColumns;
        var listed = -1;
        var anyDerived = false;
        if (context.Token is Operator { Character: '(' })
        {
            listed = 0;
            while (true)
            {
                if (context.GetNextRequired() is not StringToken column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                listed++;
                var ordinal = IndexOfViewOutputColumn(collation, view, column.Value);
                if (ordinal < 0)
                    report.Record(SimulatedSqlException.InvalidColumnName(column.Value), column.StartIndex, BindClause.InsertColumns);
                else
                    anyDerived |= derived?[ordinal] == true;
                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }
        else
        {
            anyDerived = derived is not null && Array.IndexOf(derived, true) >= 0;
        }
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            SkipOutputClause(context);

        var recordedBefore = report.Count;
        report.EnterClause(context.Token, BindClause.InsertSource);
        if (ParseInsertSourceUnbound(context) is { } source && listed >= 0 && source.Schema.Length != listed)
        {
            throw source.Schema.Length < listed
                ? SimulatedSqlException.InsertSelectListFewerThanInsertList()
                : SimulatedSqlException.InsertSelectListMoreThanInsertList();
        }
        var written = name.ToString();
        var reported = report.Count > recordedBefore
            ? view.UnstoredBody is null ? SimulatedSqlException.ViewDmlTouchesDerivedField(written) : null
            : namesOnly ? null
            : anyDerived ? SimulatedSqlException.ViewDmlTouchesDerivedField(view.UnionOwnerName ?? written) : refusal ?? NonUpdatableViewError(view, written);
        if (reported is not null)
            _ = report.RecordViewRefusal(reported);
        SkipOptionClause(context);
        RejectStrayToken(context);
        return new SimulatedNonQuery(0);
    }

    /// <summary>
    /// Records the Msg 4406 real adds to an <c>INSERT</c> through a view —
    /// not a CTE or derived table — once its source, read from
    /// <paramref name="sourceStart"/> to where the cursor sits, has failed to
    /// bind (probed 2026-10-19 against SQL Server 2025: <c>INSERT vw (a)
    /// VALUES (x1)</c> is Msg 207 then Msg 4406 through a view it could write
    /// through).
    /// </summary>
    private static void NoteViewInsertSource(ParserContext context, BindErrorReport? report, Token? sourceStart, View? view, MultiPartName name)
    {
        if (report is not null && view is { UnstoredBody: null } && report.Covers(sourceStart))
            report.RecordViewRefusalAfterFailureWithin(SimulatedSqlException.ViewDmlTouchesDerivedField(name.ToString()), sourceStart!.StartIndex, report.Covers(context.Token) ? context.Token!.StartIndex : report.Command.Length);
    }

    /// <summary>The column types a refused view's <c>WHERE</c> binds against: its own output columns, qualified by nothing but the target.</summary>
    private static Func<MultiPartName, SqlType> RefusedViewColumnTypes(Database database, MultiPartName target, View view) =>
        name =>
        {
            if (!Selection.QualifierIsDmlTarget(database, target, name))
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
            var ordinal = IndexOfViewOutputColumn(database.Collation, view, name.Leaf);
            return ordinal >= 0 ? view.OutputColumns[ordinal].Type : throw SimulatedSqlException.InvalidColumnName(name);
        };
}
