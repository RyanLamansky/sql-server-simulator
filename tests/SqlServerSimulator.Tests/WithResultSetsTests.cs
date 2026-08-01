using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>EXECUTE … WITH RESULT SETS</c> option — the
/// <c>UNDEFINED</c> / <c>NONE</c> / explicit-definition forms, the projection
/// the explicit form applies (rename, retype, NULL / NOT NULL), and the
/// contract errors (Msg 11535 / 11536 / 11537 / 11538 / 11553 plus the Msg
/// 8114 value-conversion failure). Probed against SQL Server 2025
/// (2026-07-31).
/// </summary>
[TestClass]
public sealed class WithResultSetsTests
{
    private static Simulation WithProcedure(string body = "select 1 as a, 'hello' as b")
    {
        var sim = new Simulation();
        sim.ExecuteBatches($"create procedure dbo.p as {body}");
        return sim;
    }

    [TestMethod]
    public void Undefined_LeavesModuleMetadataAlone()
    {
        using var reader = WithProcedure().ExecuteReader("exec dbo.p with result sets undefined");
        IsTrue(reader.Read());
        AreEqual("a", reader.GetName(0));
        AreEqual("b", reader.GetName(1));
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public void Explicit_RenamesColumns()
    {
        using var reader = WithProcedure().ExecuteReader("exec dbo.p with result sets ((x int, y varchar(20)))");
        IsTrue(reader.Read());
        AreEqual("x", reader.GetName(0));
        AreEqual("y", reader.GetName(1));
    }

    [TestMethod]
    public void Explicit_RetypesColumns()
    {
        using var reader = WithProcedure().ExecuteReader("exec dbo.p with result sets ((x bigint, y nvarchar(40)))");
        IsTrue(reader.Read());
        AreEqual("bigint", reader.GetDataTypeName(0));
        AreEqual("nvarchar", reader.GetDataTypeName(1));
        AreEqual(1L, reader.GetInt64(0));
        AreEqual("hello", reader.GetString(1));
    }

    [TestMethod]
    public void Explicit_IntToVarchar_ConvertsValue()
        => AreEqual("1", WithProcedure("select 1 as a").ExecuteScalar(
            "exec dbo.p with result sets ((x varchar(30)))"));

    [TestMethod]
    public void Explicit_NarrowStringTarget_TruncatesSilently()
        => AreEqual("he", WithProcedure("select 'hello' as a").ExecuteScalar(
            "exec dbo.p with result sets ((x varchar(2)))"));

    [TestMethod]
    public void Explicit_IntTooWideForVarchar_UsesAsteriskFallback()
        => AreEqual("*", WithProcedure("select 300 as a").ExecuteScalar(
            "exec dbo.p with result sets ((x varchar(2)))"));

    [TestMethod]
    public void Explicit_CollateOnDeclaredColumn_IsAccepted()
        => AreEqual("hello", WithProcedure("select 'hello' as a").ExecuteScalar(
            "exec dbo.p with result sets ((x varchar(20) collate Latin1_General_BIN2 not null))"));

    [TestMethod]
    public void Explicit_MaxLengthTarget_IsAccepted()
        => AreEqual("hello", WithProcedure("select 'hello' as a").ExecuteScalar(
            "exec dbo.p with result sets ((x varchar(max)))"));

    [TestMethod]
    public void None_WithNoResultSets_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int)");
        sim.ExecuteBatches("create procedure dbo.p as insert t values (1)");
        AreEqual(1, sim.ExecuteScalar("exec dbo.p with result sets none; select count(*) from t"));
    }

    [TestMethod]
    public void None_WithResultSet_Raises11535()
        => WithProcedure().AssertSqlError("exec dbo.p with result sets none", 11535,
            "EXECUTE statement failed because its WITH RESULT SETS clause specified 0 result set(s), and the statement tried to send more result sets than this.");

    [TestMethod]
    public void MoreSetsThanDeclared_Raises11535()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 1 as a; select 2 as b end");
        _ = sim.AssertSqlError("exec dbo.p with result sets ((x int))", 11535);
    }

    [TestMethod]
    public void FewerSetsThanDeclared_Raises11536()
        => WithProcedure("select 1 as a").AssertSqlError(
            "exec dbo.p with result sets ((x int), (y int))", 11536,
            "EXECUTE statement failed because its WITH RESULT SETS clause specified 2 result set(s), but the statement only sent 1 result set(s) at run time.");

    [TestMethod]
    public void NoResultSetAtAll_AgainstOneDeclared_Raises11536()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int)");
        sim.ExecuteBatches("create procedure dbo.p as insert t values (1)");
        _ = sim.AssertSqlError("exec dbo.p with result sets ((x int))", 11536);
    }

    [TestMethod]
    public void TooFewDeclaredColumns_Raises11537()
        => WithProcedure().AssertSqlError("exec dbo.p with result sets ((x int))", 11537,
            "EXECUTE statement failed because its WITH RESULT SETS clause specified 1 column(s) for result set number 1, but the statement sent 2 column(s) at run time.");

    [TestMethod]
    public void TooManyDeclaredColumns_Raises11537()
        => WithProcedure().AssertSqlError("exec dbo.p with result sets ((x int, y varchar(9), z int))", 11537);

    [TestMethod]
    public void NoImplicitConversion_Raises11538()
        => WithProcedure().AssertSqlError("exec dbo.p with result sets ((x date, y varchar(9)))", 11538,
            "EXECUTE statement failed because its WITH RESULT SETS clause specified type 'date' for column #1 in result set #1, and the corresponding type sent at run time was 'int'; there is no conversion between the two types.");

    [TestMethod]
    public void NoImplicitConversion_ReportsBareTypeNames()
        => WithProcedure("select cast('2020-01-02' as date) as a").AssertSqlError(
            "exec dbo.p with result sets ((x decimal(5,2)))", 11538,
            "EXECUTE statement failed because its WITH RESULT SETS clause specified type 'decimal' for column #1 in result set #1, and the corresponding type sent at run time was 'date'; there is no conversion between the two types.");

    [TestMethod]
    public void ExplicitOnlyCastPairs_AreStillMsg11538()
    {
        // xml → varchar and varchar → varbinary both have a legal explicit
        // CAST but no implicit conversion, so WITH RESULT SETS refuses them.
        _ = WithProcedure("select cast('<a/>' as xml) as a").AssertSqlError(
            "exec dbo.p with result sets ((x varchar(30)))", 11538);
        _ = WithProcedure("select 'hello' as a").AssertSqlError(
            "exec dbo.p with result sets ((x varbinary(10)))", 11538);
    }

    [TestMethod]
    public void ImplicitConversionPairs_AreAccepted()
    {
        // The counterpart of the rejection above: varchar → xml, varbinary →
        // uniqueidentifier and int → datetime are implicit and convert.
        AreEqual("<a/>", WithProcedure("select '<a/>' as a").ExecuteScalar(
            "exec dbo.p with result sets ((x xml))"));
        AreEqual(new Guid("04030201-0605-0807-090a-0b0c0d0e0f10"),
            WithProcedure("select cast(0x0102030405060708090a0b0c0d0e0f10 as varbinary(16)) as a").ExecuteScalar(
                "exec dbo.p with result sets ((x uniqueidentifier))"));
        AreEqual(new DateTime(1900, 1, 2, 0, 0, 0, DateTimeKind.Unspecified),
            WithProcedure("select 1 as a").ExecuteScalar(
                "exec dbo.p with result sets ((x datetime))"));
    }

    [TestMethod]
    public void ValueConversionFailure_Raises8114()
        => WithProcedure().AssertSqlError("exec dbo.p with result sets ((x int, y int))", 8114,
            "Error converting data type varchar(5) to int.");

    [TestMethod]
    public void ValueConversionFailure_ReportsDeclaredNumericSpelling()
        => WithProcedure("select 'hello' as a").AssertSqlError(
            "exec dbo.p with result sets ((x numeric(5,2)))", 8114,
            "Error converting data type varchar(5) to numeric(5,2).");

    [TestMethod]
    public void DeclaredTypeNames_RenderCanonically()
    {
        // Real spells the declared type from the catalog, not as written: an
        // uppercase declaration and the ANSI synonym both report `nvarchar`,
        // while `numeric` survives the SqlType it shares with `decimal`.
        WithProcedure("select 300 as a").AssertSqlError(
            "exec dbo.p with result sets ((x NVARCHAR(2)))", 8114,
            "Error converting data type int to nvarchar(2).");
        WithProcedure("select 300 as a").AssertSqlError(
            "exec dbo.p with result sets ((x national character varying(2)))", 8114,
            "Error converting data type int to nvarchar(2).");
        WithProcedure("select cast('2020-01-02' as date) as a").AssertSqlError(
            "exec dbo.p with result sets ((x NUMERIC(5,2)))", 11538,
            "EXECUTE statement failed because its WITH RESULT SETS clause specified type 'numeric' for column #1 in result set #1, and the corresponding type sent at run time was 'date'; there is no conversion between the two types.");
    }

    [TestMethod]
    public void NullIntoNotNullColumn_Raises11553()
        => WithProcedure("select cast(null as int) as a").AssertSqlError(
            "exec dbo.p with result sets ((x int not null))", 11553,
            "EXECUTE statement failed because its WITH RESULT SETS clause specified a non-nullable type for column #1 in result set #1, and the corresponding value sent at run time was null.");

    [TestMethod]
    public void NotNullViolation_StreamsPrecedingRows()
    {
        // Real raises the violation per row as the set streams, so the rows
        // ahead of the offending one reach the client first.
        using var reader = WithProcedure("select 1 as a union all select null").ExecuteReader(
            "exec dbo.p with result sets ((x int not null))");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        var ex = Throws<SimulatedSqlException>(() => reader.Read());
        AreEqual(11553, ex.Number);
    }

    [TestMethod]
    public void UnspecifiedNullability_AcceptsNull()
        => AreEqual(DBNull.Value, WithProcedure("select cast(null as int) as a").ExecuteScalar(
            "exec dbo.p with result sets ((x int))"));

    [TestMethod]
    public void MultipleSets_EachTakesItsOwnDeclaration()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 1 as a; select 'z' as b, 2 as c end");
        using var reader = sim.ExecuteReader(
            "exec dbo.p with result sets ((one bigint), (two char(3), three varchar(9)))");
        IsTrue(reader.Read());
        AreEqual("one", reader.GetName(0));
        AreEqual(1L, reader.GetInt64(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("two", reader.GetName(0));
        AreEqual("three", reader.GetName(1));
        AreEqual("2", reader.GetString(1));
    }

    [TestMethod]
    public void ALaterSetsMismatch_FailsTheWholeStatement()
    {
        // Divergence: real streams the sets that matched before raising, so a
        // client sees set #1's rows and then the error. The simulator
        // materializes a statement's outcomes before yielding any, so the
        // set-level violation fails the EXECUTE as a whole. Row-level
        // violations inside an accepted set still stream (see
        // NotNullViolation_StreamsPrecedingRows).
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 1 as a; select 2 as b end");
        var ex = sim.AssertSqlError("exec dbo.p with result sets ((one bigint), (two date))", 11538);
        Contains("result set #2", ex.Message);
    }

    [TestMethod]
    public void RowCountOnlyOutcomes_DoNotCountAsResultSets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int)");
        sim.ExecuteBatches("create procedure dbo.p as begin insert t values (1); select 7 as a end");
        AreEqual(7L, sim.ExecuteScalar("exec dbo.p with result sets ((x bigint))"));
    }

    [TestMethod]
    public void ProcedureBodyRunsBeforeTheContractFails()
    {
        // The module executes to completion first; only then does the
        // projection reject its output (probe-confirmed: a body PRINT is
        // delivered before the error).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int)");
        sim.ExecuteBatches("create procedure dbo.p as begin insert t values (1); select 1 as a end");
        _ = sim.AssertSqlError("exec dbo.p with result sets ((x date))", 11538);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void ContractError_IsAttributedToTheProducingModule()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as select 1 as a");
        AreEqual("dbo.p:11538", sim.ExecuteScalar("""
            begin try
                exec dbo.p with result sets ((x date));
            end try
            begin catch
                select error_procedure() + ':' + cast(error_number() as varchar(10));
            end catch
            """));
    }

    [TestMethod]
    public void NestedProcedures_AttributeToTheInnermostProducer()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.inner_p as select 1 as a",
            "create procedure dbo.outer_p as exec dbo.inner_p");
        AreEqual("dbo.inner_p", sim.ExecuteScalar("""
            begin try
                exec dbo.outer_p with result sets ((x date));
            end try
            begin catch
                select error_procedure();
            end catch
            """));
    }

    [TestMethod]
    public void TooFewSets_IsAttributedToTheExecuteStatement()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as select 1 as a");
        AreEqual(DBNull.Value, sim.ExecuteScalar("""
            begin try
                exec dbo.p with result sets ((x int), (y int));
            end try
            begin catch
                select error_procedure();
            end catch
            """));
    }

    [TestMethod]
    public void DynamicSql_AcceptsTheClause()
        => AreEqual(1L, new Simulation().ExecuteScalar(
            "exec ('select 1 as a') with result sets ((x bigint))"));

    [TestMethod]
    public void SpExecuteSql_AcceptsTheClause()
        => AreEqual(1L, new Simulation().ExecuteScalar(
            "exec sp_executesql N'select 1 as a' with result sets ((x bigint))"));

    [TestMethod]
    public void ReturnCodeCapture_CoexistsWithTheClause()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 1 as a; return 9 end");
        using var reader = sim.ExecuteReader("declare @rc int; exec @rc = dbo.p with result sets ((x bigint)); select @rc");
        IsTrue(reader.Read());
        AreEqual(1L, reader.GetInt64(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(9, reader.GetInt32(0));
    }

    [TestMethod]
    public void ArgumentsPrecedeTheClause()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p @i int as select @i as a");
        AreEqual(5L, sim.ExecuteScalar("exec dbo.p 5 with result sets ((x bigint))"));
    }

    [TestMethod]
    public void ImplicitExec_AcceptsTheClause()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as select 1 as a");
        AreEqual(1L, sim.ExecuteScalar("dbo.p with result sets ((x bigint))"));
    }

    [TestMethod]
    public void Recompile_CombinesWithResultSets()
    {
        var sim = WithProcedure("select 1 as a");
        AreEqual(1L, sim.ExecuteScalar("exec dbo.p with recompile, result sets ((x bigint))"));
        AreEqual(1L, sim.ExecuteScalar("exec dbo.p with result sets ((x bigint)), recompile"));
    }

    [TestMethod]
    public void DuplicateResultSetsOption_IsASyntaxError()
        => WithProcedure().ValidateSyntaxError("exec dbo.p with result sets none, result sets none", "result");

    [TestMethod]
    public void SingleSetNeedsDoubledParentheses()
        => WithProcedure().ValidateSyntaxError("exec dbo.p with result sets (x int, y varchar(9))", "x");

    [TestMethod]
    public void EmptyDefinitionList_IsASyntaxError()
        => WithProcedure().ValidateSyntaxError("exec dbo.p with result sets ()", ")");

    [TestMethod]
    public void TrailingTokenAfterTheClause_IsASyntaxError()
        => WithProcedure().ValidateSyntaxError("exec dbo.p with result sets ((x int, y varchar(9))) junk", "junk");

    [TestMethod]
    public void UnknownDeclaredType_Raises2715()
        => _ = WithProcedure().AssertSqlError("exec dbo.p with result sets ((x notatype, y int))", 2715);

    [TestMethod]
    public void InsertExecSource_RejectsTheClause()
    {
        var sim = WithProcedure();
        sim.ValidateSyntaxError(
            "create table #t (a int, b varchar(20)); insert #t exec dbo.p with result sets ((x int, y varchar(20)))",
            "sets");
    }

    [TestMethod]
    public void CteAfterExec_StillParsesAsItsOwnStatement()
    {
        // The WITH is claimed only when an execute option follows it, so a
        // CTE behind an EXEC is untouched.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int)");
        sim.ExecuteBatches("create procedure dbo.p as insert t values (1)");
        AreEqual(4, sim.ExecuteScalar("exec dbo.p; with c as (select 4 as v) select v from c"));
    }

    [TestMethod]
    public void AsObjectForm_IsNotModeled()
    {
        var sim = WithProcedure();
        var ex = Throws<NotSupportedException>(() => sim.ExecuteScalar("exec dbo.p with result sets (as object dbo.shape)"));
        Contains("AS OBJECT", ex.Message);
    }

    [TestMethod]
    public void SkippedBranch_StillParsesTheClause()
    {
        // Skip mode advances the cursor through the option list, so a
        // malformed clause in an un-taken IF branch still raises.
        var sim = WithProcedure();
        sim.ValidateSyntaxError("if 1 = 0 exec dbo.p with result sets (x int)", "x");
    }

    [TestMethod]
    public void ExecuteReader_Extension_UsesTheDeclaredSchema()
    {
        using var reader = WithProcedure("select 1 as a").ExecuteReader(
            "exec dbo.p with result sets ((x smallint))");
        AreEqual(typeof(short), reader.GetFieldType(0));
        IsTrue(reader.Read());
        AreEqual((short)1, reader.GetInt16(0));
    }
}
