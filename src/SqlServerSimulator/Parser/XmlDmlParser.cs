using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Recursive-descent parser for the XML-DML text a <c>.modify()</c> call
/// carries, run once at compile time. It produces an <see cref="XmlDml"/> and
/// raises every diagnostic real settles before the first row is read: the
/// statement-shape errors (Msg 6305 / 2205 / 2209) and the static target /
/// content type checks (Msg 2226 / 2207 / 2258 / 2240 / 2249 / 2337 / 2356 /
/// 2264 / 9310), in real's own check order.
/// </summary>
internal sealed class XmlDmlParser(
    string text,
    string? defaultNamespace,
    Dictionary<string, string> prefixes,
    ParserContext context,
    Func<string, SqlType>? resolveColumnType,
    Schemas.XmlSchemaCollection? schemaCollection,
    string method)
{
    private readonly string text = text;
    private readonly string? defaultNamespace = defaultNamespace;
    private readonly Dictionary<string, string> prefixes = prefixes;
    private readonly ParserContext context = context;
    private readonly Func<string, SqlType>? resolveColumnType = resolveColumnType;

    /// <summary>
    /// The receiver's <c>xml(&lt;collection&gt;)</c> binding, or null for an
    /// untyped receiver — the one input that can make an element target of
    /// <c>replace value of</c> legal.
    /// </summary>
    private readonly Schemas.XmlSchemaCollection? schemaCollection = schemaCollection;

    /// <summary>
    /// What real writes between the brackets of every diagnostic raised here —
    /// <c>modify</c> behind a variable receiver, and the
    /// <c>schema.table.column.modify</c> the receiving column names otherwise.
    /// </summary>
    private readonly string method = method;

    private int index;

    /// <summary>Parses the whole statement, leaving nothing unconsumed.</summary>
    public XmlDml ParseStatement()
    {
        this.SkipWhitespace();
        if (this.index >= this.text.Length)
            throw SimulatedSqlException.XQueryExpressionMissing();
        if (this.TryKeyword("insert"))
            return this.ParseInsert();
        if (this.TryKeyword("delete"))
            return this.ParseDelete();
        if (this.TryKeyword("replace"))
            return this.ParseReplaceValueOf();

        // Real distinguishes "this parses as XQuery but isn't XML-DML" (Msg
        // 6305) from "this isn't XQuery at all" (Msg 2209) by trying the
        // expression grammar on text no XML-DML keyword opened: `count(/r)`
        // and `for $i in /r return $i` are 6305 where `(` and `/r[` are 2209.
        _ = XmlQueryEngine.CompileBody(this.text, this.defaultNamespace, this.prefixes, this.method);
        throw SimulatedSqlException.XmlDmlExpressionRequired();
    }

    private XmlDml ParseDelete()
    {
        var target = this.ParsePath(this.text.Length);
        return target.Kind == XmlDmlNodeKind.Document
            ? throw SimulatedSqlException.XmlDmlOnlyNodesDeletable(this.method, target.Describe())
            : XmlDml.CreateDelete(target, this.method);
    }

    private XmlDml ParseReplaceValueOf()
    {
        if (!this.TryKeyword("value") || !this.TryKeyword("of"))
            throw this.SyntaxError();

        var withIndex = this.FindKeyword("with");
        if (withIndex < 0)
            throw SimulatedSqlException.XmlDmlWithExpected();
        var target = this.ParsePath(withIndex);
        this.index = withIndex + "with".Length;

        // Real checks the target's cardinality first, then its kind — an
        // unbracketed path reports 2337 whatever it selects.
        if (!target.Singleton)
            throw SimulatedSqlException.XmlDmlReplaceTargetNotSingleton(this.method, target.Describe());
        if (target.Kind is not (XmlDmlNodeKind.Attribute or XmlDmlNodeKind.Text) && !this.TargetHasSimpleTypedContent(target))
            throw SimulatedSqlException.XmlDmlReplaceTargetNotSimpleContent(this.method, target.Describe());

        this.SkipWhitespace();
        if (this.Current is '<')
            throw SimulatedSqlException.XmlDmlReplaceWithConstructedXml(this.method);

        // Real takes a whole XQuery expression here, not a term list, so the
        // rest of the text compiles through the read methods' own engine with
        // the `sql:` accessors admitted — which is what evaluates
        // `data(/IndividualSurvey/TotalPurchaseYTD)[1] + sql:column("inserted.LineTotal")`.
        var accessors = new XmlSqlAccessorScope((isColumn, name) => isColumn ? null : this.ResolveContentAccessor(isColumn, name));
        var value = XmlQueryEngine.CompileBody(this.text[this.index..], this.defaultNamespace, this.prefixes, this.method, accessors, this.Typing);
        this.ResolveAccessorNames(accessors);
        this.RequireValueOfDeclaredType(target, value);
        var nillable = target.Kind == XmlDmlNodeKind.Element && this.Typing?.NillableElements.Contains(target.Name) == true;
        return XmlDml.CreateReplaceValueOf(target, value, [.. accessors.Accessors], nillable, this.method);
    }

    /// <summary>The receiver's schema typing, or null for untyped <c>xml</c>.</summary>
    private XmlStaticTyping? Typing => this.schemaCollection?.GetStaticTyping();

    /// <summary>
    /// Msg 2247: over a typed instance, a <c>replace value of</c> target the
    /// schema types takes only a value of that type or one derived from it —
    /// real checks the <c>with</c> expression's static type, so
    /// <c>xs:integer</c> into an <c>xs:int</c> attribute and <c>xs:decimal</c>
    /// into an <c>xs:double</c> element are both refused while an
    /// <c>int</c> <c>sql:variable</c> into an <c>xs:decimal</c> element is
    /// taken (probed 2026-09-28 against SQL Server 2025). An untyped value,
    /// and the empty sequence, are left to the write.
    /// </summary>
    private void RequireValueOfDeclaredType(XmlDmlPath target, XmlQueryExpr value)
    {
        if (this.Typing is not { } typing || value is XmlSequenceExpr { IsEmpty: true })
            return;
        string? expected = null;
        _ = target.Kind switch
        {
            XmlDmlNodeKind.Attribute => typing.AttributeTypes.TryGetValue(target.Name, out expected),
            XmlDmlNodeKind.Element => typing.ElementTypes.TryGetValue(target.Name, out expected),
            _ => false,
        };
        if (expected is null || value.AtomizedKind() == XmlStaticKind.Untyped)
            return;

        var plural = XmlQueryExpr.IsPlural(value.Occurrence);
        if (plural || !XmlAtomicTypes.IsSubtype(value.TypeName, XmlAtomicTypes.Resolve(expected[3..]), targetIsUntyped: false))
        {
            throw SimulatedSqlException.XmlDmlReplaceValueTypeMismatch(
                this.method, plural ? value.AtomizedTypeName() : value.TypeName, expected);
        }
    }

    /// <summary>
    /// Validates each <c>sql:variable</c> the value expression named — the
    /// lookup is what reports Msg 137 for one never declared, exactly as the
    /// term-based accessor path reports it.
    /// </summary>
    /// <remarks>
    /// A <c>sql:column</c> is <em>not</em> resolved here: an UPDATE's SET list
    /// parses ahead of its FROM clause, so <c>sql:column("inserted.LineTotal")</c>
    /// names a source that isn't in scope yet. Those bind in
    /// <see cref="Expressions.XmlModify.GetSqlType"/>, which the statement calls
    /// with its own full scope — real's binding point, and still compile time.
    /// </remarks>
    private void ResolveAccessorNames(XmlSqlAccessorScope accessors)
    {
        foreach (var accessor in accessors.Accessors)
        {
            if (!accessor.IsColumn)
                _ = this.VariableType(accessor.Name);
        }
    }

    /// <summary>
    /// Whether <paramref name="target"/> selects an element the receiver's
    /// schema collection types with simple content — the only way an element
    /// (rather than an attribute or a <c>text()</c> node) is a legal
    /// <c>replace value of</c> target. An untyped receiver has no collection
    /// and so answers false, which is real's Msg 2356.
    /// </summary>
    private bool TargetHasSimpleTypedContent(XmlDmlPath target) =>
        target.Kind == XmlDmlNodeKind.Element
        && this.schemaCollection is { } collection
        && collection.GetSimpleContentElementNames().Contains(target.Name);

    private XmlDml ParseInsert()
    {
        var positionAt = this.FindInsertPosition();
        if (positionAt < 0)
            throw this.SyntaxError();
        var contentText = this.text[this.index..positionAt].Trim();

        // The content is an XQuery expression like any other — a constructor,
        // a path copying nodes out of the instance, a FLWOR — with one form of
        // its own: a bare sql:variable / sql:column carrying an xml instance,
        // which only this position admits (Msg 9342 anywhere else).
        XmlDmlItem[] content = [];
        XmlQueryExpr? contentExpression = null;
        XmlSqlAccessorRef[] contentAccessors = [];
        if (IsBareAccessor(contentText))
        {
            content = this.ParseContentSequence();
            this.SkipWhitespace();
            if (this.index != positionAt)
                throw this.SyntaxError();
        }
        else
        {
            if (contentText.Length == 0)
                throw this.SyntaxError();
            var accessors = new XmlSqlAccessorScope(this.ResolveContentAccessor);
            contentExpression = XmlQueryEngine.CompileBody(contentText, this.defaultNamespace, this.prefixes, this.method, accessors, this.Typing);
            contentAccessors = [.. accessors.Accessors];
            this.index = positionAt;
        }

        XmlDmlPosition position;
        if (this.TryKeyword("as"))
        {
            position = this.TryKeyword("first") ? XmlDmlPosition.AsFirst
                : this.TryKeyword("last") ? XmlDmlPosition.AsLast
                : throw this.SyntaxError();
            if (!this.TryKeyword("into"))
                throw this.SyntaxError();
        }
        else
        {
            position = this.TryKeyword("into") ? XmlDmlPosition.Into
                : this.TryKeyword("before") ? XmlDmlPosition.Before
                : this.TryKeyword("after") ? XmlDmlPosition.After
                : throw this.SyntaxError();
        }

        var target = this.ParsePath(this.text.Length);

        // Check order is real's, probed one shape at a time: target
        // cardinality, then the content's own type, then the
        // attribute-with-a-position rule, then the target's node kind.
        if (!target.Singleton)
            throw SimulatedSqlException.XmlDmlInsertTargetNotSingleton(this.method, target.Describe());
        foreach (var item in content)
        {
            if (item.Term.StaticType is { } staticType && staticType is not XmlSqlType)
                throw SimulatedSqlException.XmlDmlOnlyNodesInsertable(this.method, XmlDml.XQueryTypeName(staticType));
        }
        if (contentExpression is not null && contentExpression.Kind != XmlStaticKind.Node)
            throw SimulatedSqlException.XmlDmlOnlyNodesInsertable(this.method, contentExpression.AtomizedTypeName());
        var positional = position is XmlDmlPosition.Before or XmlDmlPosition.After;
        if (positional && contentExpression is { IsAttributeOnly: true })
            throw SimulatedSqlException.XmlDmlAttributeInsertHasPosition(this.method, contentExpression.NodeTypeBase());
        if (positional)
        {
            if (target.Kind is XmlDmlNodeKind.Attribute or XmlDmlNodeKind.Document)
                throw SimulatedSqlException.XmlDmlInsertBeforeAfterTargetKind(this.method, target.Describe());
        }
        else if (target.Kind is not (XmlDmlNodeKind.Element or XmlDmlNodeKind.Document))
        {
            throw SimulatedSqlException.XmlDmlInsertIntoTargetKind(this.method, target.Describe());
        }

        return XmlDml.CreateInsert(target, content, contentExpression, contentAccessors, position, this.method);
    }

    /// <summary>
    /// Whether an insert's content is one <c>sql:variable</c> /
    /// <c>sql:column</c> accessor, or a parenthesized list of them — the form
    /// that may carry a whole <c>xml</c> instance.
    /// </summary>
    private static bool IsBareAccessor(string content)
    {
        var body = content.StartsWith('(') && content.EndsWith(')') ? content[1..^1] : content;
        foreach (var part in body.Split(','))
        {
            var trimmed = part.Trim();
            if (!(trimmed.StartsWith("sql:variable", StringComparison.Ordinal) || trimmed.StartsWith("sql:column", StringComparison.Ordinal))
                || !trimmed.EndsWith(')'))
            {
                return false;
            }
        }
        return body.Length > 0;
    }

    /// <summary>
    /// Types a <c>sql:</c> accessor inside an insert's content expression the
    /// way a read method's are typed: a variable by its declared type (Msg
    /// 9519 / 9501 for a bad or undeclared name), a column through the
    /// statement's target-table scope where one exists, untyped otherwise. An
    /// <c>xml</c> value is Msg 9342 — only the bare form carries one.
    /// </summary>
    private string? ResolveContentAccessor(bool isColumn, string name)
    {
        SqlType? type;
        if (!isColumn)
        {
            type = this.VariableType(name);
        }
        else
        {
            type = this.resolveColumnType?.Invoke(name);
        }
        if (type is null)
            return null;
        if (type is XmlSqlType)
            throw SimulatedSqlException.XQuerySqlAccessorXmlNotAllowed(this.method);
        return XmlAtomicTypes.SqlTypeName(type) ?? throw SimulatedSqlException.XQuerySqlAccessorTypeNotSupported(this.method, type.SqlServerName);
    }

    /// <summary>
    /// A <c>sql:variable</c>'s declared type: the name must carry its
    /// <c>@</c> (Msg 9519) and name a declared variable (Msg 9501).
    /// </summary>
    private SqlType VariableType(string name)
    {
        if (name.Length < 2 || name[0] != '@')
            throw SimulatedSqlException.XQuerySqlVariableNameInvalid(name);
        return this.context.Batch.Variables.TryGetValue(name[1..], out var slot)
            ? slot.DeclaredType
            : throw SimulatedSqlException.XQuerySqlVariableNotFound(name);
    }

    /// <summary>
    /// The start of an <c>insert</c>'s position clause — <c>into</c>,
    /// <c>as first into</c> / <c>as last into</c>, <c>before</c> or
    /// <c>after</c> — found outside quotes, brackets and markup.
    /// </summary>
    private int FindInsertPosition()
    {
        var found = -1;
        foreach (var keyword in (ReadOnlySpan<string>)["after", "as", "before", "into"])
        {
            var at = this.FindKeyword(keyword);
            while (at >= 0 && keyword == "as" && !this.FollowedByFirstOrLast(at))
                at = this.FindKeyword(keyword, at + 1);
            if (at >= 0 && (found < 0 || at < found))
                found = at;
        }
        return found;
    }

    private bool FollowedByFirstOrLast(int asAt)
    {
        var after = asAt + 2;
        while (after < this.text.Length && char.IsWhiteSpace(this.text[after]))
            after++;
        return this.text.AsSpan(after).StartsWith("first", StringComparison.Ordinal) || this.text.AsSpan(after).StartsWith("last", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses an <c>insert</c>'s bare-accessor content: either a
    /// parenthesized sequence of accessors or a single one.
    /// </summary>
    private XmlDmlItem[] ParseContentSequence()
    {
        this.SkipWhitespace();
        if (this.Current != '(')
            return [XmlDmlItem.Value(this.ParseTerm())];

        this.index++;
        var items = new List<XmlDmlItem>();
        while (true)
        {
            items.Add(XmlDmlItem.Value(this.ParseTerm()));
            this.SkipWhitespace();
            if (this.Current == ',')
            {
                this.index++;
                continue;
            }
            if (this.Current != ')')
                throw this.SyntaxError();
            this.index++;
            return [.. items];
        }
    }

    private XmlDmlTerm ParseTerm()
    {
        this.SkipWhitespace();
        var word = this.PeekWord();
        if (word is not ("sql:column" or "sql:variable"))
            throw this.SyntaxError();
        this.index += word.Length;
        this.SkipWhitespace();
        if (this.Current != '(')
            throw this.SyntaxError();
        this.index++;
        this.SkipWhitespace();
        if (this.Current is not ('"' or '\''))
            throw SimulatedSqlException.XQueryStringLiteralExpected(this.method);
        var name = this.ReadQuoted(this.Current);
        this.SkipWhitespace();
        if (this.Current != ')')
            throw this.SyntaxError();
        this.index++;

        // The Variables dict is keyed without the '@' the XQuery text writes;
        // the lookup supplies the static type Msg 2207 reports.
        return word[4] == 'v'
            ? XmlDmlTerm.FromVariable(name[1..], this.VariableType(name))
            : XmlDmlTerm.FromColumn(name, this.resolveColumnType?.Invoke(name));
    }

    private string ReadQuoted(char quote)
    {
        this.index++;
        var sb = new StringBuilder();
        while (this.index < this.text.Length)
        {
            var c = this.text[this.index];
            if (c == quote)
            {
                // A doubled delimiter is XQuery's escape for one of itself.
                if (this.Peek(1) == quote)
                {
                    _ = sb.Append(quote);
                    this.index += 2;
                    continue;
                }
                this.index++;
                return sb.ToString();
            }
            _ = sb.Append(c);
            this.index++;
        }
        throw this.SyntaxError();
    }

    /// <summary>
    /// Takes the path text between the cursor and <paramref name="end"/>, and
    /// derives the static node type real reports in the target-check messages.
    /// </summary>
    private XmlDmlPath ParsePath(int end)
    {
        this.SkipWhitespace();
        var body = this.text[this.index..end].Trim();
        this.index = end;
        if (body.Length == 0)
            throw this.SyntaxError();

        // Only a positional predicate over the whole path makes it singular;
        // a predicate on an inner step (`/r/a[1]/text()`) leaves the static
        // type plural, which is what real reports.
        var analyzed = body;
        var singleton = false;
        while (analyzed.Length > 1 && analyzed[0] == '(' && MatchingParen(analyzed) is var close && close > 0)
        {
            var trailer = analyzed[(close + 1)..].TrimStart();
            if (trailer.Length == 0)
            {
                analyzed = analyzed[1..close].Trim();
                continue;
            }
            if (trailer[0] != '[' || trailer[^1] != ']')
                break;
            singleton = true;
            analyzed = analyzed[1..close].Trim();
        }

        var compiled = XmlQueryEngine.CompileBody(body, this.defaultNamespace, this.prefixes, "modify", this.Typing);
        if (analyzed == ".")
            return new XmlDmlPath(body, compiled, XmlDmlNodeKind.Document, string.Empty, singleton: true);

        var (kind, name) = ClassifyStep(LastStep(analyzed));
        return new XmlDmlPath(body, compiled, kind, name, singleton);
    }

    /// <summary>Index of the <c>)</c> closing the parenthesis at position 0, or -1.</summary>
    private static int MatchingParen(string body)
    {
        var depth = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(')
                depth++;
            else if (body[i] == ')' && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>The path's final step, ignoring <c>/</c> inside parentheses or predicates.</summary>
    private static string LastStep(string path)
    {
        var depth = 0;
        for (var i = path.Length - 1; i >= 0; i--)
        {
            var c = path[i];
            if (c is ')' or ']')
                depth++;
            else if (c is '(' or '[')
                depth--;
            else if (c == '/' && depth == 0)
                return path[(i + 1)..].Trim();
        }
        return path.Trim();
    }

    /// <summary>Maps a final step to the node kind and name real names in its target-check message.</summary>
    private static (XmlDmlNodeKind Kind, string Name) ClassifyStep(string step)
    {
        var bracket = step.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
            step = step[..bracket].TrimEnd();
        if (step.StartsWith('@'))
        {
            var attribute = step[1..];
            var colon = attribute.LastIndexOf(':');
            return (XmlDmlNodeKind.Attribute, colon >= 0 ? attribute[(colon + 1)..] : attribute);
        }
        if (step.StartsWith("text(", StringComparison.Ordinal))
            return (XmlDmlNodeKind.Text, string.Empty);
        if (step.StartsWith("comment(", StringComparison.Ordinal))
            return (XmlDmlNodeKind.Comment, string.Empty);
        if (step.StartsWith("processing-instruction(", StringComparison.Ordinal))
            return (XmlDmlNodeKind.ProcessingInstruction, string.Empty);
        var localColon = step.LastIndexOf(':');
        return (XmlDmlNodeKind.Element, localColon >= 0 ? step[(localColon + 1)..] : step);
    }

    /// <summary>The next word starting at the cursor, without consuming it.</summary>
    private string PeekWord()
    {
        var end = this.index;
        while (end < this.text.Length && (char.IsLetter(this.text[end]) || this.text[end] is '-' or ':'))
            end++;
        return this.text[this.index..end];
    }

    /// <summary>Consumes <paramref name="word"/> when it sits at the cursor as a whole word.</summary>
    private bool TryKeyword(string word)
    {
        this.SkipWhitespace();
        if (!this.PeekWord().Equals(word, StringComparison.Ordinal))
            return false;
        this.index += word.Length;
        return true;
    }

    /// <summary>
    /// Finds <paramref name="word"/> as a whole word outside quotes, braces,
    /// parentheses and markup — how the <c>with</c> that splits
    /// <c>replace value of</c> is located.
    /// </summary>
    private int FindKeyword(string word, int from = -1)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = this.index; i < this.text.Length; i++)
        {
            var c = this.text[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }
            if (c is '(' or '[' or '{' or '<')
            {
                depth++;
                continue;
            }
            if (c is ')' or ']' or '}' or '>')
            {
                depth--;
                continue;
            }
            if (depth != 0 || i < from || c != word[0] || !this.text.AsSpan(i).StartsWith(word, StringComparison.Ordinal))
                continue;
            if ((i > 0 && IsWordChar(this.text[i - 1])) || (i + word.Length < this.text.Length && IsWordChar(this.text[i + word.Length])))
                continue;
            return i;
        }
        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':';

    private char Current => this.index < this.text.Length ? this.text[this.index] : '\0';

    private char Peek(int offset) => this.index + offset < this.text.Length ? this.text[this.index + offset] : '\0';

    private void SkipWhitespace()
    {
        while (this.index < this.text.Length && char.IsWhiteSpace(this.text[this.index]))
            this.index++;
    }

    /// <summary>
    /// Msg 2209 naming the token at the cursor — real quotes the offending
    /// word, or <c>&lt;eof&gt;</c> when the text ran out.
    /// </summary>
    private SimulatedSqlException SyntaxError()
    {
        this.SkipWhitespace();
        if (this.index >= this.text.Length)
            return SimulatedSqlException.XmlDmlSyntaxError("<eof>");
        var word = this.PeekWord();
        return SimulatedSqlException.XmlDmlSyntaxError(word.Length > 0 ? word : this.text[this.index].ToString());
    }
}
