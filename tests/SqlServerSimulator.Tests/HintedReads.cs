using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlServerSimulator;

/// <summary>
/// Holds a query the plan honors its hints for to what it answers without
/// them: a table, join or query hint chooses how a statement reads, never
/// what it returns or changes.
/// </summary>
static partial class HintedReads
{
    /// <summary>
    /// Asserts <paramref name="query"/>, run on one fresh simulation, returns
    /// the rows and affects the count it does with its hints stripped, run on
    /// another; a query whose hints sit where the stripping can't see them, in
    /// a view or function it reads, is only run.
    /// </summary>
    public static void AssertAnswersAsUnhinted(Func<Simulation> fresh, string query)
    {
        var unhinted = JoinHint.Replace(OptionClause.Replace(TableHints.Replace(query, ""), ""), "join");
        var answer = Answer(fresh(), query);
        if (unhinted != query)
            CollectionAssert.AreEqual(Answer(fresh(), unhinted), answer);
    }

    /// <summary>Each result set's rows, sorted since no order is asked for, then the count of rows affected.</summary>
    private static List<string> Answer(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(sql);
        using var reader = command.ExecuteReader();
        var answer = new List<string>();
        do
        {
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                _ = reader.GetValues(values);
                rows.Add(string.Join("|", values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))));
            }
            rows.Sort(StringComparer.Ordinal);
            answer.AddRange(rows);
            answer.Add("--");
        }
        while (reader.NextResult());
        reader.Close();
        answer.Add(reader.RecordsAffected.ToString(CultureInfo.InvariantCulture));
        return answer;
    }

    /// <summary><c>WITH (…)</c> table hints, two levels of parentheses deep: <c>index(ix, iy)</c>, <c>forceseek(ix(a, b))</c>.</summary>
    [GeneratedRegex(@"\s+with\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TableHints { get; }

    [GeneratedRegex(@"\s+option\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)", RegexOptions.IgnoreCase)]
    private static partial Regex OptionClause { get; }

    [GeneratedRegex(@"\b(?:hash|merge|loop|remote)\s+join\b", RegexOptions.IgnoreCase)]
    private static partial Regex JoinHint { get; }
}
