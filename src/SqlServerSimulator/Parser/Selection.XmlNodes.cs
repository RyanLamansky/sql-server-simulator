using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// <c>xmlexpr.nodes('xquery')</c> rowset-returning method support. Like
/// <c>OPENJSON</c> / <c>STRING_SPLIT</c>, it's implemented as a
/// <see cref="Selection"/> factory so the FROM-source machinery (alias /
/// qualifier / lateral re-execution per outer row, CROSS / OUTER APPLY
/// composition) reuses the existing derived-table codepath. Each yielded row
/// carries one <c>xml</c> column whose value is the serialized outer XML of a
/// matched node; a downstream <c>.value()</c> / nested <c>.nodes()</c> against
/// that column re-parses the fragment (see <see cref="XmlQueryEngine"/>).
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Builds a single-column (<c>xml</c>) <see cref="Selection"/> for
    /// <c>xmlexpr.nodes(...)</c>. <paramref name="columnName"/> is the column
    /// alias from the mandatory <c>AS table(column)</c> clause.
    /// </summary>
    private static Selection FromXmlNodes(Expression target, XmlQueryExpr xquery, string columnName)
    {
        SqlType[] schema = [SqlType.Xml];
        string[] columnNames = [columnName];
        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateXmlNodesRows(target, xquery, schema, batch, outerResolver));
    }

    /// <summary>
    /// Parses an <c>xmlexpr.nodes('xquery') [AS] alias(column)</c> FROM / APPLY
    /// source. On entry the cursor is on the first token of the xml target
    /// expression; on return it sits just past the closing <c>)</c> of the
    /// column-alias list.
    /// </summary>
    private static FromSource ParseXmlNodesSource(ParserContext context)
    {
        if (Expression.Parse(context) is not XmlMethodCall { IsNodes: true } nodesCall)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var (alias, columnName) = ConsumeNodesAlias(context);
        var plan = FromXmlNodes(nodesCall.Target, nodesCall.XQuery, columnName);
        HeapColumn[] columns = [new(columnName, SqlType.Xml, maxLength: null, nullable: true)];
        return new FromSource(
            qualifier: alias,
            columnNames: [columnName],
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);
    }

    /// <summary>
    /// Consumes the mandatory <c>[AS] table_alias(column_alias)</c> clause that
    /// follows a <c>.nodes(...)</c> source. On entry the cursor is on the
    /// optional <c>AS</c> / the alias name; on return it sits on the first
    /// token past the closing <c>)</c>.
    /// </summary>
    private static (string Alias, string ColumnName) ConsumeNodesAlias(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            context.MoveNextRequired();
        if (context.Token is not Name aliasName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name columnName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return (aliasName.Value, columnName.Value);
    }

    private static IEnumerable<byte[]> EnumerateXmlNodesRows(
        Expression target,
        XmlQueryExpr xquery,
        SqlType[] schema,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var input = target.Run(new RuntimeContext(resolver, batch));
        if (input.IsNull)
            yield break;
        foreach (var nodeXml in XmlQueryEngine.EvaluateNodes(input.AsString, xquery))
            yield return RowEncoder.EncodeRow(schema, [SqlValue.FromXml(nodeXml)]);
    }
}
