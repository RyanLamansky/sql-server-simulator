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
    /// <strong>Msg 6602</strong>, with the handle slot and the return code both
    /// left untouched — matching what real leaves behind when the error is
    /// caught (an uncaught one, which real reports without aborting the batch,
    /// does set the code to 1).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpXmlPrepareDocument(BatchContext batch, string? returnCodeVariableName)
    {
        var args = ParseXmlDocumentProcArguments(batch, isRemove: false);
        if (batch.IsSkipping)
            yield break;

        static string? DocumentText(SqlValue value) =>
            value.IsNull ? null : value.CoerceTo(SqlType.NVarcharMax).AsString;

        var document = PreparedXmlDocument.Parse(
            args.HasText ? DocumentText(args.Text) : null,
            args.HasNamespaces ? DocumentText(args.Namespaces) : null);

        var handle = batch.Connection.NextPreparedXmlHandle();
        batch.Connection.PreparedXmlDocuments[handle] = document;
        if (args.HandleSlot is { } slot)
            slot.Value = SqlValue.FromInt32(handle).CoerceTo(slot.DeclaredType);
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>
    /// <c>EXEC @rc = sp_xml_removedocument @hdoc</c>. Releases the handle; one
    /// this session never held — including one it already released — is
    /// <strong>Msg 8179</strong>, which leaves the return code unwritten.
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
            throw SimulatedSqlException.CouldNotFindPreparedStatement(handle);
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>Writes a system procedure's return code into the caller's <c>EXEC @rc =</c> slot, if it wrote one.</summary>
    private static void SetProcedureReturnCode(BatchContext batch, string? returnCodeVariableName, int code)
    {
        if (returnCodeVariableName is null)
            return;
        var slot = batch.GetVariableSlot(returnCodeVariableName);
        slot.Value = SqlValue.FromInt32(code).CoerceTo(slot.DeclaredType);
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
