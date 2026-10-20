using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_xml_preparedocument / sp_xml_removedocument: the session's
// prepared-document store, which OPENXML reads. Behavior is probe-confirmed
// against SQL Server 2025; the deep-dive is in docs/claude/xml.md.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
public partial class Simulation
{
    /// <summary>
    /// <c>EXEC @rc = sp_xml_preparedocument @hdoc OUTPUT, @xmltext
    /// [, @xpath_namespaces]</c>. Parses the document, files it under a fresh
    /// session handle, and writes that handle back through the OUTPUT slot.
    /// An omitted or NULL <c>@xmltext</c> still allocates a handle over an
    /// empty document (probe-confirmed). A document that won't parse is
    /// <strong>Msg 6602</strong>, with the handle slot left untouched and the
    /// return code 1 — or untouched too when a <c>TRY</c> catches the error
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpXmlPrepareDocument(BatchContext batch, string? returnCodeVariableName)
    {
        var args = ParseXmlDocumentProcArguments(batch, isRemove: false);
        if (batch.IsSkipping)
            yield break;

        static string? DocumentText(SqlValue value) =>
            value.IsNull ? null : value.CoerceTo(SqlType.NVarcharMax).AsString;

        // A document that fails still uses up its handle (probed 2026-10-20
        // against SQL Server 2025).
        var handle = batch.Connection.NextPreparedXmlHandle();
        PreparedXmlDocument document;
        try
        {
            document = PreparedXmlDocument.Parse(
                batch,
                args.HasText ? DocumentText(args.Text) : null,
                args.HasNamespaces ? DocumentText(args.Namespaces) : null);
        }
        catch (SimulatedSqlException) when (batch.Connection.OpenTryFrames == 0)
        {
            SetReturnCode(batch, returnCodeVariableName, 1);
            throw;
        }

        batch.Connection.PreparedXmlDocuments[handle] = document;
        if (args.HandleSlot is { } slot)
            slot.Value = SqlValue.FromInt32(handle).CoerceTo(slot.DeclaredType);
        SetReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>
    /// <c>EXEC @rc = sp_xml_removedocument @hdoc</c>. Releases the handle; one
    /// this session never held — including one it already released — is
    /// <strong>Msg 8179</strong>, returning 1, or leaving the return code
    /// unwritten when a <c>TRY</c> catches it.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpXmlRemoveDocument(BatchContext batch, string? returnCodeVariableName)
    {
        var args = ParseXmlDocumentProcArguments(batch, isRemove: true);
        if (batch.IsSkipping)
            yield break;

        var handle = args.HasHandle && !args.Handle.IsNull
            ? ScalarArguments.CoerceProcedureParameter(args.Handle, SqlType.Int32)
            : 0;
        if (!batch.Connection.PreparedXmlDocuments.TryRemove(handle, out _))
        {
            // The extended procedure raises it as its own (probed 2026-10-17
            // against SQL Server 2025).
            var missing = SimulatedSqlException.CouldNotFindPreparedStatement(handle);
            missing.PreserveDiagnostics(1, "sp_xml_removedocument");
            if (batch.Connection.OpenTryFrames == 0)
                SetReturnCode(batch, returnCodeVariableName, 1);
            throw missing;
        }
        SetReturnCode(batch, returnCodeVariableName, 0);
    }


    // Parsed sp_xml_preparedocument / sp_xml_removedocument arguments.
    // Presence flags are distinct from NULL-ness: an omitted argument has no
    // SqlValue at all, so reading .IsNull off the default would fault.
    private struct XmlDocumentProcArguments
    {
        /// <summary>The <c>@hdoc OUTPUT</c> slot sp_xml_preparedocument writes the new handle into.</summary>
        public VariableSlot? HandleSlot;

        /// <summary>The handle value sp_xml_removedocument was handed.</summary>
        public SqlValue Handle;
        public bool HasHandle;

        public SqlValue Text;
        public bool HasText;
        public SqlValue Namespaces;
        public bool HasNamespaces;
    }

    /// <summary>
    /// Binds positional / named EXEC arguments for the document procs, whose
    /// positional order is (@hdoc, @xmltext, @xpath_namespaces). A
    /// <c>@hdoc</c> arg with an OUTPUT slot is the write-back target; without
    /// one it is an input, which only <c>sp_xml_removedocument</c> reads.
    /// </summary>
    private static XmlDocumentProcArguments ParseXmlDocumentProcArguments(BatchContext batch, bool isRemove)
    {
        var procName = isRemove ? "sp_xml_removedocument" : "sp_xml_preparedocument";
        var arguments = ParseExecArguments(batch.Parser, batch);
        var result = default(XmlDocumentProcArguments);
        var positional = 0;
        foreach (var arg in arguments)
        {
            var parameterName = arg.Name ?? PositionalName(positional++);
            switch (parameterName)
            {
                case var n when BuiltInToken.Equals(n, "hdoc"):
                    if (arg.OutputSlot is { } outputSlot)
                        result.HandleSlot = outputSlot;
                    else
                        (result.Handle, result.HasHandle) = (arg.Value, true);
                    break;
                case var n when !isRemove && BuiltInToken.Equals(n, "xmltext"):
                    (result.Text, result.HasText) = (arg.Value, true);
                    break;
                case var n when !isRemove && BuiltInToken.Equals(n, "xpath_namespaces"):
                    (result.Namespaces, result.HasNamespaces) = (arg.Value, true);
                    break;
                default:
                    throw SimulatedSqlException.InvalidProcedureParameters(procName);
            }
        }
        return result;

        string PositionalName(int index) => index switch
        {
            0 => "hdoc",
            1 when !isRemove => "xmltext",
            2 when !isRemove => "xpath_namespaces",
            _ => throw SimulatedSqlException.InvalidProcedureParameters(procName),
        };
    }
}
