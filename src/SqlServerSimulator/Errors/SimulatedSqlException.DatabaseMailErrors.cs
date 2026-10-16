namespace SqlServerSimulator;

// What msdb's Database Mail procedures raise, in SQL Server 2025's numbers and
// wordings (probed 2026-10-16). Like the Agent procedures' errors, each is
// raised at a line of real's source, often in a verification procedure, which
// AtAgentSite records for the dispatch to attribute.
partial class SimulatedSqlException
{
    /// <summary>Msg 14604: neither of a mail object's identifiers, <paramref name="what"/> being <c>account</c>, <c>profile</c> or <c>principal</c>.</summary>
    internal static SimulatedSqlException MailIdentifiersBothNull(string what) =>
        new($"Both {what} parameters (id and name) cannot be NULL", 14604, 16, 1);

    /// <summary>Msg 14605: a mail object's id and name naming two different objects.</summary>
    internal static SimulatedSqlException MailIdentifiersMismatch(string what) =>
        new($"Both {what} parameters (id and name) do not point to the same object", 14605, 16, 1);

    /// <summary>Msg 14606: a mail object's id naming none.</summary>
    internal static SimulatedSqlException MailIdNotValid(string what) =>
        new($"{what} id is not valid", 14606, 16, 1);

    /// <summary>Msg 14607: a mail object's name naming none — or, from <c>sp_send_dbmail</c>, a profile the caller may not use.</summary>
    internal static SimulatedSqlException MailNameNotValid(string what) =>
        new($"{what} name is not valid", 14607, 16, 1);

    /// <summary>Msg 14608: neither of two parameters a mail procedure needs one of.</summary>
    internal static SimulatedSqlException MailEitherParameter(string first, string second) =>
        new($"Either {first} or {second} parameter needs to be supplied", 14608, 16, 1);

    /// <summary>Msg 14610: <c>sysmail_update_profile_sp</c> with nothing to change.</summary>
    internal static SimulatedSqlException MailProfileUpdateNeedsChange() =>
        new("Either @profile_name or @description parameter needs to be specified for update", 14610, 16, 1);

    /// <summary>Msg 14611: <c>sysmail_update_profileaccount_sp</c> without a sequence number.</summary>
    internal static SimulatedSqlException MailSequenceNumberRequired() =>
        new("Account sequence number must be supplied for update", 14611, 16, 1);

    /// <summary>Msg 14614: a NULL mail server type, which real's message reads as <c>(null)</c>.</summary>
    internal static SimulatedSqlException MailServerTypeInvalid(string? serverType) =>
        new($"{serverType ?? "(null)"} is not a valid mailserver_type", 14614, 16, 1);

    /// <summary>Msg 14615: a password without a user name.</summary>
    internal static SimulatedSqlException MailPasswordWithoutUserName() =>
        new("The @username parameter needs to be supplied if the @password is supplied.", 14615, 16, 1);

    /// <summary>Msg 14618: an <c>sp_send_dbmail</c> switch passed as NULL.</summary>
    internal static SimulatedSqlException MailParameterCannotBeNull(string parameter) =>
        new($"Parameter '{parameter}' must be specified. This parameter cannot be NULL.", 14618, 16, 1);

    /// <summary>Msg 14622: an importance outside LOW, NORMAL and HIGH, named upper-cased.</summary>
    internal static SimulatedSqlException MailImportanceInvalid(string value) =>
        new($"Parameter @importance does not support the value \"{value}\". Mail importance must be one of LOW, NORMAL, or HIGH.", 14622, 16, 1);

    /// <summary>Msg 14623: a sensitivity outside NORMAL, PERSONAL, PRIVATE and CONFIDENTIAL, named upper-cased.</summary>
    internal static SimulatedSqlException MailSensitivityInvalid(string value) =>
        new($"Parameter @sensitivity does not support the value \"{value}\". Mail sensitivity must be one of NORMAL, PERSONAL, PRIVATE, or CONFIDENTIAL.", 14623, 16, 1);

    /// <summary>Msg 14624: none of a group of <c>sp_send_dbmail</c> parameters, listed as real's message lists them.</summary>
    internal static SimulatedSqlException MailAtLeastOneOf(string parameters) =>
        new($"At least one of the following parameters must be specified. \"{parameters}\".", 14624, 16, 1);

    /// <summary>Msg 14626: a body format other than TEXT or HTML, named upper-cased.</summary>
    internal static SimulatedSqlException MailFormatInvalid(string value) =>
        new($"Parameter @mailformat does not support the value \"{value}\". The mail format must be TEXT or HTML.", 14626, 16, 1);

    /// <summary>Msg 14630: an attachment whose extension <c>ProhibitedExtensions</c> lists, named by its whole path as real's message names it.</summary>
    internal static SimulatedSqlException MailExtensionProhibited(string path) =>
        new($"Database Mail is not permitted to send files with the file extension {path}.", 14630, 16, 1);

    /// <summary>Msg 14636: no profile named, and neither a private nor a public default one.</summary>
    internal static SimulatedSqlException MailNoGlobalProfile() =>
        new("No global profile is configured. Specify a profile name in the @profile_name parameter.", 14636, 16, 1);

    /// <summary>Msg 14641: <c>sp_send_dbmail</c> while <c>sysmail_stop_sp</c> has the queue stopped.</summary>
    internal static SimulatedSqlException MailStopped() =>
        new("Mail not queued. Database Mail is stopped. Use sysmail_start_sp to start Database Mail.", 14641, 16, 1);

    /// <summary>Msg 14666: a user name with default credentials.</summary>
    internal static SimulatedSqlException MailUserNameWithDefaultCredentials() =>
        new("User name cannot be supplied when using default credentials", 14666, 16, 1);

    /// <summary>
    /// Msg 22050: the query <c>sp_send_dbmail</c> runs refused — any error the
    /// query raises (<c>E_FAIL</c>, -2147467259) or a result width below 9
    /// (<c>E_INVALIDARG</c>, -2147024809).
    /// </summary>
    internal static SimulatedSqlException MailQueryFailed(int hresult) =>
        new($"Failed to initialize sqlcmd library with error number {hresult}.", 22050, 16, 1);

    /// <summary>Msg 22051: an attachment that doesn't exist.</summary>
    internal static SimulatedSqlException MailAttachmentInvalid(string path) =>
        new($"Attachment file {path} is invalid.", 22051, 16, 1);

    /// <summary>Msg 22051: an attachment or query result larger than <c>MaxFileSize</c>.</summary>
    internal static SimulatedSqlException MailAttachmentTooLarge(int maxFileSize) =>
        new($"File attachment or query results size exceeds allowable value of {maxFileSize} bytes.", 22051, 16, 1);

    /// <summary>Msg 22051: an existing attachment a SQL login outside <c>sysadmin</c> names, which only an integrated login's context reads.</summary>
    internal static SimulatedSqlException MailAttachmentNeedsIntegratedLogin() =>
        new("The client connection security context could not be impersonated. Attaching files require an integrated client login", 22051, 16, 1);

    /// <summary>
    /// Msg 547: an account's server row naming a type <c>sysmail_servertype</c>
    /// lacks, under the name real generated for that foreign key, at state 1.
    /// </summary>
    internal static SimulatedSqlException MailServerTypeConflict() =>
        new("The INSERT statement conflicted with the FOREIGN KEY constraint \"FK__sysmail_s__serve__53D770D6\". The conflict occurred in database \"msdb\", table \"dbo.sysmail_servertype\", column 'servertype'.", 547, 16, 1);
}
