namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// Synthetic type-name leaf produced by folding a multi-word ANSI type
/// synonym — <c>double precision</c>, <c>character varying</c>,
/// <c>national character varying</c>, <c>binary varying</c>,
/// <c>national text</c>, and kin — into the single canonical SQL Server
/// base-type name its constituent word tokens map to.
/// <see cref="Span"/> returns the canonical name (fed to
/// <see cref="Storage.SqlType.GetByName"/>); <see cref="Token.LineNumber"/>,
/// <see cref="Token.Source"/>, and <see cref="Token.ToString"/> still reflect
/// the original source words (the token spans them), so error attribution and
/// definition round-trips are unaffected.
/// </summary>
/// <param name="canonicalName">The single canonical base-type name (e.g. <c>float</c>).</param>
/// <param name="anchor">The first word token of the synonym — supplies the source string and start offset.</param>
/// <param name="endIndex">The end offset (past the last character) of the synonym's last word token.</param>
sealed class SynonymTypeName(string canonicalName, Token anchor, int endIndex)
    : Name(anchor.command, anchor.StartIndex, endIndex - anchor.StartIndex)
{
    public override ReadOnlySpan<char> Span => canonicalName;

    public override string Value => canonicalName;
}
