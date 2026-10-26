using System.Runtime.ExceptionServices;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The engine's own exceptions, counted as a consumer's debugger meets them:
/// a statement that succeeds dispatches none, and an error dispatches only as
/// often as its delivery needs, since each throw — a catch that rethrows
/// included — is another unwind and another stop under a debugger that breaks
/// on thrown exceptions.
/// </summary>
[TestClass]
public sealed class ExceptionDispatchTests
{
    private const string Setup = """
        create table p (id int primary key, name nvarchar(20));
        create table c (id int primary key, pid int references p(id), name nvarchar(20));
        insert p values (1, N'a'), (2, N'b');
        insert c values (1, 1, N'x');
        """;

    /// <summary>
    /// Runs <paramref name="commandText"/> to its end on <paramref name="simulation"/>
    /// and counts the simulator's exceptions thrown on this thread meanwhile,
    /// whether the batch fails or not.
    /// </summary>
    private static int Dispatches(Simulation simulation, string commandText)
    {
        var thread = Environment.CurrentManagedThreadId;
        var count = 0;
        void Count(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.Exception.GetType().Namespace?.StartsWith("SqlServerSimulator", StringComparison.Ordinal) == true)
                count++;
        }

        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            using var reader = simulation.ExecuteReader(commandText);
            do
            {
                while (reader.Read())
                {
                }
            }
            while (reader.NextResult());
        }
        catch (SimulatedSqlException)
        {
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }
        return count;
    }

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        return simulation;
    }

    [TestMethod]
    [DataRow("""
        SET IMPLICIT_TRANSACTIONS OFF;
        SET NOCOUNT ON;
        MERGE [p] USING (
        VALUES (3, N'c', 0),
        (4, N'd', 1)) AS i ([id], [name], _Position) ON 1=0
        WHEN NOT MATCHED THEN
        INSERT ([id], [name])
        VALUES (i.[id], i.[name])
        OUTPUT INSERTED.[id], i._Position;
        """)]
    [DataRow("insert c (id, pid, name) output inserted.id, inserted.name values (2, 2, N'y')")]
    [DataRow("update c set name = p.name output deleted.name, inserted.name from c join p on p.id = c.pid")]
    [DataRow("select p.id, c.name from p left join c on c.pid = p.id where p.name like N'a%' order by p.id")]
    [DataRow("delete c output deleted.id where c.id = 1")]
    public void A_Statement_That_Succeeds_Dispatches_Nothing(string statement)
        => AreEqual(0, Dispatches(Seeded(), statement));

    [TestMethod]
    [DataRow("delete p where id = 1", 2)]
    [DataRow("delete p output deleted.id where id = 1", 3)]
    public void An_Error_Dispatches_As_Its_Delivery_Needs(string statement, int dispatches)
        => AreEqual(dispatches, Dispatches(Seeded(), statement));
}
