namespace SqlServerSimulator;

/// <summary>
/// Every schema-object <c>DROP</c> naming an object of another kind is
/// Msg 3705 naming both kinds, <c>IF EXISTS</c> or not. Probed 2026-09-26
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DropCrossKindTests
{
    private static readonly Simulation Objects = Build();

    private static Simulation Build()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create view v as select a from t",
            "create procedure p as select 1",
            "create function f() returns int as begin return 1 end",
            "create sequence s",
            "create trigger tr on t after insert as select 1");
        return sim;
    }

    [TestMethod]
    [DataRow("drop view t", "Cannot use DROP VIEW with 't' because 't' is a table. Use DROP TABLE.")]
    [DataRow("drop view if exists p", "Cannot use DROP VIEW with 'p' because 'p' is a procedure. Use DROP PROCEDURE.")]
    [DataRow("drop procedure v", "Cannot use DROP PROCEDURE with 'v' because 'v' is a view. Use DROP VIEW.")]
    [DataRow("drop function t", "Cannot use DROP FUNCTION with 't' because 't' is a table. Use DROP TABLE.")]
    [DataRow("drop sequence if exists v", "Cannot use DROP SEQUENCE with 'v' because 'v' is a view. Use DROP VIEW.")]
    [DataRow("drop trigger p", "Cannot use DROP TRIGGER with 'p' because 'p' is a procedure. Use DROP PROCEDURE.")]
    [DataRow("drop view tr", "Cannot use DROP VIEW with 'tr' because 'tr' is a trigger. Use DROP TRIGGER.")]
    [DataRow("drop procedure f", "Cannot use DROP PROCEDURE with 'f' because 'f' is a function. Use DROP FUNCTION.")]
    [DataRow("drop view s", "Cannot use DROP VIEW with 's' because 's' is a sequence. Use DROP SEQUENCE.")]
    public void ADropOfTheWrongKind_IsMsg3705(string statement, string message)
        => Objects.AssertSqlError(statement, 3705, message);
}
