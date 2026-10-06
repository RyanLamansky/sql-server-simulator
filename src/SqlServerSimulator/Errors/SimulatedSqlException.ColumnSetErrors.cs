namespace SqlServerSimulator;

// The sparse column set's refusals: declaring one, and the XML written
// through one. Every wording probed 2026-10-06 against SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>Msg 1732 state 2: a second <c>COLUMN_SET FOR ALL_SPARSE_COLUMNS</c> in one table.</summary>
    internal static SimulatedSqlException SecondColumnSet(string columnName, string tableName) =>
        new($"Cannot create the sparse column set '{columnName}' in the table '{tableName}' because a table cannot have more than one sparse column set. Modify the statement so that only one column is specified as COLUMN_SET FOR ALL_SPARSE_COLUMNS.", 1732, 16, 2);

    /// <summary>Msg 1733 state 3: a column set of a type other than <c>xml</c>.</summary>
    internal static SimulatedSqlException ColumnSetNotNullableXml(string columnName, string tableName) =>
        new($"Cannot create the sparse column set '{columnName}' in the table '{tableName}' because a sparse column set must be a nullable xml column. Modify the column definition to allow null values.", 1733, 16, 3);

    /// <summary>Msg 1734 state 1: <c>ALTER TABLE … ADD</c> of a column set to a table already holding sparse columns.</summary>
    internal static SimulatedSqlException ColumnSetOverExistingSparseColumns(string columnName, string tableName) =>
        new($"Cannot create the sparse column set '{columnName}' in the table '{tableName}' because the table already contains one or more sparse columns. A sparse column set cannot be added to a table if the table contains a sparse column.", 1734, 16, 1);

    /// <summary>Msg 360 state 1: a write naming both a column set and a sparse column it covers.</summary>
    internal static SimulatedSqlException ColumnSetAndSparseColumnWritten() =>
        new("The target column list of an INSERT, UPDATE, or MERGE statement cannot contain both a sparse column and the column set that contains the sparse column. Rewrite the statement to include either the sparse column or the column set, but not both.", 360, 16, 1);

    /// <summary>Msg 9524 state 1: column set content that isn't a sequence of simple elements.</summary>
    internal static SimulatedSqlException ColumnSetXmlMalformed() =>
        new("The XML content provided does not conform to the required XML format for sparse column sets.", 9524, 16, 1);

    /// <summary>Msg 9525 state 1: column set content naming one column twice.</summary>
    internal static SimulatedSqlException ColumnSetXmlDuplicate(string columnSet, string columnName) =>
        new($"The XML content that is supplied for the sparse column set '{columnSet}' contains duplicate references to the column '{columnName}'. A column can only be referenced once in XML content supplied to a sparse column set.", 9525, 16, 1);

    /// <summary>Msg 9530 state 1: an attribute on a column set element — real's text drops the column set name's closing quote.</summary>
    internal static SimulatedSqlException ColumnSetXmlAttribute(string columnSet, string attribute, string element) =>
        new($"In the XML content that is supplied for the column set column '{columnSet}, the '{attribute}' attribute on the element '{element}' is not valid. Remove the attribute.", 9530, 16, 1);

    /// <summary>Msg 9532 state 2: a column set element whose text doesn't convert to its column's type.</summary>
    internal static SimulatedSqlException ColumnSetXmlConversion(string columnSet, string typeName, string columnName) =>
        new($"In the query/DML operation involving  column set '{columnSet}',  conversion failed when converting from the data type 'nvarchar' to the data type '{typeName}' for the column '{columnName}'.", 9532, 16, 2);

    /// <summary>Msg 1911 state 201: a column set element naming no sparse column of the table.</summary>
    internal static SimulatedSqlException ColumnSetXmlUnknownColumn(string columnName) =>
        new($"Column name '{columnName}' does not exist in the target table, index or view.", 1911, 16, 201);
}
