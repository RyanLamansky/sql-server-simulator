using System.Text;

namespace SqlServerSimulator.Parser.FullText;

/// <summary>One row of <c>sys.dm_fts_parser</c>.</summary>
internal readonly struct FullTextParserRow(byte[] keyword, int groupId, int occurrence, string specialTerm, string displayTerm, int expansionType, string source)
{
    public readonly byte[] Keyword = keyword;
    public readonly int GroupId = groupId;
    public readonly int Occurrence = occurrence;
    public readonly string SpecialTerm = specialTerm;
    public readonly string DisplayTerm = displayTerm;
    public readonly int ExpansionType = expansionType;
    public readonly string Source = source;
}

/// <summary>
/// Collects what <c>sys.dm_fts_parser</c> reports for a condition: each leaf
/// of the condition — a word, a phrase, one <c>FORMSOF</c> argument — as a
/// group, numbered in the order written, listing the terms the word breaker
/// produced at their occurrences, the sentence and paragraph markers between
/// them, and an inflectional leaf's expansions ahead of each word they grow
/// from. Probed 2026-09-29 against SQL Server 2025.
/// </summary>
internal sealed class FullTextParserReport
{
    public readonly List<FullTextParserRow> Rows = [];
    private int groupId;

    /// <summary>The keyword real reports for a sentence or paragraph marker.</summary>
    private static readonly byte[] MarkerKeyword = [0xFF];

    public void AddLeaf(string source, List<FullTextTerm> terms, bool expand, FullTextLanguage? stoplist)
    {
        this.groupId++;
        foreach (var term in terms)
        {
            switch (term.Kind)
            {
                case FullTextTermKind.EndOfSentence:
                    this.Rows.Add(new FullTextParserRow(MarkerKeyword, this.groupId, term.Position, "End Of Sentence", "END OF FILE", 0, source));
                    continue;
                case FullTextTermKind.EndOfParagraph:
                    this.Rows.Add(new FullTextParserRow(MarkerKeyword, this.groupId, term.Position, "End of Paragraph", "END OF FILE", 0, source));
                    continue;
                default:
                    break;
            }
            var noise = stoplist is not null && stoplist.IsNoise(term.Text);
            if (expand && (!noise || FullTextLexicon.ExpandsAsNoise(term.Text)))
            {
                foreach (var form in FullTextLexicon.InflectionalForms(term.Text))
                    this.Rows.Add(new FullTextParserRow(Encoding.BigEndianUnicode.GetBytes(form), this.groupId, term.Position, "Exact Match", form, 2, source));
            }
            this.Rows.Add(new FullTextParserRow(Encoding.BigEndianUnicode.GetBytes(term.Text), this.groupId, term.Position, noise ? "Noise Word" : "Exact Match", term.Text, 0, source));
        }
    }
}
