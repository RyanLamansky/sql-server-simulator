namespace SqlServerSimulator;

// The errors sp_addmessage / sp_altermessage / sp_dropmessage raise, and the
// RAISERROR / FORMATMESSAGE errors a registered message adds. Wording, class
// and state are probed 2026-09-30 against SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>Msg 15040: <c>sp_addmessage</c> with an id of 50000 or less.</summary>
    internal static SimulatedSqlException UserMessageIdTooLow() =>
        new("User-defined error messages must have an ID greater than 50000.", 15040, 16, 1);

    /// <summary>Msg 15041: <c>sp_addmessage</c> with a severity outside 1 through 25.</summary>
    internal static SimulatedSqlException UserMessageSeverityOutOfRange() =>
        new("User-defined error messages must have a severity level between 1 and 25.", 15041, 16, 1);

    /// <summary>Msg 15071: <c>sp_addmessage</c> with a NULL id, severity or text.</summary>
    internal static SimulatedSqlException AddMessageUsage() =>
        new("Usage: sp_addmessage <msgnum>,<severity>,<msgtext> [,<language> [,FALSE | TRUE [,REPLACE]]]", 15071, 16, 1);

    /// <summary>Msg 15033: a message procedure's <c>@lang</c> that names no installed language.</summary>
    internal static SimulatedSqlException NotAnOfficialLanguageName(string language) =>
        new($"'{language}' is not a valid official language name.", 15033, 16, 1);

    /// <summary>Msg 15279: a localized message added before its us_english version.</summary>
    internal static SimulatedSqlException UsEnglishMessageRequired(string language) =>
        new($"You must add the us_english version of this message before you can add the '{language}' version.", 15279, 16, 1);

    /// <summary>Msg 15271: <c>sp_addmessage</c>'s <c>@with_log</c> that is neither 'true' nor 'false'.</summary>
    internal static SimulatedSqlException InvalidWithLogValue() =>
        new("Invalid @with_log parameter value. Valid values are 'true' or 'false'.", 15271, 16, 1);

    /// <summary>Msg 15043: <c>sp_addmessage</c> over an existing message without <c>REPLACE</c>, or with a <c>@replace</c> that isn't.</summary>
    internal static SimulatedSqlException MessageReplaceRequired() =>
        new("You must specify 'REPLACE' to overwrite an existing message.", 15043, 16, 1);

    /// <summary>Msg 15304: a localized message whose severity differs from its us_english version's.</summary>
    internal static SimulatedSqlException LocalizedMessageSeverityMismatch(string language, byte severity) =>
        new($"The severity level of the '{language}' version of this message must be the same as the severity level ({severity}) of the us_english version.", 15304, 16, 1);

    /// <summary>Msg 15177: <c>sp_dropmessage</c> with a NULL or missing id.</summary>
    internal static SimulatedSqlException DropMessageUsage() =>
        new("Usage: sp_dropmessage <msg number> [,<language> | 'ALL']", 15177, 16, 1);

    /// <summary>Msg 15178: <c>sp_dropmessage</c> of an id of 50000 or less.</summary>
    internal static SimulatedSqlException CannotDropSystemMessage() =>
        new("Cannot drop a message with an ID less than 50,000.", 15178, 16, 1);

    /// <summary>Msg 15179: a message procedure naming a message or language version that doesn't exist.</summary>
    internal static SimulatedSqlException MessageDoesNotExist(int messageId) =>
        new($"The message number {messageId} or specified language version does not exist.", 15179, 16, 1);

    /// <summary>Msg 15280: <c>sp_dropmessage</c> of a us_english message that localized versions remain for.</summary>
    internal static SimulatedSqlException LocalizedMessagesRemain() =>
        new("All localized versions of this message must be dropped before the us_english version can be dropped.", 15280, 16, 1);

    /// <summary>Msg 15176: <c>sp_altermessage</c> with an <c>@parameter</c> other than WITH_LOG.</summary>
    internal static SimulatedSqlException AlterMessageParameterInvalid() =>
        new("The only valid @parameter value is 'WITH_LOG'.", 15176, 16, 1);

    /// <summary>Msg 15277: <c>sp_altermessage</c> with an <c>@parameter_value</c> that is neither 'true' nor 'false'.</summary>
    internal static SimulatedSqlException AlterMessageValueInvalid() =>
        new("The only valid @parameter_value values are 'true' or 'false'.", 15277, 16, 1);

    /// <summary>Msg 290 state 2: <c>sp_altermessage</c> with a NULL id, which fails on real's own object call.</summary>
    internal static SimulatedSqlException AlterMessageNullId() =>
        new("Invalid EXECUTE statement using object \"ErrorMessage\", method \"Lock\".", 290, 16, 2);

    // sp_addtype / sp_droptype — probed 2026-09-30 against SQL Server 2025.

    /// <summary>Msg 15036: a data type <c>sp_addtype</c> or <c>sp_droptype</c> can't find, or the caller can't see.</summary>
    internal static SimulatedSqlException DataTypeDoesNotExist(string typeName) =>
        new($"The data type '{typeName}' does not exist or you do not have permission.", 15036, 16, 1);

    /// <summary>Msg 15085: <c>sp_addtype</c>'s <c>@nulltype</c> that is none of NULL, NOT NULL and NONULL.</summary>
    internal static SimulatedSqlException AddTypeUsage() =>
        new("Usage: sp_addtype name, 'data type' [,'NULL' | 'NOT NULL']", 15085, 16, 1);

    /// <summary>Msg 15108: <c>sp_addtype</c> of a MAX type, which only <c>CREATE TYPE</c> defines.</summary>
    internal static SimulatedSqlException AddTypeCannotDefineMaxTypes() =>
        new("sp_addtype cannot be used to define user-defined data types for varchar(max), nvarchar(max) or varbinary(max) data types. Use CREATE TYPE for this purpose.", 15108, 16, 1);

    /// <summary>Msg 15656: <c>sp_addtype</c> of an <c>xml</c> base.</summary>
    internal static SimulatedSqlException AddTypeFromXml() =>
        new("Cannot create user defined types from XML data type.", 15656, 16, 1);

    // sp_tableoption / sp_indexoption / sp_autostats / sp_updatestats / sp_createstats — probed 2026-09-30 against SQL Server 2025.

    /// <summary>Msg 15002: a procedure that refuses to run in a transaction (<c>sp_tableoption</c>, <c>sp_indexoption</c>).</summary>
    internal static SimulatedSqlException ProcedureCannotRunInTransaction(string procedure) =>
        new($"The procedure 'sys.{procedure}' cannot be executed within a transaction.", 15002, 16, 1);

    /// <summary>Msg 15600 at class 15: an option, value or parameter a <c>sys.sp_*</c> procedure doesn't accept — the option procedures, the linked-server ones, <c>sp_settriggerorder</c>.</summary>
    internal static SimulatedSqlException InvalidSystemProcedureOption(string procedure) =>
        new($"An invalid parameter or option was specified for procedure 'sys.{procedure}'.", 15600, 15, 1);

    /// <summary>Msg 15388: a table name <c>sp_tableoption</c> or <c>sp_indexoption</c> can't resolve.</summary>
    internal static SimulatedSqlException NoUserTableMatching(string name) =>
        new($"There is no user table matching the input name '{name}' in the current database or you do not have permission to access the table.", 15388, 11, 1);

    /// <summary>Msg 15390: a table or indexed view name <c>sp_autostats</c> can't resolve.</summary>
    internal static SimulatedSqlException NoTableOrIndexedViewMatching(string name) =>
        new($"Input name '{name}' does not have a matching user table or indexed view in the current database.", 15390, 11, 1);

    /// <summary>Msg 15387: <c>sp_autostats</c> given a name qualified by another database.</summary>
    internal static SimulatedSqlException QualifiedNameMustBeCurrentDatabase() =>
        new("If the qualified object name specifies a database, that database must be the current database.", 15387, 11, 1);

    /// <summary>Msg 15323: <c>sp_autostats</c> naming an index the table doesn't have.</summary>
    internal static SimulatedSqlException SelectedIndexDoesNotExist(string tableName) =>
        new($"The selected index does not exist on table '{tableName}'.", 15323, 16, 1);

    /// <summary>Msg 15112: <c>sp_tableoption 'text in row'</c> with a number outside 24 through 7000.</summary>
    internal static SimulatedSqlException TextInRowValueInvalid() =>
        new("The third parameter for table option 'text in row' is invalid. It should be 'on', 'off', '0', '1' or a number from 24 through 7000.", 15112, 11, 1);

    /// <summary>Msg 2599: <c>sp_tableoption 'text in row'</c> on a table with no <c>text</c>, <c>ntext</c> or <c>image</c> column.</summary>
    internal static SimulatedSqlException CannotSwitchToInRowText(string tableName) =>
        new($"Cannot switch to in row text in table \"{tableName}\".", 2599, 16, 1);

    /// <summary>Msg 14138: <c>sp_updatestats</c> given an option other than RESAMPLE, as its <c>char(8)</c> parameter holds it.</summary>
    internal static SimulatedSqlException InvalidUpdateStatsOption(string option) =>
        new($"Invalid option name '{option}'.", 14138, 16, 1);

    // Legacy security procedures — probed 2026-09-30 against SQL Server 2025.

    /// <summary>Msg 15010: a login's default database (or <c>sp_defaultdb</c>'s) that the instance doesn't have.</summary>
    internal static SimulatedSqlException DefaultDatabaseDoesNotExist(string database) =>
        new($"The database '{database}' does not exist. Supply a valid database name. To see available databases, use sys.databases.", 15010, 16, 1);

    /// <summary>Msg 15006: a SQL login named with a backslash.</summary>
    internal static SimulatedSqlException InvalidLoginNameCharacters(string name) =>
        new($"'{name}' is not a valid name because it contains invalid characters.", 15006, 16, 1);

    /// <summary>Msg 15021: <c>sp_addlogin</c>'s <c>@encryptopt</c> asking to skip encryption of a password that isn't a hash.</summary>
    internal static SimulatedSqlException PasswordParameterInvalid() =>
        new("Invalid value given for parameter PASSWORD. Specify a valid parameter value.", 15021, 16, 2);

    /// <summary>Msg 33062: a password under the policy's 8 characters.</summary>
    internal static SimulatedSqlException PasswordTooShort() =>
        new("Password validation failed. The password does not meet SQL Server password policy requirements because it is too short. The password must be at least 8 characters.", 33062, 16, 2);

    /// <summary>Msg 33064: a password without three of the four character sets, or with the login's own name in it.</summary>
    internal static SimulatedSqlException PasswordNotComplex() =>
        new("Password validation failed. The password does not meet SQL Server password policy requirements because it is not complex enough. The password must be at least 8 characters long and contain characters from three of the following four sets: Uppercase letters, Lowercase letters, Base 10 digits, and Symbols.", 33064, 16, 2);

    /// <summary>Msg 15122: <c>CHECK_EXPIRATION</c> on with <c>CHECK_POLICY</c> off.</summary>
    internal static SimulatedSqlException CheckExpirationNeedsPolicy() =>
        new("The CHECK_EXPIRATION option cannot be used when CHECK_POLICY is OFF.", 15122, 16, 1);

    /// <summary>Msg 15007: a login a legacy security procedure, <c>sp_who</c> / <c>sp_who2</c>'s <c>@loginame</c> or <c>CREATE USER … FOR LOGIN</c> can't find.</summary>
    internal static SimulatedSqlException NotAValidLogin(string name) =>
        new($"'{name}' is not a valid login or you do not have permission.", 15007, 16, 1);

    /// <summary>Msg 15014: <c>sp_adduser</c>'s <c>@grpname</c> that names no role of the database.</summary>
    internal static SimulatedSqlException RoleDoesNotExistInDatabase(string role) =>
        new($"The role '{role}' does not exist in the current database.", 15014, 16, 1);

    /// <summary>Msg 15008: <c>sp_dropuser</c> of a name the database has no principal for.</summary>
    internal static SimulatedSqlException UserDoesNotExistInDatabase(string user) =>
        new($"User '{user}' does not exist in the current database.", 15008, 16, 1);

    /// <summary>Msg 15151: a user <c>DROP USER</c> or <c>sp_revokedbaccess</c> can't drop, absent or not the caller's to drop.</summary>
    internal static SimulatedSqlException CannotDropUser(string user) =>
        new($"Cannot drop the user '{user}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Msg 15150: dropping <c>dbo</c> through <c>sys.sp_revokedbaccess</c>.</summary>
    internal static SimulatedSqlException CannotDropDatabaseOwnerUser(string user) =>
        new($"Cannot drop the user '{user}'.", 15150, 16, 1);

    /// <summary>Msg 15150: <c>ALTER USER dbo WITH DEFAULT_SCHEMA</c>, whose default schema is always <c>dbo</c> (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException CannotAlterDatabaseOwnerUser(string user) =>
        new($"Cannot alter the user '{user}'.", 15150, 16, 1);

    /// <summary>
    /// Msg 102 at severity 16, naming the option as written: <c>ALTER USER …
    /// WITH DEFAULT_SCHEMA = NULL</c>, refused as it runs rather than as it
    /// parses, and ending the batch (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException NullDefaultSchemaRefused(string optionAsWritten) =>
        new SimulatedSqlException($"Incorrect syntax near '{optionAsWritten}'.", 102, 16, 1).EndingBatch();

    /// <summary>Msg 15150: <c>sp_droprole</c> of <c>public</c> or a fixed role.</summary>
    internal static SimulatedSqlException CannotDropFixedRole(string role) =>
        new($"Cannot drop the role '{role}'.", 15150, 16, 1);

    /// <summary>Msg 15409 at class 11: <c>sp_helprolemember</c> of a name that isn't a role.</summary>
    internal static SimulatedSqlException NotARole(string name) =>
        new($"'{name}' is not a role.", 15409, 11, 1);

    /// <summary>Msg 15291: <c>sp_change_users_login</c> naming a user that is absent or not mapped to a login.</summary>
    internal static SimulatedSqlException UserAbsentOrInvalid(string user) =>
        new($"Terminating this procedure. The User name '{user}' is absent or invalid.", 15291, 16, 1);

    /// <summary>Msg 15287: <c>sp_change_users_login</c> naming <c>sa</c> as the login, or a fixed user as the user.</summary>
    internal static SimulatedSqlException ForbiddenChangeUsersLoginName(string name) =>
        new($"Terminating this procedure. '{name}' is a forbidden value for the login name parameter in this procedure.", 15287, 16, 1);

    /// <summary>Msg 15286: <c>sp_change_users_login</c> with an <c>@Action</c> it doesn't know.</summary>
    internal static SimulatedSqlException UnrecognizedChangeUsersLoginAction(string action) =>
        new($"Terminating this procedure. The @action '{action}' is unrecognized. Try 'REPORT', 'UPDATE_ONE', or 'AUTO_FIX'.", 15286, 16, 1);

}
