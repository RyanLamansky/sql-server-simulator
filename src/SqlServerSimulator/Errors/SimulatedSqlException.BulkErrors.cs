using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// What BULK INSERT, OPENROWSET(BULK …) and the ad hoc provider rowsets
// (OPENROWSET(provider …), OPENDATASOURCE) report. Wording probed 2026-09-29
// against SQL Server 2025 on Linux.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 4860: the file a bulk load names can't be read. The state tells the
    /// sites apart: 1 for <c>BULK INSERT</c>'s data file, 3 for a format file,
    /// 4 for an <c>OPENROWSET(BULK …)</c> data file, and 75 for a login
    /// holding no <c>CONTROL SERVER</c>, which the Linux server refuses any
    /// file. Ends the batch, uncatchable by a <c>TRY</c> in the same scope.
    /// </summary>
    internal static SimulatedSqlException BulkFileNotFound(string path, byte state) =>
        new($"Cannot bulk load. The file \"{path}\" does not exist or you don't have file access rights.", 4860, 16, state);

    /// <summary>
    /// Msg 4861: an <c>ERRORFILE</c> (or the <c>.Error.Txt</c> file beside it)
    /// the server can't create — code 80 when the file exists, else code 5.
    /// </summary>
    internal static SimulatedSqlException BulkErrorFileNotOpened(string path, bool exists) =>
        new($"Cannot bulk load because the file \"{path}\" could not be opened. Operating system error code {(exists ? "80(The file exists.)" : "5(Access is denied.)")}.", 4861, 16, 1);

    /// <summary>
    /// A refusal of a format file's contents, which real reads compiling the
    /// statement that names it: it closes the batch's binder report, ends the
    /// batch uncaught by a <c>TRY</c> in its scope, and rolls the transaction
    /// back as under <c>XACT_ABORT</c> (probed 2026-10-25 against SQL Server
    /// 2025).
    /// </summary>
    private static SimulatedSqlException FormatFileRefusal(string message, int number, byte state = 1) =>
        new(message, number, 16, state) { AbortsAsUnderXactAbort = true, RefusedRecompilingDeferred = true, EndsBindReport = true };

    /// <summary>Msg 4820: a non-XML format file whose first token is a number that names no version.</summary>
    internal static SimulatedSqlException BulkFormatFileUnknownVersion(string path) =>
        FormatFileRefusal($"Cannot bulk load. Unknown version of format file \"{path}\".", 4820);

    /// <summary>Msg 4822: a format file declaring no field, or no column for one to load.</summary>
    internal static SimulatedSqlException BulkFormatFileColumnCount(string path) =>
        FormatFileRefusal($"Cannot bulk load. Invalid number of columns in the format file \"{path}\".", 4822);

    /// <summary>Msg 4823: a non-XML field whose order isn't its place in the file.</summary>
    internal static SimulatedSqlException BulkFormatFileColumnNumber(string path) =>
        FormatFileRefusal($"Cannot bulk load. Invalid column number in the format file \"{path}\".", 4823);

    /// <summary>Msg 4824: a non-XML field whose host type is no bcp type name, which is case-sensitive.</summary>
    internal static SimulatedSqlException BulkFormatFileDataType(int field, string path) =>
        FormatFileRefusal($"Cannot bulk load. Invalid data type for column number {field} in the format file \"{path}\".", 4824);

    /// <summary>Msg 4825: a non-XML field whose prefix length isn't 0, 1, 2, 4 or 8.</summary>
    internal static SimulatedSqlException BulkFormatFilePrefix(int field, string path) =>
        FormatFileRefusal($"Cannot bulk load. Invalid prefix for column number {field} in the format file \"{path}\".", 4825);

    /// <summary>Msg 4826: a non-XML field whose length isn't an <c>int</c> of at least 0.</summary>
    internal static SimulatedSqlException BulkFormatFileColumnLength(int field, string path) =>
        FormatFileRefusal($"Cannot bulk load. Invalid column length for column number {field} in the format file \"{path}\".", 4826);

    /// <summary>
    /// Msg 4828: a non-XML field whose server column order isn't an
    /// <c>int</c> of at least 0, or the first of two fields loading one column.
    /// </summary>
    internal static SimulatedSqlException BulkFormatFileDestinationColumn(int field, string path) =>
        FormatFileRefusal($"Cannot bulk load. Invalid destination table column number for source column {field} in the format file \"{path}\".", 4828);

    /// <summary>
    /// Msg 4839: a field's collation that isn't one, followed by Msg 4822 when
    /// the field is the file's first.
    /// </summary>
    internal static SimulatedSqlException BulkFormatFileCollation(int field, string path)
    {
        var refusal = FormatFileRefusal($"Cannot perform the bulk load. Invalid collation name for source column {field} in the format file \"{path}\".", 4839);
        if (field != 1)
            return refusal;
        List<SimulatedError> entries = [.. refusal.Errors, .. BulkFormatFileColumnCount(path).Errors];
        return new(string.Join(Environment.NewLine, entries.Select(entry => entry.Message)), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries))
        {
            AbortsAsUnderXactAbort = true,
            RefusedRecompilingDeferred = true,
            EndsBindReport = true,
        };
    }

    /// <summary>Msg 4855: an XML format file's element where a different one belongs, at its line.</summary>
    internal static SimulatedSqlException BulkFormatFileUnexpectedElement(int line, string path, string element) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": unexpected element \"{element}\".", 4855);

    /// <summary>Msg 4856: an XML format file's element or end tag where neither belongs, at its line.</summary>
    internal static SimulatedSqlException BulkFormatFileUnexpectedInfoItem(int line, string path) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": unexpected info item.", 4856);

    /// <summary>
    /// Msg 4857: an XML format file's attribute its element doesn't take, or
    /// doesn't take for its type, at the element's line; a namespace
    /// declaration is named as the empty string.
    /// </summary>
    internal static SimulatedSqlException BulkFormatFileAttributeNotAllowed(int line, string path, string attribute) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": Attribute \"{attribute}\" could not be specified for this type.", 4857);

    /// <summary>Msg 4858: an XML format file's attribute whose value doesn't read, at its element's line.</summary>
    internal static SimulatedSqlException BulkFormatFileBadAttribute(int line, string path, string value, string attribute) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": bad value {value} for attribute \"{attribute}\".", 4858);

    /// <summary>Msg 4859: an XML format file's element missing an attribute it requires, at its line.</summary>
    internal static SimulatedSqlException BulkFormatFileMissingAttribute(int line, string path, string attribute) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": required attribute \"{attribute}\" is missing.", 4859);

    /// <summary>Msg 4872: an XML <c>FIELD</c> whose <c>ID</c> an earlier one took.</summary>
    internal static SimulatedSqlException BulkFormatFileDuplicateId(int line, string path, string id) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": duplicate element id \"{id}\".", 4872);

    /// <summary>Msg 4873: an XML <c>COLUMN</c> whose <c>SOURCE</c> names no <c>FIELD</c>.</summary>
    internal static SimulatedSqlException BulkFormatFileUnknownIdReference(int line, string path, string id) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": referencing non-existing element id \"{id}\".", 4873);

    /// <summary>Msg 4874: an XML <c>COLUMN</c> whose <c>SOURCE</c> an earlier one named.</summary>
    internal static SimulatedSqlException BulkFormatFileDuplicateIdReference(int line, string path, string id) =>
        FormatFileRefusal($"Line {line} in format file \"{path}\": duplicate element id reference \"{id}\".", 4874);

    /// <summary>
    /// Msg 4862: a format file that is empty — operating system error 38 — or
    /// a non-XML one ending among the tokens its field count promises.
    /// </summary>
    internal static SimulatedSqlException BulkFormatFileIncomplete(string path, bool empty) =>
        FormatFileRefusal($"Cannot bulk load because the file \"{path}\" is incomplete or could not be read. Operating system error code {(empty ? "38(Reached the end of the file.)" : "(null)")}.", 4862);

    /// <summary>
    /// The 9400 family as real's format-file reader raises it, at state 48:
    /// Msg 9422 for text ahead of the root element and Msg 9435 for a document
    /// with none, at the line and character after the last one it read.
    /// </summary>
    internal static SimulatedSqlException BulkFormatFileXmlParsing(bool rootMissing, int line, int character) =>
        FormatFileRefusal($"XML parsing: line {line}, character {character}, {(rootMissing ? "one root element" : "incorrect document syntax")}", rootMissing ? 9435 : 9422, 48);

    /// <summary>Msg 4832: the data file ends inside a row.</summary>
    internal static SimulatedSqlException BulkUnexpectedEndOfFile() =>
        new("Bulk load: An unexpected end of file was encountered in the data file.", 4832, 16, 1);

    /// <summary>Msg 4864: a field that doesn't convert to its column's type.</summary>
    internal static SimulatedSqlException BulkConversionTypeMismatch(long row, int column, string columnName) =>
        new($"Bulk load data conversion error (type mismatch or invalid character for the specified codepage) for row {row}, column {column} ({columnName}).", 4864, 16, 1);

    /// <summary>
    /// Msg 4863: a field too long for its column, at state 1, or a
    /// <c>SINGLE_CLOB</c> file that isn't valid UTF-8, at state 4.
    /// </summary>
    internal static SimulatedSqlException BulkConversionTruncation(long row, int column, string columnName, byte state = 1) =>
        new($"Bulk load data conversion error (truncation) for row {row}, column {column} ({columnName}).", 4863, 16, state);

    /// <summary>Msg 4867: a numeric field outside its column's range.</summary>
    internal static SimulatedSqlException BulkConversionOverflow(long row, int column, string columnName) =>
        new($"Bulk load data conversion error (overflow) for row {row}, column {column} ({columnName}).", 4867, 16, 1);

    /// <summary>
    /// Msg 4869: an empty field bound for a NOT NULL column that no default
    /// fills — a row error, reported and skipped as a conversion error is.
    /// </summary>
    internal static SimulatedSqlException BulkUnexpectedNull(long row, int column, string columnName) =>
        new($"The bulk load failed. Unexpected NULL value in data file row {row}, column {column}. The destination column ({columnName}) is defined as NOT NULL.", 4869, 16, 1);

    /// <summary>Msg 4865: more row errors than <c>MAXERRORS</c> allows.</summary>
    internal static SimulatedSqlException BulkMaxErrorsExceeded(int maxErrors) =>
        new($"Cannot bulk load because the maximum number of errors ({maxErrors}) was exceeded.", 4865, 16, 1);

    /// <summary>
    /// The pair — Msg 7399 then Msg 7330 — that follows every error ending a
    /// bulk load's row stream, carried after <paramref name="causes"/>. It ends
    /// the batch and rolls the transaction back as under <c>XACT_ABORT</c>, and
    /// a <c>CATCH</c> reads the Msg 7330.
    /// </summary>
    internal static SimulatedSqlException BulkRowStreamFailed(params ReadOnlySpan<SimulatedSqlException> causes)
    {
        List<SimulatedError> entries = [];
        foreach (var cause in causes)
            entries.AddRange(cause.Errors);
        entries.Add(new SimulatedSqlException("The OLE DB provider \"BULK\" for linked server \"(null)\" reported an error. The provider did not give any information about the error.", 7399, 16, 1).Errors[0]);
        entries.Add(new SimulatedSqlException("Cannot fetch a row from OLE DB provider \"BULK\" for linked server \"(null)\".", 7330, 16, 2).Errors[0]);
        return new(string.Join(Environment.NewLine, entries.Select(entry => entry.Message)), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries))
        {
            AbortsAsUnderXactAbort = true,
        };
    }

    /// <summary>
    /// Msg 4879: a <c>FORMAT = 'CSV'</c> field that isn't well formed — a
    /// quote where a field doesn't start with one, text after a closing quote,
    /// or a record with the wrong number of fields.
    /// </summary>
    internal static SimulatedSqlException BulkInvalidCsvValue(string path, long row, int column) =>
        new($"Bulk load failed due to invalid column value in CSV data file {path} in row {row}, column {column}.", 4879, 16, 1);

    /// <summary>
    /// Msg 7301: a <c>FORMAT = 'CSV'</c> record <c>FIRSTROW</c> skips has the
    /// wrong number of fields.
    /// </summary>
    internal static SimulatedSqlException BulkColumnsInfoUnavailable() =>
        new("Cannot obtain the required interface (\"IID_IColumnsInfo\") from OLE DB provider \"BULK\" for linked server \"(null)\".", 7301, 16, 2);

    /// <summary>
    /// Msg 4830, class 0, which real sends twice: a <c>char</c> load whose file
    /// opens with a UTF-16 byte-order mark reads it as <c>widechar</c>.
    /// </summary>
    internal static SimulatedError BulkAssumedWidecharMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 4830, "Bulk load: DataFileType was incorrectly specified as char. DataFileType will be assumed to be widechar because the data file has a Unicode signature.");

    /// <summary>
    /// Msg 4831, class 0, which real sends twice: a <c>widechar</c> load whose
    /// file has no UTF-16 byte-order mark reads it as <c>char</c>.
    /// </summary>
    internal static SimulatedError BulkAssumedCharMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 4831, "Bulk load: DataFileType was incorrectly specified as widechar. DataFileType will be assumed to be char because the data file does not have a Unicode signature.");

    /// <summary>
    /// Msg 4834: <c>BULK INSERT</c> by a session holding no
    /// <c>ADMINISTER BULK OPERATIONS</c>. Ends the batch, uncatchable by a
    /// <c>TRY</c> in the same scope.
    /// </summary>
    internal static SimulatedSqlException BulkLoadPermissionDenied() =>
        new("You do not have permission to use the bulk load statement.", 4834, 16, 4);

    /// <summary>Msg 4880: <c>FIRSTROW</c> past <c>LASTROW</c>.</summary>
    internal static SimulatedSqlException BulkFirstRowAfterLastRow() =>
        new("Cannot bulk load. When you use the FIRSTROW/FIRST_ROW and LASTROW parameters, the value for FIRSTROW/FIRST_ROW cannot be greater than the value for LASTROW.", 4880, 16, 1);

    /// <summary>Msg 4130: a bulk option written twice.</summary>
    internal static SimulatedSqlException BulkDuplicateHint() =>
        new("A duplicate hint was specified for the BULK rowset.", 4130, 16, 1);

    /// <summary>
    /// Msg 4817, class 0: an <c>ORDER</c> hint naming a column the table
    /// lacks, which the load then ignores.
    /// </summary>
    internal static SimulatedError BulkSortedColumnInvalidMessage(BatchContext batch, string column) =>
        batch.InfoMessage(@class: 0, state: 1, number: 4817, $"Could not bulk load. The sorted column '{column}' is not valid. The ORDER hint is ignored.");

    /// <summary>Msg 12703: a <c>DATA_SOURCE</c> naming no external data source.</summary>
    internal static SimulatedSqlException ExternalDataSourceNotFound(string name) =>
        new($"Referenced external data source \"{name}\" not found.", 12703, 16, 2);

    /// <summary>
    /// Msg 16202, class 15: an option the Linux server refuses —
    /// <c>CODEPAGE</c> — named as written.
    /// </summary>
    internal static SimulatedSqlException OptionNotSupportedOnPlatform(string written) =>
        new($"Keyword or statement option '{written}' is not supported on the 'Linux' platform.", 16202, 15, 3);

    /// <summary>Msg 5339: <c>FORMAT = 'CSV'</c> with a native data file type.</summary>
    internal static SimulatedSqlException BulkCsvNeedsCharacterData() =>
        new("CSV format option is supported for char and widechar datafiletype options.", 5339, 16, 1);

    /// <summary>Msg 4878: a <c>FIELDQUOTE</c> longer than one character.</summary>
    internal static SimulatedSqlException BulkInvalidQuoteCharacter() =>
        new("Invalid quote character specified for bulk load. Quote character can be one single byte or Unicode character.", 4878, 16, 1);

    /// <summary>Msg 491: <c>OPENROWSET(BULK …)</c> with no alias.</summary>
    internal static SimulatedSqlException BulkRowsetNeedsCorrelationName() =>
        new("A correlation name must be specified for the bulk rowset in the from clause.", 491, 16, 1);

    /// <summary>Msg 471: more than one of <c>SINGLE_BLOB</c> / <c>SINGLE_CLOB</c> / <c>SINGLE_NCLOB</c>.</summary>
    internal static SimulatedSqlException BulkSingleOptionRepeated() =>
        new("Only one of the three options, SINGLE_BLOB, SINGLE_CLOB or SINGLE_NCLOB, can be specified.", 471, 16, 1);

    /// <summary>Msg 472: <c>FORMAT = 'CSV'</c> with neither a format file nor a <c>SINGLE_*</c> option.</summary>
    internal static SimulatedSqlException BulkRowsetNeedsFormat() =>
        new("Either a format file or one of the three options SINGLE_BLOB, SINGLE_CLOB, or SINGLE_NCLOB must be specified.", 472, 16, 1);

    /// <summary>Msg 4806: <c>SINGLE_CLOB</c> over a file with a UTF-16 byte-order mark.</summary>
    internal static SimulatedSqlException SingleClobFileIsUnicode() =>
        new("SINGLE_CLOB requires a double-byte character set (DBCS) (char) input file. The file specified is Unicode.", 4806, 16, 1);

    /// <summary>Msg 4809: <c>SINGLE_NCLOB</c> over a file without a UTF-16 byte-order mark.</summary>
    internal static SimulatedSqlException SingleNclobFileIsNotUnicode() =>
        new("SINGLE_NCLOB requires a UNICODE (widechar) input file. The file specified is not Unicode.", 4809, 16, 1);

    /// <summary>Msg 15808: <c>OPENROWSET(BULK …)</c> with no format at all.</summary>
    internal static SimulatedSqlException BulkSchemaNotDetermined() =>
        new("Schema cannot be determined from data files for file format ''. Please use WITH clause of OPENROWSET to define schema.", 15808, 16, 2);

    /// <summary>Msg 5374: a <c>WITH</c> column list on a <c>FORMAT = 'CSV'</c> file rowset.</summary>
    internal static SimulatedSqlException BulkCsvWithClauseNotSupported(string path) =>
        new($"WITH clause is not supported for locations with '{path}' connector when specified FORMAT is 'CSV'.", 5374, 16, 1);

    /// <summary>Msg 4838: an <c>OPENROWSET(BULK …)</c> format file with a <c>SQLDECIMAL</c> / <c>SQLNUMERIC</c> field.</summary>
    internal static SimulatedSqlException BulkRowsetDecimalNotSupported() =>
        FormatFileRefusal("The bulk data source does not support the SQLNUMERIC or SQLDECIMAL data types.", 4838);

    /// <summary>
    /// Msg 15281: <c>OPENROWSET</c> / <c>OPENDATASOURCE</c> over a provider
    /// while <c>Ad Hoc Distributed Queries</c> is off. Refuses the batch as it
    /// compiles, and a view whose body reads one when it runs.
    /// </summary>
    internal static SimulatedSqlException AdHocDistributedQueriesDisabled() =>
        new("SQL Server blocked access to STATEMENT 'OpenRowset/OpenDatasource' of component 'Ad Hoc Distributed Queries' because this component is turned off as part of the security configuration for this server. A system administrator can enable the use of 'Ad Hoc Distributed Queries' by using sp_configure. For more information about enabling 'Ad Hoc Distributed Queries', search for 'Ad Hoc Distributed Queries' in SQL Server Books Online.", 15281, 16, 1);

    /// <summary>
    /// Msg 7222: an ad hoc rowset over a provider other than SQL Server's,
    /// which the Linux server has none of (state 255), or a linked server
    /// declared over one (state 1, probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException OnlySqlServerProviderAllowed(byte state = 255) =>
        new("Only a SQL Server provider is allowed on this instance.", 7222, 16, state);

    /// <summary>
    /// Msg 7302: <c>OPENDATASOURCE</c>, whose data-link component the Linux
    /// server lacks.
    /// </summary>
    internal static SimulatedSqlException DataLinkProviderUnavailable() =>
        new("Cannot create an instance of OLE DB provider \"MSDASC\" for linked server \"(null)\".", 7302, 16, 1);

    /// <summary>
    /// Msg 2 at line 0: an ad hoc rowset naming a server nothing answers for,
    /// refusing the batch as it compiles.
    /// </summary>
    internal static SimulatedSqlException AdHocServerUnreachable() =>
        new SimulatedSqlException("Named Pipes Provider: Could not open a connection to SQL Server [2]. ", 2, 16, 1) { TerminatesBatch = true }.PinLine(0);

    /// <summary>
    /// Msg 15281: a procedure of a component whose configuration option is
    /// off — <c>xp_cmdshell</c>, an OLE Automation procedure, or one of
    /// Database Mail's — named in <paramref name="schema"/> as real's message
    /// names it: <c>sys.</c> for an extended procedure, <c>dbo.</c> for an
    /// msdb one (probed 2026-10-15 and 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ComponentBlocked(string procedure, string component, string schema = "sys") =>
        new($"SQL Server blocked access to procedure '{schema}.{procedure}' of component '{component}' because this component is turned off as part of the security configuration for this server. A system administrator can enable the use of '{component}' by using sp_configure. For more information about enabling '{component}', search for '{component}' in SQL Server Books Online.", 15281, 16, 1);
}
