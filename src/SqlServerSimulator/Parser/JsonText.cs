using System.Text;
using System.Text.Json;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Reads the document argument of a JSON function the way SQL Server's own
/// JSON reader does: left to right, stopping as soon as the requested path is
/// satisfied. Two rules fall out of that and neither matches
/// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> on its own —
/// only an object or an array is JSON text (a root-level scalar such as
/// <c>1</c> or <c>"abc"</c> is malformed input), and text past the end of the
/// root value is a problem only for a caller that had to keep reading.
/// </summary>
/// <remarks>
/// <see cref="Scan"/> reports the Msg 13609 that a caller raises when the path
/// does <em>not</em> resolve, and hands back the JSON text that did read
/// cleanly — for a truncated document, the prefix consumed with its open
/// containers closed, so a value read before the truncation still answers.
/// <see cref="JsonScan.OpenDepth"/> marks how far that repair reaches.
/// </remarks>
internal static class JsonText
{
    /// <summary>
    /// The character SQL Server names in Msg 13609 when the reader ran off the
    /// end of the text rather than hitting an unexpected character.
    /// </summary>
    internal const char EndOfText = '.';

    /// <summary>
    /// Nesting past <see cref="System.Text.Json"/>'s 64-level default is legal
    /// input, so the depth cap is lifted rather than surfacing as a parse
    /// failure. <see cref="Scan"/> tracks its own containers on the heap, so
    /// deep input costs memory rather than stack.
    /// </summary>
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = int.MaxValue };

    /// <summary>Parses text a <see cref="Scan"/> already validated.</summary>
    public static JsonDocument Parse(string scanned) => JsonDocument.Parse(scanned, DocumentOptions);

    /// <summary>
    /// Raises what a read-only JSON scalar owes for a path that didn't
    /// resolve: the document's pending Msg 13609 when the reader had to get as
    /// far as the problem, else the strict-mode Msg 13608. Returns without
    /// raising for a lax path over a document with nothing wrong ahead of it —
    /// the caller answers NULL.
    /// </summary>
    public static void RaiseUnresolved(in JsonScan scan, JsonWalkResult result, JsonPathMode mode)
    {
        if (result is not JsonWalkResult.Abandoned && scan.HasError)
            throw SimulatedSqlException.JsonInvalidText(scan.BadCharacter, scan.BadPosition);
        if (mode == JsonPathMode.Strict)
            throw SimulatedSqlException.JsonStrictPathNotFound();
    }

    private enum State
    {
        /// <summary>A value is required — after a <c>,</c> in an array or a <c>:</c> in an object.</summary>
        Value,

        /// <summary>The first element position of an array: a value, or <c>]</c> for the empty array.</summary>
        ArrayValue,

        /// <summary>A quoted property name is required — after a <c>,</c> in an object.</summary>
        ObjectKey,

        /// <summary>The first member position of an object: a property name, or <c>}</c> for the empty object.</summary>
        ObjectKeyOrEnd,

        /// <summary>The <c>:</c> between a property name and its value.</summary>
        Colon,

        /// <summary>A <c>,</c> or the open container's own closing bracket.</summary>
        Delimiter,
    }

    /// <summary>
    /// Reads the leading root value out of <paramref name="text"/>. See
    /// <see cref="JsonScan"/> for how the result reads.
    /// </summary>
    public static JsonScan Scan(string text)
    {
        var i = 0;
        SkipWhitespace(text, ref i);
        if (i >= text.Length)
            return new JsonScan(null, 0, EndOfText, i, cleanCut: true);
        if (text[i] is not ('{' or '['))
            return new JsonScan(null, 0, text[i], i, cleanCut: true);

        var start = i;
        var closers = new List<char>();

        // The point the repair cuts at: the end of the last value that read
        // completely, or of the opening bracket of the container being read.
        var safeEnd = i;
        var safeDepth = 0;
        var state = State.Value;

        while (true)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length)
                return Truncated(text, start, closers, safeEnd, safeDepth, EndOfText, i);

            var c = text[i];
            switch (state)
            {
                case State.Colon:
                    if (c != ':')
                        return Truncated(text, start, closers, safeEnd, safeDepth, c, i);
                    i++;
                    state = State.Value;
                    continue;

                case State.ObjectKey:
                case State.ObjectKeyOrEnd:
                    if (c == '}' && state == State.ObjectKeyOrEnd)
                        break;
                    if (c != '"' || !TryReadString(text, ref i))
                        return Truncated(text, start, closers, safeEnd, safeDepth, c, i);
                    state = State.Colon;
                    continue;

                case State.Delimiter:
                    if (c == ',')
                    {
                        i++;
                        state = closers[^1] == '}' ? State.ObjectKey : State.Value;
                        continue;
                    }
                    if (c != closers[^1])
                        return Truncated(text, start, closers, safeEnd, safeDepth, c, i);
                    break;

                case State.Value:
                case State.ArrayValue:
                default:
                    if (c == ']' && state == State.ArrayValue)
                        break;
                    if (c is '{' or '[')
                    {
                        closers.Add(c == '{' ? '}' : ']');
                        i++;
                        safeEnd = i;
                        safeDepth = closers.Count;
                        state = c == '{' ? State.ObjectKeyOrEnd : State.ArrayValue;
                        continue;
                    }

                    // A malformed scalar is reported at its first character,
                    // not at the character that spoiled it: SQL Server reads
                    // the whole token before judging it, so `{"a":1x}` names
                    // '1' and `{"a":01}` names '0'.
                    var tokenStart = i;
                    if (!TryReadScalar(text, ref i))
                        return Truncated(text, start, closers, safeEnd, safeDepth, c, tokenStart);
                    safeEnd = i;
                    safeDepth = closers.Count;
                    state = State.Delimiter;
                    continue;
            }

            // Shared close-a-container tail for the three states that can reach
            // one: `}` / `]` after a member, and the empty-container forms.
            i++;
            closers.RemoveAt(closers.Count - 1);
            if (closers.Count == 0)
                return Rooted(text, start, i);
            safeEnd = i;
            safeDepth = closers.Count;
            state = State.Delimiter;
        }
    }

    /// <summary>
    /// The root value read cleanly. Anything but whitespace after it is the
    /// Msg 13609 a caller raises once its path fails to resolve.
    /// </summary>
    private static JsonScan Rooted(string text, int start, int end)
    {
        var document = start == 0 && end == text.Length ? text : text[start..end];
        var after = end;
        SkipWhitespace(text, ref after);
        return after < text.Length
            ? new JsonScan(document, 0, text[after], after, cleanCut: true)
            : new JsonScan(document);
    }

    /// <summary>
    /// The scan stopped inside the root value. The text handed back closes the
    /// containers that were open at the cut, so a path reaching a value that
    /// read completely still answers; <see cref="JsonScan.OpenDepth"/> tells
    /// the walk which nodes only exist because of that repair.
    /// </summary>
    private static JsonScan Truncated(string text, int start, List<char> closers, int safeEnd, int safeDepth, char bad, int position)
    {
        if (safeDepth == 0)
            return new JsonScan(null, 0, bad, position, cleanCut: true);

        var repaired = new StringBuilder(safeEnd - start + safeDepth).Append(text, start, safeEnd - start);
        for (var d = safeDepth - 1; d >= 0; d--)
            _ = repaired.Append(closers[d]);

        // A reader that settles on the last complete value still reads the one
        // separator behind it before it stops, so that separator doesn't count
        // as text the repair dropped.
        var after = safeEnd;
        SkipWhitespace(text, ref after);
        if (after < text.Length && text[after] == ',')
        {
            after++;
            SkipWhitespace(text, ref after);
        }
        return new JsonScan(repaired.ToString(), safeDepth, bad, position, cleanCut: after >= position);
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n')
            i++;
    }

    /// <summary>Reads one scalar token, returning false when it isn't a JSON string, number or literal.</summary>
    private static bool TryReadScalar(string text, ref int i)
    {
        if (text[i] == '"')
            return TryReadString(text, ref i);

        var start = i;
        while (i < text.Length && text[i] is not (',' or ':' or ']' or '}' or ' ' or '\t' or '\r' or '\n'))
            i++;
        var token = text.AsSpan(start, i - start);
        return token.SequenceEqual("true") || token.SequenceEqual("false") || token.SequenceEqual("null") || IsNumber(token);
    }

    /// <summary>JSON's number grammar: <c>-? (0 | [1-9][0-9]*) (. [0-9]+)? ([eE] [+-]? [0-9]+)?</c>.</summary>
    private static bool IsNumber(ReadOnlySpan<char> token)
    {
        var i = 0;
        if (i < token.Length && token[i] == '-')
            i++;
        if (i >= token.Length || !char.IsAsciiDigit(token[i]))
            return false;
        if (token[i] == '0')
        {
            i++;
        }
        else
        {
            while (i < token.Length && char.IsAsciiDigit(token[i]))
                i++;
        }

        if (i < token.Length && token[i] == '.')
        {
            i++;
            if (i >= token.Length || !char.IsAsciiDigit(token[i]))
                return false;
            while (i < token.Length && char.IsAsciiDigit(token[i]))
                i++;
        }

        if (i < token.Length && (token[i] == 'e' || token[i] == 'E'))
        {
            i++;
            if (i < token.Length && (token[i] == '+' || token[i] == '-'))
                i++;
            if (i >= token.Length || !char.IsAsciiDigit(token[i]))
                return false;
            while (i < token.Length && char.IsAsciiDigit(token[i]))
                i++;
        }

        return i == token.Length;
    }

    /// <summary>Reads a quoted string, leaving <paramref name="i"/> past the closing quote.</summary>
    private static bool TryReadString(string text, ref int i)
    {
        var j = i + 1;
        while (j < text.Length)
        {
            var c = text[j];
            if (c == '"')
            {
                i = j + 1;
                return true;
            }
            if (c == '\\')
            {
                j++;
                if (j >= text.Length)
                    return false;
                if (text[j] == 'u')
                {
                    if (j + 4 >= text.Length)
                        return false;
                    for (var h = 1; h <= 4; h++)
                    {
                        if (!char.IsAsciiHexDigit(text[j + h]))
                            return false;
                    }
                    j += 4;
                }
                else if (text[j] is not ('"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't'))
                {
                    return false;
                }
                j++;
            }
            else if (c < ' ')
            {
                return false;
            }
            else
            {
                j++;
            }
        }
        return false;
    }
}

/// <summary>
/// What <see cref="JsonText.Scan"/> made of a JSON function's document
/// argument.
/// </summary>
internal readonly struct JsonScan
{
    /// <summary>
    /// The JSON text to evaluate — the root value's own text when it read
    /// cleanly, or the repaired prefix when the scan stopped inside it. Null
    /// when nothing was recoverable, which includes a root-level scalar.
    /// </summary>
    public readonly string? Text;

    /// <summary>
    /// How many containers the repair had to close, so how deep the spine of
    /// nodes is that only <see cref="Text"/> — not the input — completes.
    /// Zero when the root value read cleanly.
    /// </summary>
    public readonly int OpenDepth;

    /// <summary>
    /// Whether a Msg 13609 is pending. It fires only for a caller that had to
    /// read past the point the scan stopped: a path satisfied earlier answers
    /// as if the rest of the text weren't there, matching SQL Server's
    /// read-until-satisfied reader.
    /// </summary>
    public readonly bool HasError;

    /// <summary>The character Msg 13609 names, or <see cref="JsonText.EndOfText"/> at the end of the text.</summary>
    public readonly char BadCharacter;

    /// <summary>The zero-based character index Msg 13609 names.</summary>
    public readonly int BadPosition;

    /// <summary>
    /// Whether <see cref="Text"/> ends where the scan stopped, with nothing
    /// but whitespace and at most the one separator behind it — so a reader
    /// walking a step past the last value it read meets
    /// <see cref="BadPosition"/> rather than more valid text the repair
    /// discarded.
    /// </summary>
    public readonly bool CleanCut;

    /// <summary>The clean scan: a complete root value with nothing but whitespace after it.</summary>
    public JsonScan(string text)
    {
        this.Text = text;
        this.CleanCut = true;
    }

    public JsonScan(string? text, int openDepth, char badCharacter, int badPosition, bool cleanCut)
    {
        this.Text = text;
        this.OpenDepth = openDepth;
        this.HasError = true;
        this.BadCharacter = badCharacter;
        this.BadPosition = badPosition;
        this.CleanCut = cleanCut;
    }
}
