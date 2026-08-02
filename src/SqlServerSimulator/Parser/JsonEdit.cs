using System.Globalization;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Locates the slot a <c>JSON_MODIFY</c> edit occupies in the document's own
/// text, so the result can be that text with one span spliced rather than a
/// re-serialization of a parsed tree. SQL Server keeps every byte it wasn't
/// asked to change — <c>JSON_MODIFY('  {"a" : 1}  ', '$.a', 2)</c> answers
/// <c>  {"a" : 2}  </c> — which only a source-offset edit reproduces.
/// </summary>
/// <remarks>
/// The text handed in has already passed <see cref="JsonText.Scan"/>, so the
/// walk assumes a well-formed root object or array and does no error
/// reporting. Property lookup takes the <em>first</em> member of a given
/// name, matching SQL Server's reader: <c>{"a":1,"a":2}</c> edits the
/// leading <c>a</c> and leaves the trailing one standing.
/// </remarks>
internal static class JsonEdit
{
    /// <summary>
    /// Walks <paramref name="path"/> through <paramref name="text"/> and
    /// reports where the leaf sits. An empty segment list names the root
    /// value itself, which <c>append $</c> reaches.
    /// </summary>
    public static JsonEditSite Locate(string text, in JsonPath path)
    {
        var i = 0;
        SkipWhitespace(text, ref i);

        if (path.Segments.Length == 0)
        {
            var rootEnd = i;
            SkipValue(text, ref rootEnd);
            return new JsonEditSite(i, i, rootEnd, -1, -1);
        }

        var site = JsonEditSite.PathMissing;
        for (var s = 0; s < path.Segments.Length; s++)
        {
            var segment = path.Segments[s];
            if (text[i] is not ('{' or '['))
                return JsonEditSite.PathMissing;

            // A property step needs an object and an index step an array;
            // either mismatch leaves the path with nothing to name.
            var isObject = text[i] == '{';
            if (segment.IsIndex == isObject)
                return JsonEditSite.PathMissing;

            site = ScanContainer(text, i, segment);
            if (site.Outcome != JsonEditOutcome.Found)
            {
                // Only the leaf's own container is somewhere a member can be
                // inserted; a miss further up leaves nothing to insert into.
                return s == path.Segments.Length - 1 ? site : JsonEditSite.PathMissing;
            }
            i = site.ValueStart;
        }

        return site;
    }

    /// <summary>
    /// Reads the container opening at <paramref name="containerStart"/>
    /// member by member, stopping at the first one
    /// <paramref name="segment"/> names. A miss reports the container's
    /// closing bracket instead — the point an inserted member goes.
    /// </summary>
    private static JsonEditSite ScanContainer(string text, int containerStart, in JsonPath.Segment segment)
    {
        var isObject = text[containerStart] == '{';
        var close = isObject ? '}' : ']';
        var i = containerStart + 1;
        var index = 0;
        var precedingComma = -1;
        var empty = true;

        while (true)
        {
            SkipWhitespace(text, ref i);
            if (text[i] == close)
                return new JsonEditSite(i, empty);

            empty = false;
            var memberStart = i;
            var matched = !isObject && index == segment.Index;
            if (isObject)
            {
                var keyStart = i;
                SkipString(text, ref i);
                matched = KeyEquals(text, keyStart, i, segment.Property!);
                SkipWhitespace(text, ref i);
                i++;
                SkipWhitespace(text, ref i);
            }

            var valueStart = i;
            SkipValue(text, ref i);
            var valueEnd = i;
            SkipWhitespace(text, ref i);
            var followingComma = text[i] == ',' ? i : -1;

            if (matched)
                return new JsonEditSite(memberStart, valueStart, valueEnd, precedingComma, followingComma);

            if (followingComma < 0)
                return new JsonEditSite(i, empty);
            i = followingComma + 1;
            precedingComma = followingComma;
            index++;
        }
    }

    /// <summary>
    /// Whether the array spanning <paramref name="start"/> to
    /// <paramref name="end"/> holds no element — the case an appended
    /// element joins without a leading comma.
    /// </summary>
    public static bool IsEmptyArray(string text, int start, int end)
    {
        var i = start + 1;
        SkipWhitespace(text, ref i);
        return i == end - 1;
    }

    /// <summary>
    /// Compares a source property name — the quoted token spanning
    /// <paramref name="start"/> to <paramref name="end"/>, escapes and all —
    /// against a path segment's decoded name.
    /// </summary>
    private static bool KeyEquals(string text, int start, int end, string name)
    {
        var i = start + 1;
        var closingQuote = end - 1;
        var n = 0;
        while (i < closingQuote)
        {
            char c;
            if (text[i] == '\\')
            {
                var escape = text[i + 1];
                if (escape == 'u')
                {
                    c = (char)int.Parse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    i += 6;
                }
                else
                {
                    c = escape switch
                    {
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => escape,
                    };
                    i += 2;
                }
            }
            else
            {
                c = text[i];
                i++;
            }

            if (n >= name.Length || name[n] != c)
                return false;
            n++;
        }
        return n == name.Length;
    }

    /// <summary>Advances past the value starting at <paramref name="i"/>.</summary>
    private static void SkipValue(string text, ref int i)
    {
        if (text[i] == '"')
        {
            SkipString(text, ref i);
            return;
        }

        if (text[i] is not ('{' or '['))
        {
            while (text[i] is not (',' or ':' or ']' or '}' or ' ' or '\t' or '\r' or '\n'))
                i++;
            return;
        }

        var depth = 0;
        while (true)
        {
            var c = text[i];
            if (c == '"')
            {
                SkipString(text, ref i);
                continue;
            }
            i++;
            if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']' && --depth == 0)
            {
                return;
            }
        }
    }

    /// <summary>Advances past the quoted string starting at <paramref name="i"/>.</summary>
    private static void SkipString(string text, ref int i)
    {
        i++;
        while (text[i] != '"')
            i += text[i] == '\\' ? 2 : 1;
        i++;
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n')
            i++;
    }
}

/// <summary>Where a <see cref="JsonEdit.Locate"/> landed.</summary>
internal enum JsonEditOutcome
{
    /// <summary>
    /// A step before the leaf missed, so there is no container the leaf
    /// could be written into. The zero value, so <c>default</c> reads as it.
    /// </summary>
    PathMissing,

    /// <summary>The path named a value the document holds.</summary>
    Found,

    /// <summary>
    /// The leaf's own container exists but holds no such member — the case a
    /// new property is inserted into.
    /// </summary>
    MemberMissing,
}

/// <summary>
/// The source-text coordinates <c>JSON_MODIFY</c> splices at. Every index is
/// into the document argument as written, so the surrounding text comes back
/// untouched.
/// </summary>
internal readonly struct JsonEditSite
{
    public readonly JsonEditOutcome Outcome;

    /// <summary>
    /// Where the leaf member starts — its property name's opening quote, or,
    /// for an array element, the value itself. The left edge of a deletion
    /// that has no preceding comma to swallow.
    /// </summary>
    public readonly int MemberStart;

    /// <summary>The first character of the leaf's value.</summary>
    public readonly int ValueStart;

    /// <summary>One past the last character of the leaf's value.</summary>
    public readonly int ValueEnd;

    /// <summary>The comma before the leaf member, or -1 when it is the first.</summary>
    public readonly int PrecedingComma;

    /// <summary>The comma after the leaf member, or -1 when it is the last.</summary>
    public readonly int FollowingComma;

    /// <summary>
    /// The closing bracket of the container a missing member would join —
    /// SQL Server inserts immediately before it, so whatever whitespace the
    /// container's last member trails survives.
    /// </summary>
    public readonly int ContainerClose;

    /// <summary>Whether that container holds no member, so the insert needs no comma.</summary>
    public readonly bool ContainerEmpty;

    /// <summary>The path ran out of document before its last segment.</summary>
    public static readonly JsonEditSite PathMissing;

    /// <summary>The leaf, with the container coordinates a deletion needs.</summary>
    public JsonEditSite(int memberStart, int valueStart, int valueEnd, int precedingComma, int followingComma)
    {
        this.Outcome = JsonEditOutcome.Found;
        this.MemberStart = memberStart;
        this.ValueStart = valueStart;
        this.ValueEnd = valueEnd;
        this.PrecedingComma = precedingComma;
        this.FollowingComma = followingComma;
        this.ContainerClose = -1;
    }

    /// <summary>The leaf's container, which holds no member of that name or index.</summary>
    public JsonEditSite(int containerClose, bool empty)
    {
        this.Outcome = JsonEditOutcome.MemberMissing;
        this.MemberStart = -1;
        this.ValueStart = -1;
        this.ValueEnd = -1;
        this.PrecedingComma = -1;
        this.FollowingComma = -1;
        this.ContainerClose = containerClose;
        this.ContainerEmpty = empty;
    }
}
