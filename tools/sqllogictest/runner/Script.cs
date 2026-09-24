using System.Text;

namespace SltRunner;

/// <summary>One statement/query record from a .test script.</summary>
sealed class SltRecord
{
    public string Kind = "";              // "statement" | "query"
    public bool ExpectOk;                 // statement records
    public string TypeString = "";        // query records: I/R/T chars
    public string SortMode = "nosort";
    public string Label = "";
    public string Sql = "";
    public List<string> Expected = new(); // literal expected values (non-hash form)
    public int ExpectedValueCount = -1;   // >= 0 when the hash form was used
    public string ExpectedHash = "";
    public int Line;
    public List<string> SkipIf = new();
    public List<string> OnlyIf = new();
    public int HashThreshold;
}

static class ScriptParser
{
    const string Engine = "mssql";

    /// <summary>True when the engine conditionals let this record run for us.</summary>
    public static bool AppliesToUs(SltRecord r)
    {
        foreach (var s in r.SkipIf)
            if (string.Equals(s, Engine, StringComparison.OrdinalIgnoreCase))
                return false;
        if (r.OnlyIf.Count > 0)
        {
            foreach (var s in r.OnlyIf)
                if (string.Equals(s, Engine, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        return true;
    }

    public static bool IgnoreHalt;

    public static List<SltRecord> Parse(string path)
    {
        var lines = File.ReadAllLines(path);
        var records = new List<SltRecord>();
        var threshold = 8;
        var i = 0;

        static bool IsComment(string s) => s.StartsWith('#');

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Length == 0 || line.Trim().Length == 0 || IsComment(line)) { i++; continue; }

            var skipIf = new List<string>();
            var onlyIf = new List<string>();
            while (i < lines.Length)
            {
                var l = lines[i];
                if (IsComment(l)) { i++; continue; }
                if (l.StartsWith("skipif ", StringComparison.Ordinal)) { skipIf.Add(Token(l, 1)); i++; continue; }
                if (l.StartsWith("onlyif ", StringComparison.Ordinal)) { onlyIf.Add(Token(l, 1)); i++; continue; }
                break;
            }
            if (i >= lines.Length) break;
            line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            if (line.StartsWith("halt", StringComparison.Ordinal))
            {
                if (!IgnoreHalt) break;
                i++;
                continue;
            }

            if (line.StartsWith("hash-threshold", StringComparison.Ordinal))
            {
                threshold = int.TryParse(Token(line, 1), out var t) ? t : threshold;
                i++;
                continue;
            }

            if (line.StartsWith("statement", StringComparison.Ordinal))
            {
                var rec = new SltRecord
                {
                    Kind = "statement",
                    ExpectOk = Token(line, 1) == "ok",
                    Line = i + 1,
                    SkipIf = skipIf,
                    OnlyIf = onlyIf,
                    HashThreshold = threshold,
                };
                i++;
                rec.Sql = ReadSql(lines, ref i, stopAtDashes: false);
                if (rec.Sql.Length > 0) records.Add(rec);
                continue;
            }

            if (line.StartsWith("query", StringComparison.Ordinal))
            {
                var tok = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
                var rec = new SltRecord
                {
                    Kind = "query",
                    TypeString = tok.Length > 1 ? tok[1] : "",
                    SortMode = tok.Length > 2 ? tok[2] : "nosort",
                    Label = tok.Length > 3 ? tok[3] : "",
                    Line = i + 1,
                    SkipIf = skipIf,
                    OnlyIf = onlyIf,
                    HashThreshold = threshold,
                };
                i++;
                rec.Sql = ReadSql(lines, ref i, stopAtDashes: true);
                // If we stopped on "----", consume results.
                if (i < lines.Length && lines[i].StartsWith("----", StringComparison.Ordinal))
                {
                    i++;
                    while (i < lines.Length && lines[i].Trim().Length > 0)
                    {
                        rec.Expected.Add(lines[i]);
                        i++;
                    }
                }
                if (rec.Expected.Count == 1)
                {
                    var m = rec.Expected[0];
                    var idx = m.IndexOf(" values hashing to ", StringComparison.Ordinal);
                    if (idx > 0 && int.TryParse(m.AsSpan(0, idx), out var n))
                    {
                        rec.ExpectedValueCount = n;
                        rec.ExpectedHash = m[(idx + 19)..].Trim();
                        rec.Expected.Clear();
                    }
                }
                if (rec.Sql.Length > 0) records.Add(rec);
                continue;
            }

            // Unknown control line: skip.
            i++;
        }

        return records;
    }

    static string Token(string line, int index)
    {
        var parts = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index] : "";
    }

    static string ReadSql(string[] lines, ref int i, bool stopAtDashes)
    {
        var sb = new StringBuilder();
        while (i < lines.Length)
        {
            var l = lines[i];
            if (l.Trim().Length == 0) break;
            if (stopAtDashes && l.StartsWith("----", StringComparison.Ordinal)) break;
            if (IsCommentLine(l)) { i++; continue; }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l);
            i++;
        }
        return sb.ToString().Trim();
    }

    static bool IsCommentLine(string s) => s.StartsWith('#');
}
