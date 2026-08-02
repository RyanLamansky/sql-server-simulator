using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>JSON_MODIFY(json, path, newValue)</c>: returns the JSON-text first
/// argument with the slot the path names rewritten. The result is the input's
/// own text with one span spliced — every byte the edit didn't touch survives,
/// so <c>JSON_MODIFY('  {"a" : 1}  ', '$.a', 2)</c> answers
/// <c>  {"a" : 2}  </c> the way real SQL Server does. Result is typed
/// <see cref="SqlType.NVarcharMax"/> (<c>nvarchar(max)</c>, matching real SQL
/// Server) so an updated document larger than the bounded 2-byte wire length
/// prefix streams as PLP rather than crashing the TDS session.
/// </summary>
/// <remarks>
/// <para>
/// EF Core 10 emits this from <c>OwnsOne(...).ToJson()</c> partial-update
/// paths — e.g. mutating <c>c.Address.City</c> compiles to
/// <c>UPDATE … SET [Address] = JSON_MODIFY([Address], 'strict $.City', JSON_VALUE(@p0, '$.""'))</c>.
/// The <c>strict</c> prefix is honored: a missing leaf path raises
/// Msg 13608, matching SaveChanges' implicit assumption that the owned
/// object is always fully populated.
/// </para>
/// <para>
/// The four edits and where each one splices, all probe-confirmed against
/// SQL Server 2025: a <em>replace</em> swaps the leaf value's own span; an
/// <em>insert</em> goes immediately before the container's closing bracket,
/// with a leading comma unless the container is empty; a <em>delete</em>
/// (a lax path, an object member, a NULL value) takes the member plus the
/// comma before it, or the comma after it when the member is the container's
/// first; and an <em>append</em> goes immediately before the target array's
/// closing bracket. Inserted text is canonical whatever the document's own
/// spacing convention — SQL Server writes <c>,"b":2</c> into
/// <c>{ "a" : 1 }</c>.
/// </para>
/// </remarks>
internal sealed class JsonModify : Expression
{
    /// <summary>Msg 13608's State byte when JSON_MODIFY is the one raising it.</summary>
    private const byte StrictNotFoundState = 2;

    private readonly Expression jsonInput;
    private readonly Expression pathInput;
    private readonly Expression newValueInput;

    /// <summary>
    /// Whether the third argument is itself a JSON producer, whose text
    /// embeds raw rather than as a quoted string.
    /// </summary>
    private readonly bool newValueIsJson;

    public JsonModify(ParserContext context)
    {
        this.jsonInput = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pathInput = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.newValueInput = Parse(context.MoveNextRequiredReturnSelf());
        this.newValueIsJson = JsonValueRender.ProducesJson(this.newValueInput);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var jsonInputValue = this.jsonInput.Run(runtime);
        var pathValue = this.pathInput.Run(runtime);
        var newSqlValue = this.newValueInput.Run(runtime);
        RequireWritableValueType(newSqlValue.Type);
        if (jsonInputValue.IsNull || pathValue.IsNull)
            return SqlValue.Null(SqlType.NVarcharMax);

        var path = JsonPath.Parse(pathValue.AsString, acceptAppend: true);

        // `$` on its own names the whole document, which leaves JSON_MODIFY
        // no slot to write into — real refuses the path rather than replacing
        // the document with the third argument. `append $` still resolves,
        // onto a root array.
        if (path.Segments.Length == 0 && !path.Append)
            throw SimulatedSqlException.JsonUnsupportedModifyPath();

        var document = jsonInputValue.AsString;

        // JSON_MODIFY reproduces the whole document, so once it has an edit to
        // make it reads the lot and anything the scan objects to — trailing
        // text included — is Msg 13609 (State 7). A path that can't apply to
        // what it finds is a no-op the reader settles early, and the input
        // comes straight back however malformed the rest of it is.
        var scan = JsonText.Scan(document);
        if (scan.HasError)
        {
            if (scan.Text is not null)
            {
                using var settled = JsonText.Parse(scan.Text);
                if (path.Walk(settled.RootElement, scan, out _) == JsonWalkResult.Abandoned)
                    return Unchanged(document);
            }
            throw SimulatedSqlException.JsonInvalidText(scan.BadCharacter, scan.BadPosition, 7);
        }

        var site = JsonEdit.Locate(document, path);
        return site.Outcome switch
        {
            JsonEditOutcome.Found => this.EditFound(document, path, site, newSqlValue),
            JsonEditOutcome.MemberMissing => this.EditMissing(document, path, site, newSqlValue),
            _ => path.Mode == JsonPathMode.Strict
                ? throw SimulatedSqlException.JsonStrictPathNotFound(StrictNotFoundState)
                : Unchanged(document),
        };
    }

    /// <summary>
    /// The path named a value the document holds: append onto it, delete it,
    /// or overwrite it.
    /// </summary>
    private SqlValue EditFound(string document, in JsonPath path, in JsonEditSite site, SqlValue newValue)
    {
        if (path.Append)
        {
            if (document[site.ValueStart] != '[')
            {
                return path.Mode == JsonPathMode.Strict
                    ? throw SimulatedSqlException.JsonArrayNotFound()
                    : Unchanged(document);
            }
            var closingBracket = site.ValueEnd - 1;
            var separator = JsonEdit.IsEmptyArray(document, site.ValueStart, site.ValueEnd) ? "" : ",";
            return Splice(document, closingBracket, closingBracket, separator + this.Render(newValue));
        }

        // A NULL value deletes the member — but only through a lax path, and
        // only from an object: a strict path and an array element both take
        // a JSON `null` instead.
        return newValue.IsNull && path.Mode == JsonPathMode.Lax && !path.Segments[^1].IsIndex
            ? Delete(document, site)
            : Splice(document, site.ValueStart, site.ValueEnd, this.Render(newValue));
    }

    /// <summary>
    /// Removes an object member, taking the comma before it when it has one,
    /// else the comma after it, else neither. The whitespace on the far side
    /// of whichever comma goes stays put, so
    /// <c>{ "a" : 1 , "b" : 2 }</c> minus <c>a</c> is <c>{  "b" : 2 }</c>.
    /// </summary>
    private static SqlValue Delete(string document, in JsonEditSite site) =>
        site.PrecedingComma >= 0 ? Splice(document, site.PrecedingComma, site.ValueEnd, "")
        : site.FollowingComma >= 0 ? Splice(document, site.MemberStart, site.FollowingComma + 1, "")
        : Splice(document, site.MemberStart, site.ValueEnd, "");

    /// <summary>
    /// The leaf's container exists but holds no such member. A new property
    /// joins an object; an out-of-range array index has no slot to occupy and
    /// leaves the document alone.
    /// </summary>
    private SqlValue EditMissing(string document, in JsonPath path, in JsonEditSite site, SqlValue newValue)
    {
        if (path.Mode == JsonPathMode.Strict)
            throw SimulatedSqlException.JsonStrictPathNotFound(StrictNotFoundState);

        var leaf = path.Segments[^1];
        if (leaf.IsIndex || (newValue.IsNull && !path.Append))
            return Unchanged(document);

        // An append onto a key the object lacks creates it holding a
        // one-element array, NULL value included.
        var rendered = this.Render(newValue);
        var inserted = new StringBuilder(site.ContainerEmpty ? "" : ",");
        JsonValueRender.AppendJsonString(inserted, leaf.Property!);
        _ = path.Append
            ? inserted.Append(":[").Append(rendered).Append(']')
            : inserted.Append(':').Append(rendered);
        return Splice(document, site.ContainerClose, site.ContainerClose, inserted.ToString());
    }

    /// <summary>
    /// Replaces <paramref name="document"/>'s <c>[start, end)</c> span with
    /// <paramref name="replacement"/>, leaving every other character —
    /// whitespace and key order included — as written.
    /// </summary>
    private static SqlValue Splice(string document, int start, int end, string replacement) =>
        SqlValue.FromNVarchar(
            SqlType.NVarcharMax,
            string.Concat(document.AsSpan(0, start), replacement, document.AsSpan(end)));

    private static SqlValue Unchanged(string document) => SqlValue.FromNVarchar(SqlType.NVarcharMax, document);

    /// <summary>
    /// Renders the third argument as the JSON text that goes into the slot.
    /// Numbers stay JSON numbers, booleans stay JSON booleans, a JSON-typed
    /// argument (a nested JSON_QUERY / JSON_OBJECT / JSON_ARRAY /
    /// JSON_MODIFY) embeds raw, and everything else becomes a JSON string.
    /// </summary>
    private string Render(SqlValue value)
    {
        var sb = new StringBuilder();
        JsonValueRender.Append(sb, value, this.newValueIsJson);
        return sb.ToString();
    }

    /// <summary>
    /// The types the written value may carry: the string family bar the
    /// legacy LOBs, the integer family, decimal / numeric, float, real and
    /// bit. Everything else — money, every date/time type,
    /// <c>uniqueidentifier</c>, binary / varbinary / image, text / ntext,
    /// xml, sql_variant, hierarchyid and the spatial types — is Msg 8116,
    /// even as a typed NULL (all probe-confirmed against SQL Server 2025).
    /// An untyped <c>NULL</c> literal types as <c>int</c> and so passes,
    /// which is what leaves the delete-a-member form open.
    /// </summary>
    private static void RequireWritableValueType(SqlType type)
    {
        if (SqlType.IsIntegerCategory(type)
            || type is DecimalSqlType
            || type == SqlType.Float
            || type == SqlType.Real
            || type == SqlType.Bit
            // xml and the spatial types share the string category but are
            // refused like the legacy LOBs.
            || (SqlType.IsStringCategory(type) && type is not TextSqlType and not NTextSqlType and not XmlSqlType and not SpatialSqlType))
        {
            return;
        }
        throw SimulatedSqlException.InvalidArgumentDataType(type.SqlServerName, argumentIndex: 3, "json_modify");
    }

    /// <summary>
    /// Real binds the third argument's type while compiling — probe-confirmed
    /// that a <c>date</c> third argument reports Msg 8116 over an empty
    /// rowset — so the gate runs here as well as per value.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        RequireWritableValueType(this.newValueInput.GetSqlType(batch, resolveColumnType));
        return SqlType.NVarcharMax;
    }

    internal override string DebugDisplay() => $"JSON_MODIFY({this.jsonInput.DebugDisplay()}, {this.pathInput.DebugDisplay()}, {this.newValueInput.DebugDisplay()})";
}
