using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The engine runs under the invariant culture whatever the host's, and hands
/// the host's back: after each call, and to the handlers it raises.
/// </summary>
[TestClass]
public sealed class HostCultureTests
{
    [TestMethod]
    public void EngineOutput_IgnoresTheHostCulture()
    {
        var host = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
        try
        {
            var ex = new Simulation().AssertSqlError("throw 50000, 'x', -1", 2756);
            AreEqual("Invalid value -1 for state. State value must not be less than 0.", ex.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = host;
        }
    }

    [TestMethod]
    public void Calls_LeaveTheCallersCultureInPlace()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        using var connection = new Simulation().CreateOpenConnection();
        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "select 1; select 2";
            _ = command.ExecuteScalar();
            using (var reader = command.ExecuteReader())
            {
                _ = reader.Read();
                _ = reader.NextResult();
            }
            transaction.Commit();
        }

        AreSame(culture, CultureInfo.CurrentCulture);
        AreSame(uiCulture, CultureInfo.CurrentUICulture);
    }

    [TestMethod]
    public void InfoMessageHandlers_RunUnderTheCallersCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        var seen = new List<CultureInfo>();
        connection.InfoMessage += (_, _) => seen.Add(CultureInfo.CurrentCulture);
        using var command = connection.CreateCommand();
        command.CommandText = "print 'a'; select 1; print 'b'";
        _ = command.ExecuteNonQuery();
        using (var reader = command.ExecuteReader())
        {
            while (reader.NextResult())
            {
            }
        }

        HasCount(4, seen);
        foreach (var handlerCulture in seen)
            AreSame(culture, handlerCulture);
    }
}
