using System.Text;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// sp_MSforeachtable — run one to three command templates once per user table,
// substituting the table's bracketed two-part name for each @replacechar
// occurrence. Real drives this from a global cursor consumed by
// sp_MSforeach_worker; the simulator materializes the same table list up front
// and reuses the dynamic-SQL batch path for every command it runs, so a
// command's result sets, row counts and errors behave exactly as they do under
// EXEC(). Probe-confirmed against SQL Server 2025 (2026-07-31).
//
// This file is the deliberate exception to the system-proc convention of
// building result rows in C# from catalog state (CatalogProcs.cs and the
// HelpProcs family are the norm). The justification is @whereand: its contract
// is a raw SQL fragment concatenated into the cursor query, binding against
// the `o` alias — an arbitrary fragment (subqueries, functions, nested
// predicates) needs a real query to graft onto and the full engine to
// evaluate it, which is exactly what ExecuteDynamicBatch provides. A proc
// whose parameters are values rather than SQL text gets no such license —
// don't copy this shape.
//
// The cost accepted here: ForEachTableNames' embedded query makes this proc a
// hidden internal consumer of the public SQL surface (sysobjects /
// sys.all_objects / OBJECTPROPERTY / schema_name / REPLACE), so a change to
// any of those can break it at a distance. SystemInfoProcTests' foreachtable
// tests are the tripwire.
partial class Simulation
{
    /// <summary>
    /// Handles <c>EXEC sp_MSforeachtable @command1 [, @replacechar]
    /// [, @command2] [, @command3] [, @whereand] [, @precommand]
    /// [, @postcommand]</c>. <c>@precommand</c> runs once first,
    /// <c>@command1</c> / <c>@command2</c> / <c>@command3</c> run once per
    /// table in that order, and <c>@postcommand</c> runs once last; every one
    /// of them is dispatched as its own dynamic batch, so each yields its own
    /// result sets to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table list is the one real's cursor selects — every object
    /// <c>OBJECTPROPERTY(id, 'IsUserTable')</c> reports 1 for whose
    /// <c>sysobjects.category</c> carries no MS-shipped bit — and
    /// <c>@whereand</c> is appended to that query verbatim, so the usual
    /// <c>'and o.name like ''…'''</c> filters bind against the same <c>o</c>
    /// alias real exposes. The one adaptation is the object table's name:
    /// real reads <c>dbo.sysobjects</c>, the simulator registers the legacy
    /// view unqualified and under <c>sys</c>, so the generated query says
    /// <c>sysobjects o</c>.
    /// </para>
    /// <para>
    /// Substitution follows real's rules for the character preceding
    /// <c>@replacechar</c>: a <c>'</c> doubles every quote in the substituted
    /// name and a <c>[</c> doubles every closing bracket; anywhere else the
    /// already-bracketed <c>[schema].[table]</c> goes in as-is. Real's own
    /// worker re-escapes a bracketed name a second time in that last case, so
    /// a table whose name contains <c>]</c> comes back over-escaped there and
    /// correctly escaped here.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpMsForEachTable(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (command1, replaceChar, command2, command3, whereAnd, preCommand, postCommand) =
            ParseSpMsForEachTableArgs(arguments);
        if (command1 is null)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_MSforeachtable", "command1");

        if (preCommand is not null)
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, preCommand, preDeclaredVariables: null))
                yield return outcome;
        }

        string?[] commands = [command1, command2, command3];
        foreach (var tableName in this.ForEachTableNames(batch, whereAnd))
        {
            foreach (var command in commands)
            {
                if (command is null)
                    continue;
                var expanded = ForEachSubstitute(command, replaceChar, tableName, bare: tableName);
                foreach (var outcome in this.ExecuteDynamicBatch(batch, expanded, preDeclaredVariables: null))
                    yield return outcome;
            }
        }

        if (postCommand is null)
            yield break;
        foreach (var outcome in this.ExecuteDynamicBatch(batch, postCommand, preDeclaredVariables: null))
            yield return outcome;
    }

    // Real's cursor query, run through the ordinary dynamic-SQL path so
    // @whereand binds against the same catalog surface a caller would write it
    // against. The rows are consumed here rather than yielded on.
    private List<string> ForEachTableNames(BatchContext batch, string? whereAnd)
    {
        const string cursorQuery = """
            select '[' + REPLACE(schema_name(syso.schema_id), N']', N']]') + ']' + '.'
                 + '[' + REPLACE(object_name(o.id), N']', N']]') + ']'
            from sysobjects o join sys.all_objects syso on o.id = syso.object_id
            where OBJECTPROPERTY(o.id, N'IsUserTable') = 1 and o.category & 2 = 0
            """;
        var names = new List<string>();
        foreach (var outcome in this.ExecuteDynamicBatch(batch, cursorQuery + " " + whereAnd, preDeclaredVariables: null))
        {
            if (outcome is not SimulatedSqlResultSet resultSet)
                continue;
            var cursor = resultSet.CreateCursor();
            while (cursor.MoveNext())
                names.Add(cursor[0].AsString);
        }

        return names;
    }

    // One pass over the template, replacing each @replacechar occurrence with
    // the name escaped for the context the character sits in — real's worker
    // rules, shared by sp_MSforeachtable and sp_MSforeachdb. `bare` is what an
    // occurrence outside quotes and brackets substitutes: a table name arrives
    // already bracketed so it goes in as-is, while a database name is a plain
    // identifier the worker QUOTENAMEs.
    private static string ForEachSubstitute(string command, char replaceChar, string name, string bare)
    {
        var quoted = name.Replace("'", "''", StringComparison.Ordinal);
        var bracketed = name.Replace("]", "]]", StringComparison.Ordinal);
        var text = new StringBuilder(command.Length);
        for (var i = 0; i < command.Length; i++)
        {
            if (command[i] != replaceChar)
            {
                _ = text.Append(command[i]);
                continue;
            }

            var previous = i > 0 ? command[i - 1] : '\0';
            _ = text.Append(previous switch
            {
                '\'' => quoted,
                '[' => bracketed,
                _ => bare,
            });
        }

        return text.ToString();
    }

    private static (string? Command1, char ReplaceChar, string? Command2, string? Command3,
        string? WhereAnd, string? PreCommand, string? PostCommand) ParseSpMsForEachTableArgs(
        List<ProcArgument> arguments)
    {
        string? command1 = null, command2 = null, command3 = null, whereAnd = null, preCommand = null, postCommand = null;
        var replaceChar = '?';
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: command1 = CatalogStringArg(arg); break;
                    case 1: replaceChar = ForEachReplaceChar(arg, replaceChar); break;
                    case 2: command2 = CatalogStringArg(arg); break;
                    case 3: command3 = CatalogStringArg(arg); break;
                    case 4: whereAnd = CatalogStringArg(arg); break;
                    case 5: preCommand = CatalogStringArg(arg); break;
                    case 6: postCommand = CatalogStringArg(arg); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_MSforeachtable");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "command1"): command1 = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "replacechar"): replaceChar = ForEachReplaceChar(arg, replaceChar); break;
                case var n when BuiltInToken.Equals(n, "command2"): command2 = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "command3"): command3 = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "whereand"): whereAnd = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "precommand"): preCommand = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "postcommand"): postCommand = CatalogStringArg(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_MSforeachtable");
            }
        }

        return (command1, replaceChar, command2, command3, whereAnd, preCommand, postCommand);
    }

    // @replacechar is declared nchar(1); a NULL or empty argument keeps the
    // declared '?' default.
    private static char ForEachReplaceChar(ProcArgument arg, char fallback) =>
        CatalogStringArg(arg) is { Length: > 0 } value ? value[0] : fallback;
}
