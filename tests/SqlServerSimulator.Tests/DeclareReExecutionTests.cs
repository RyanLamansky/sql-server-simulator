using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>DECLARE</c> re-executed by a loop body is legal: T-SQL hoists the
/// declaration to the batch and leaves only the assignment behind, so the
/// second pass re-assigns rather than re-declaring. A <em>second</em>
/// <c>DECLARE</c> of the same name stays Msg 134 however unreachable it is —
/// that check is compile-scoped over the batch text, which is what makes the
/// two cases distinguishable at all. Every value probe-confirmed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DeclareReExecutionTests
{
    [TestMethod]
    public void InitializerRunsOnEveryPass()
    {
        // 18, not 21: each pass re-runs `= 5` and then adds one, so the sum is
        // 6+6+6. Were the initializer run once and the value carried, it would
        // be 6+7+8.
        AreEqual(18, new Simulation().ExecuteScalar("""
            declare @i int = 0, @sum int = 0;
            while @i < 3
            begin
                declare @q int = 5;
                set @q = @q + 1;
                set @sum = @sum + @q;
                set @i = @i + 1;
            end
            select @sum
            """));
    }

    [TestMethod]
    public void WithoutAnInitializerTheValueCarriesIntoTheNextPass()
    {
        // 6, not 3: a bare DECLARE doesn't reset the slot to NULL, so the
        // increments accumulate as 1+2+3.
        AreEqual(6, new Simulation().ExecuteScalar("""
            declare @i int = 0, @sum int = 0;
            while @i < 3
            begin
                declare @q int;
                set @q = isnull(@q, 0) + 1;
                set @sum = @sum + @q;
                set @i = @i + 1;
            end
            select @sum
            """));
    }

    [TestMethod]
    public void TheLastIterationsValueSurvivesTheLoop()
    {
        // The reported repro. The pass that ends the WHILE still walks the
        // body in skip mode; the initializer is execution-scoped, so that pass
        // must not overwrite this with NULL.
        AreEqual("v2", new Simulation().ExecuteScalar("""
            declare @i int = 0;
            while @i < 3
            begin
                declare @q nvarchar(100) = N'v' + cast(@i as nvarchar(10));
                set @i = @i + 1;
            end
            select @q
            """));
    }

    [TestMethod]
    public void ADeclareInsideAnIfInsideALoopReExecutes()
        => AreEqual(6, new Simulation().ExecuteScalar("""
            declare @i int = 0, @s int = 0;
            while @i < 3
            begin
                if @i >= 0
                begin
                    declare @z int = 2;
                    set @s = @s + @z;
                end
                set @i = @i + 1;
            end
            select @s
            """));

    [TestMethod]
    public void ADeclareInANestedLoopReExecutes()
        => AreEqual(28, new Simulation().ExecuteScalar("""
            declare @i int = 0, @j int, @n int = 0;
            while @i < 2
            begin
                set @j = 0;
                while @j < 2
                begin
                    declare @k int = 7;
                    set @n = @n + @k;
                    set @j = @j + 1;
                end
                set @i = @i + 1;
            end
            select @n
            """));

    [TestMethod]
    public void ATableVariableDeclaredInALoopAccumulates()
        // Re-executing the DECLARE doesn't start an empty table: three passes
        // leave three rows.
        => AreEqual(3, new Simulation().ExecuteScalar("""
            declare @i int = 0;
            while @i < 3
            begin
                declare @t table (v int);
                insert @t values (@i);
                set @i = @i + 1;
            end
            select count(*) from @t
            """));

    // === What must still be Msg 134 ===

    [TestMethod]
    public void TwoTextualDeclaresOfTheSameName_Raises134()
        => _ = new Simulation().AssertSqlError("declare @d int; declare @d int; select 1", 134);

    [TestMethod]
    public void TheSecondDeclareIsRefusedEvenWhenUnreachable()
        // Compile-scoped: the branch never runs and it is still an error.
        => _ = new Simulation().AssertSqlError("""
            declare @e int;
            if 1 = 0
            begin
                declare @e int;
            end
            select 1
            """, 134);

    [TestMethod]
    public void ARepeatWithinOneDeclareStatement_Raises134()
        // One statement naming the same variable twice — the case that the
        // statement offset alone cannot tell from a re-execution.
        => _ = new Simulation().AssertSqlError("declare @a int, @a int; select 1", 134);

    [TestMethod]
    public void ADeclareAfterALoopThatDeclaredTheSameName_Raises134()
        => _ = new Simulation().AssertSqlError("""
            declare @i int = 0;
            while @i < 2 begin declare @w int = 1; set @i = @i + 1; end
            declare @w int;
            select 1
            """, 134);

    [TestMethod]
    public void ARedeclarationWithADifferentType_Raises134()
        => _ = new Simulation().AssertSqlError("declare @b int; declare @b nvarchar(10); select 1", 134);
}
