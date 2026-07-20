using System.Reflection;
using SqlServerSimulator.Storage.Bacpac;

namespace SqlServerSimulator;

[TestClass]
public class QualityTests
{
    [TestMethod]
    [Description("Prevents unintentional expansion of the public API.")]
    public void PublicApiWhitelist()
    {
        var publicTypes = typeof(Simulation)
            .Assembly
            .GetTypes()
            .Where(type => type.IsPublic)
            .ToArray();

        // Per-type whitelist of member names declared directly on the type.
        // Property / event / operator accessors and compiler-generated members
        // (record <Clone>$, the C# 14 extension's <G>$... nested type) are
        // filtered before comparison, so the entries below read as the
        // human-meaningful API surface.
        Dictionary<Type, HashSet<string>> allowedMembers = new()
        {
            [typeof(Simulation)] = [
                ".ctor",
                nameof(Simulation.CreateDbConnection),
                nameof(Simulation.ImportBacpac),
                nameof(Simulation.AddRemoteSimulation),
                nameof(Simulation.ServerCollationName),
                nameof(Simulation.ListenLocalAsync),
                nameof(Simulation.ListenNetworkAsync),
            ],
            [typeof(SimulatedNetworkListener)] = [
                nameof(SimulatedNetworkListener.Port),
                nameof(SimulatedNetworkListener.ServerCertificate),
                nameof(SimulatedNetworkListener.Dispose),
                nameof(SimulatedNetworkListener.DisposeAsync),
            ],
            [typeof(SimulatedNetworkListenerOptions)] = [
                ".ctor",
                nameof(SimulatedNetworkListenerOptions.Port),
                nameof(SimulatedNetworkListenerOptions.BindAddress),
                nameof(SimulatedNetworkListenerOptions.ServerCertificate),
            ],
            [typeof(SimulatedDbConnection)] = [
                nameof(SimulatedDbConnection.InfoMessage),
                nameof(SimulatedDbConnection.ConnectionString),
                nameof(SimulatedDbConnection.Database),
                nameof(SimulatedDbConnection.DataSource),
                nameof(SimulatedDbConnection.ServerVersion),
                nameof(SimulatedDbConnection.State),
                nameof(SimulatedDbConnection.ChangeDatabase),
                nameof(SimulatedDbConnection.Close),
                nameof(SimulatedDbConnection.Open),
                nameof(SimulatedDbConnection.CreateCommand),
                nameof(SimulatedDbConnection.BeginTransaction),
            ],
            [typeof(SimulatedDbCommand)] = [
                nameof(SimulatedDbCommand.CommandText),
                nameof(SimulatedDbCommand.CommandTimeout),
                nameof(SimulatedDbCommand.CommandType),
                nameof(SimulatedDbCommand.DesignTimeVisible),
                nameof(SimulatedDbCommand.UpdatedRowSource),
                nameof(SimulatedDbCommand.Cancel),
                nameof(SimulatedDbCommand.ExecuteNonQuery),
                nameof(SimulatedDbCommand.ExecuteScalar),
                nameof(SimulatedDbCommand.Prepare),
                nameof(SimulatedDbCommand.CreateParameter),
                nameof(SimulatedDbCommand.Parameters),
                nameof(SimulatedDbCommand.Connection),
                nameof(SimulatedDbCommand.Transaction),
                nameof(SimulatedDbCommand.ExecuteReader),
            ],
            [typeof(SimulatedDbTransaction)] = [
                nameof(SimulatedDbTransaction.IsolationLevel),
                nameof(SimulatedDbTransaction.Commit),
                nameof(SimulatedDbTransaction.Rollback),
                nameof(SimulatedDbTransaction.Connection),
            ],
            [typeof(SimulatedDbDataReader)] = [
                "Item",
                nameof(SimulatedDbDataReader.Depth),
                nameof(SimulatedDbDataReader.FieldCount),
                nameof(SimulatedDbDataReader.HasRows),
                nameof(SimulatedDbDataReader.IsClosed),
                nameof(SimulatedDbDataReader.RecordsAffected),
                nameof(SimulatedDbDataReader.GetBoolean),
                nameof(SimulatedDbDataReader.GetByte),
                nameof(SimulatedDbDataReader.GetBytes),
                nameof(SimulatedDbDataReader.GetChar),
                nameof(SimulatedDbDataReader.GetChars),
                nameof(SimulatedDbDataReader.GetDataTypeName),
                nameof(SimulatedDbDataReader.GetDateTime),
                nameof(SimulatedDbDataReader.GetDecimal),
                nameof(SimulatedDbDataReader.GetDouble),
                nameof(SimulatedDbDataReader.GetEnumerator),
                nameof(SimulatedDbDataReader.GetFieldType),
                nameof(SimulatedDbDataReader.GetFloat),
                nameof(SimulatedDbDataReader.GetGuid),
                nameof(SimulatedDbDataReader.GetInt16),
                nameof(SimulatedDbDataReader.GetInt32),
                nameof(SimulatedDbDataReader.GetInt64),
                nameof(SimulatedDbDataReader.GetName),
                nameof(SimulatedDbDataReader.GetOrdinal),
                nameof(SimulatedDbDataReader.GetString),
                nameof(SimulatedDbDataReader.GetValue),
                nameof(SimulatedDbDataReader.GetFieldValue),
                nameof(SimulatedDbDataReader.GetValues),
                nameof(SimulatedDbDataReader.IsDBNull),
                nameof(SimulatedDbDataReader.NextResult),
                nameof(SimulatedDbDataReader.Read),
            ],
            [typeof(SimulatedSqlException)] = [
                nameof(SimulatedSqlException.ErrorCode),
                nameof(SimulatedSqlException.IsTransient),
                nameof(SimulatedSqlException.Number),
                nameof(SimulatedSqlException.Class),
                nameof(SimulatedSqlException.State),
                nameof(SimulatedSqlException.Errors),
                nameof(SimulatedSqlException.LineNumber),
                nameof(SimulatedSqlException.Procedure),
                nameof(SimulatedSqlException.Server),
            ],
            [typeof(SimulatedError)] = [
                nameof(SimulatedError.Class),
                nameof(SimulatedError.LineNumber),
                nameof(SimulatedError.Message),
                nameof(SimulatedError.Number),
                nameof(SimulatedError.Procedure),
                nameof(SimulatedError.Server),
                nameof(SimulatedError.Source),
                nameof(SimulatedError.State),
                nameof(SimulatedError.ToString),
            ],
            [typeof(SimulatedErrorCollection)] = [
                "Item",
                nameof(SimulatedErrorCollection.Count),
                nameof(SimulatedErrorCollection.CopyTo),
                nameof(SimulatedErrorCollection.GetEnumerator),
            ],
            [typeof(SimulatedInfoMessageEventArgs)] = [
                nameof(SimulatedInfoMessageEventArgs.Errors),
                nameof(SimulatedInfoMessageEventArgs.LineNumber),
                nameof(SimulatedInfoMessageEventArgs.Message),
                nameof(SimulatedInfoMessageEventArgs.Source),
            ],
            [typeof(SimulatedDbParameter)] = [
                ".ctor",
                nameof(SimulatedDbParameter.DbType),
                nameof(SimulatedDbParameter.Direction),
                nameof(SimulatedDbParameter.IsNullable),
                nameof(SimulatedDbParameter.ParameterName),
                nameof(SimulatedDbParameter.Size),
                nameof(SimulatedDbParameter.SourceColumn),
                nameof(SimulatedDbParameter.SourceColumnNullMapping),
                nameof(SimulatedDbParameter.Value),
                nameof(SimulatedDbParameter.TypeName),
                nameof(SimulatedDbParameter.ResetDbType),
            ],
            [typeof(SimulatedDbParameterCollection)] = [
                ".ctor",
                "Item",
                nameof(SimulatedDbParameterCollection.Count),
                nameof(SimulatedDbParameterCollection.SyncRoot),
                nameof(SimulatedDbParameterCollection.Add),
                nameof(SimulatedDbParameterCollection.AddRange),
                nameof(SimulatedDbParameterCollection.Clear),
                nameof(SimulatedDbParameterCollection.Contains),
                nameof(SimulatedDbParameterCollection.CopyTo),
                nameof(SimulatedDbParameterCollection.GetEnumerator),
                nameof(SimulatedDbParameterCollection.IndexOf),
                nameof(SimulatedDbParameterCollection.Insert),
                nameof(SimulatedDbParameterCollection.Remove),
                nameof(SimulatedDbParameterCollection.RemoveAt),
            ],
            [typeof(BacpacImportOptions)] = [
                ".ctor",
                nameof(BacpacImportOptions.DatabaseName),
                nameof(BacpacImportOptions.MaxDegreeOfParallelism),
                nameof(Equals),
                nameof(GetHashCode),
                nameof(ToString),
            ],
            [typeof(BacpacImportResult)] = [
                ".ctor",
                nameof(BacpacImportResult.ElementCounts),
                nameof(BacpacImportResult.Skipped),
                nameof(BacpacImportResult.Warnings),
            ],
            [typeof(BacpacSkipped)] = [
                ".ctor",
                "Deconstruct",
                nameof(BacpacSkipped.ElementName),
                nameof(BacpacSkipped.ElementType),
                nameof(BacpacSkipped.Reason),
                nameof(Equals),
                nameof(GetHashCode),
                nameof(ToString),
            ],
        };

        Assert.HasCount(allowedMembers.Count, publicTypes);
        foreach (var type in publicTypes)
            Assert.Contains(type, allowedMembers.Keys);

        foreach (var (type, allowedNames) in allowedMembers)
        {
            var memberNames = type
                .GetMembers()
                .Where(member => member.DeclaringType == type)
                .Where(member => member.Name[0] != '<')
                .Where(member => member is not MethodInfo mi || !mi.IsSpecialName)
                .Select(member => member.Name)
                .ToHashSet();

            Assert.HasCount(allowedNames.Count, memberNames, $"Member count mismatch on {type.FullName}");
            foreach (var name in memberNames)
                Assert.Contains(name, allowedNames, $"Unexpected public member '{name}' on {type.FullName}");
        }
    }
}
