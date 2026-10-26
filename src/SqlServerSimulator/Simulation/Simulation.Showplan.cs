using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// SET SHOWPLAN_TEXT / _ALL / _XML: a batch compiled without running, which
// answers its statements' plans in place of their results.
partial class Simulation
{
    /// <summary>
    /// The ShowPlan <c>StatementType</c> real gives the statement at
    /// <paramref name="context"/>'s cursor when it compiles no query plan for
    /// it, read off its leading tokens (probed 2026-10-26 against SQL Server
    /// 2025); a variable's <c>DECLARE</c> is <see cref="DeclareType"/>, which
    /// its text settles once it has parsed. A statement holding a query takes
    /// the type its Query Store shape reads instead.
    /// </summary>
    private static string? ShowplanStatementType(ParserContext context)
    {
        var next = PeekAfterLead(context);
        return context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Begin } => next is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed }
                ? "BEGIN TRANSACTION"
                : $"BEGIN {next?.ErrorText.ToUpperInvariant()}",
            ReservedKeyword { Keyword: Keyword.Commit } => "COMMIT TRANSACTION",
            ReservedKeyword { Keyword: Keyword.Create or Keyword.Alter or Keyword.Drop } lead when next is ReservedKeyword or UnquotedString
                => $"{lead.ErrorText.ToUpperInvariant()} {next.ErrorText.ToUpperInvariant()}",
            ReservedKeyword { Keyword: Keyword.Declare } => next is AtPrefixedString ? DeclareType : "DECLARE CURSOR",
            ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute } => "EXECUTE PROC",
            ReservedKeyword { Keyword: Keyword.Rollback } => "ROLLBACK TRANSACTION",
            ReservedKeyword { Keyword: Keyword.Save } => "SAVE TRANSACTION",
            ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' } => "SELECT WITHOUT QUERY",
            ReservedKeyword { Keyword: Keyword.Set } => next is AtPrefixedString ? "ASSIGN" : "SET ON/OFF",
            ReservedKeyword { Keyword: Keyword.Truncate } => "TRUNCATE TABLE",
            ReservedKeyword { Keyword: Keyword.Use } => "USE DATABASE",
            { } lead => lead.ErrorText.ToUpperInvariant(),
            null => null,
        };
    }

    /// <summary>
    /// A variable's <c>DECLARE</c>, which real plans as an <c>ASSIGN</c> when
    /// it initializes one and leaves out otherwise, folding its text into a
    /// neighbor's (probed 2026-10-26 against SQL Server 2025).
    /// </summary>
    private const string DeclareType = "DECLARE";

    /// <summary>
    /// Records the statement starting at <paramref name="start"/>, which
    /// <paramref name="batch"/> walked under <c>SET SHOWPLAN_*</c> without
    /// error, under <paramref name="type"/>.
    /// </summary>
    private static void NoteShowplanStatement(BatchContext batch, Token start, string type)
    {
        var text = batch.Parser.Command.CommandText;
        var (tight, _) = StatementEndsOf(batch.Parser, start.StartIndex);
        var statement = text[start.StartIndex..(tight >= start.StartIndex ? tight + 1 : text.Length)];
        if (type == DeclareType)
        {
            // An initializer is the one '=' a declaration list holds outside
            // parentheses.
            var depth = 0;
            var initializes = false;
            foreach (var character in statement)
            {
                depth += character switch { '(' => 1, ')' => -1, _ => 0 };
                if (initializes = depth == 0 && character == '=')
                    break;
            }
            if (!initializes)
                return;
            type = "ASSIGN";
        }
        batch.ShowplanStatements!.Add(new ShowplanStatement(statement, type, batch.CurrentDatabase));
    }

    /// <summary>
    /// What a batch <paramref name="batch"/> walked under <c>SET SHOWPLAN_*</c>
    /// answers in place of running <paramref name="statements"/>, as real does
    /// (probed 2026-10-26 against SQL Server 2025): under <c>SHOWPLAN_XML</c>
    /// one row holding the batch's ShowPlan document, a statement element
    /// each; under <c>SHOWPLAN_TEXT</c> each statement holding a query's text
    /// in a result of its own and the statements between them gathered in
    /// one; under <c>SHOWPLAN_ALL</c> a row each. Real's operator rows and
    /// elements, its optimizer's estimates, are left out, the simulator
    /// having no optimizer whose plan they could describe.
    /// </summary>
    private List<SimulatedStatementOutcome> ShowplanResults(BatchContext batch, List<ShowplanStatement> statements)
    {
        var connection = batch.Connection;
        var text = NVarcharSqlType.Get(SqlType.MaxLengthSentinel, batch.CurrentDatabase.Collation, Coercibility.Implicit);
        var shapes = new QueryStoreShape[statements.Count];
        for (var i = 0; i < statements.Count; i++)
            shapes[i] = this.QueryStoreShapeOf(statements[i].Text, statements[i].Database, parser: null, tokensFrom: null);
        string TypeOf(int i) => shapes[i].Captured ? shapes[i].StatementType : statements[i].Type;

        switch (connection.Showplan)
        {
            case ShowplanMode.Xml:
                {
                    var xml = OpenShowPlan();
                    var setOptions = SetOptionsOf(connection, connection.QuotedIdentifiers);
                    for (var i = 0; i < statements.Count; i++)
                    {
                        var statement = statements[i];
                        _ = shapes[i].Captured
                            ? AppendShowPlanStatement(xml, statement.Text, i + 1, shapes[i], StatementSqlHandleOf(shapes[i].Text), contextSettingsId: null,
                                parentObjectId: 0, parameterizationType: 0, retrievedFromCache: false, (short)statement.Database.CompatibilityLevel, setOptions)
                            : xml.Append(CultureInfo.InvariantCulture, $"<StmtSimple StatementText=\"{EscapeShowPlanAttribute(statement.Text)}\" StatementId=\"{i + 1}\" StatementCompId=\"{i + 1}\" StatementType=\"{statement.Type}\" RetrievedFromCache=\"false\"></StmtSimple>");
                    }
                    var document = xml.Append(ShowPlanClose).ToString();
                    return [new SimulatedSqlResultSet([text], ["Microsoft SQL Server 2005 XML Showplan"], [[SqlValue.FromNVarchar(document)]])];
                }
            case ShowplanMode.Text:
                {
                    var results = new List<SimulatedStatementOutcome>();
                    List<SqlValue[]>? between = null;
                    for (var i = 0; i < statements.Count; i++)
                    {
                        SqlValue[] row = [SqlValue.FromNVarchar(statements[i].Text)];
                        if (!shapes[i].Captured)
                        {
                            (between ??= []).Add(row);
                            continue;
                        }
                        if (between is not null)
                            results.Add(new SimulatedSqlResultSet([text], ["StmtText"], between));
                        between = null;
                        results.Add(new SimulatedSqlResultSet([text], ["StmtText"], [row]));
                    }
                    if (between is not null)
                        results.Add(new SimulatedSqlResultSet([text], ["StmtText"], between));
                    return results;
                }
            default:
                {
                    SqlType[] schema = [text, SqlType.Int32, SqlType.Int32, SqlType.Int32, text, text, text, text, SqlType.Real, SqlType.Real, SqlType.Real,
                        SqlType.Int32, SqlType.Real, text, text, text, SqlType.Bit, SqlType.Real];
                    var rows = new List<SqlValue[]>(statements.Count);
                    var noText = SqlValue.Null(text);
                    var noNumber = SqlValue.Null(SqlType.Real);
                    for (var i = 0; i < statements.Count; i++)
                    {
                        var id = (i + 1).ToString(CultureInfo.InvariantCulture);
                        rows.Add([SqlValue.FromNVarchar(statements[i].Text), SqlValue.FromInt32(i + 1), SqlValue.FromInt32(i + 1), SqlValue.FromInt32(0),
                            noText, noText, SqlValue.FromNVarchar(id), noText, noNumber, noNumber, noNumber, SqlValue.Null(SqlType.Int32), noNumber, noText, noText,
                            SqlValue.FromNVarchar(TypeOf(i)), SqlValue.FromBoolean(false), noNumber]);
                    }
                    return [new SimulatedSqlResultSet(schema, ShowplanAllColumnNames, rows)];
                }
        }
    }

    private static readonly string[] ShowplanAllColumnNames =
    [
        "StmtText", "StmtId", "NodeId", "Parent", "PhysicalOp", "LogicalOp", "Argument", "DefinedValues", "EstimateRows", "EstimateIO",
        "EstimateCPU", "AvgRowSize", "TotalSubtreeCost", "OutputList", "Warnings", "Type", "Parallel", "EstimateExecutions",
    ];
}

/// <summary>Which <c>SET SHOWPLAN_*</c> switch a session has on, if any.</summary>
internal enum ShowplanMode : byte
{
    None,
    Text,
    All,
    Xml,
}

/// <summary>
/// A statement a batch walked under <c>SET SHOWPLAN_*</c>: its text, the
/// ShowPlan type it takes when it holds no query, and the database it
/// compiled in.
/// </summary>
internal readonly struct ShowplanStatement(string text, string type, Database database)
{
    public readonly string Text = text;
    public readonly string Type = type;
    public readonly Database Database = database;
}
