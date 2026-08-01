using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>JSON_PATH_EXISTS(json, path)</c>: returns <c>1</c> when the path
/// resolves to an existing value in the JSON document, <c>0</c> otherwise.
/// NULL on either input returns NULL. It never raises: a document that isn't
/// JSON text is 0 rather than the Msg 13609 its siblings raise, and a
/// <c>strict</c> path that misses is 0 rather than Msg 13608. Result type is
/// <see cref="SqlType.Bit"/>.
/// </summary>
internal sealed class JsonPathExists : Expression
{
    private readonly Expression jsonInput;
    private readonly Expression pathInput;

    public JsonPathExists(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jv = this.jsonInput.Run(runtime);
        var pv = this.pathInput.Run(runtime);
        if (jv.IsNull || pv.IsNull)
            return SqlValue.Null(SqlType.Bit);
        var path = JsonPath.Parse(pv.AsString);

        // JSON_PATH_EXISTS is the one member of the family that never raises:
        // a document the scan objects to is 0, and so is a strict-mode miss
        // that would be Msg 13608 anywhere else. Like JSON_MODIFY it answers
        // for the whole document, so trailing text counts against it even when
        // the path itself would have resolved.
        var scan = JsonText.Scan(jv.AsString);
        if (scan.HasError)
            return SqlValue.FromBoolean(false);

        using var doc = JsonText.Parse(scan.Text!);
        return SqlValue.FromBoolean(path.Walk(doc.RootElement, scan, out _) == JsonWalkResult.Resolved);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Bit;

    internal override string DebugDisplay() => $"JSON_PATH_EXISTS({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()})";
}
