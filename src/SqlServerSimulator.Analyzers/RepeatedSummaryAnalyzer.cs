using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a documentation comment holding a second <c>&lt;summary&gt;</c>. The
/// shape is what a member inserted between another member's documentation and
/// its declaration leaves behind: the displaced text now sits above the new
/// member, which carries two summaries — one of them describing something
/// else — while the member it was written for has none. Nothing else notices:
/// the compiler concatenates the two, and the generated XML reads as one
/// confusing summary.
/// </summary>
/// <remarks>
/// The fix is to move the displaced block back above the member it describes,
/// or to delete it when that member is gone. A comment meant to carry more
/// than one paragraph uses <c>&lt;para&gt;</c> inside one summary, or
/// <c>&lt;remarks&gt;</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepeatedSummaryAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS016",
        title: "Documentation comment with a second <summary>",
        messageFormat: "This documentation comment has a second <summary>; the first likely belongs to another member — move it above the member it describes, or delete it if that member is gone",
        category: "Documentation",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A documentation comment with two <summary> elements is almost always a member inserted between another member's documentation and its declaration: the displaced summary now describes the wrong member, and the member it was written for has none. Move the displaced block back above its member, or delete it if that member no longer exists; several paragraphs of one summary use <para>, and further detail goes in <remarks>.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeTree);
    }

    private static void AnalyzeTree(SyntaxTreeAnalysisContext context)
    {
        var root = context.Tree.GetRoot(context.CancellationToken);
        var summaries = 0;
        foreach (var line in context.Tree.GetText(context.CancellationToken).Lines)
        {
            var text = line.ToString();
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
            var isDocLine = string.CompareOrdinal(text, start, "///", 0, 3) == 0
                && (start + 3 == text.Length || text[start + 3] != '/')
                && IsComment(root.FindTrivia(line.Start + start, findInsideTrivia: true));
            if (!isDocLine)
            {
                summaries = 0;
                continue;
            }

            for (var at = text.IndexOf("<summary>", start, System.StringComparison.Ordinal); at >= 0; at = text.IndexOf("<summary>", at + 1, System.StringComparison.Ordinal))
            {
                if (++summaries == 2)
                    context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(context.Tree, new Microsoft.CodeAnalysis.Text.TextSpan(line.Start + at, "<summary>".Length))));
            }
        }
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
        || trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.DocumentationCommentExteriorTrivia);
}
