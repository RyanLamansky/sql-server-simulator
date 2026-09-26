namespace SqlServerSimulator;

/// <summary>
/// <c>OBJECTPROPERTY</c> across the documented properties and the modeled
/// object kinds: which kinds each property answers for, and NULL for the rest.
/// Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ObjectPropertyMatrixTests
{
    private static readonly Simulation Objects = Build();

    private static Simulation Build()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table par (id int primary key)",
            """
            create table t (id int identity constraint pk_t primary key clustered, u int constraint uq_t unique nonclustered,
                c int constraint ck_t check (c > 0), d int constraint df_t default 1,
                f int constraint fk_t foreign key references par(id) on delete cascade, g uniqueidentifier rowguidcol, ts rowversion, tx text)
            """,
            "create table h (a int, b int, m varchar(max), constraint ck_multi check (a > b))",
            "alter table h nocheck constraint ck_multi",
            "create view v with schemabinding as select id, u from dbo.t",
            "create view v2 as select a from h",
            "create procedure p as select 1",
            "create function f() returns int with schemabinding as begin return 1 end",
            "create function itf() returns table as return select 1 x",
            "create function mtf() returns @r table (a int) as begin return end",
            "create trigger tr_i on t after insert as select 1",
            "create trigger tr_ud on t after update, delete not for replication as select 1",
            "create trigger tr_io on v2 instead of insert as select 1",
            "create sequence s",
            "create synonym sn for t");
        return sim;
    }

    [TestMethod]
    [DataRow("t", "IsUserTable", "1")]
    [DataRow("p", "IsExecuted", "1")]
    [DataRow("s", "IsExecuted", "0")]
    [DataRow("ck_t", "IsConstraint", "1")]
    [DataRow("ck_t", "IsCheckCnst", "1")]
    [DataRow("sn", "IsCheckCnst", "0")]
    [DataRow("uq_t", "IsUniqueCnst", "1")]
    [DataRow("fk_t", "IsForeignKey", "1")]
    [DataRow("pk_t", "IsPrimaryKey", "1")]
    [DataRow("df_t", "IsDefaultCnst", "1")]
    [DataRow("pk_t", "IsEncrypted", "NULL")]
    [DataRow("ck_t", "IsEncrypted", "0")]
    [DataRow("pk_t", "CnstIsClustKey", "1")]
    [DataRow("uq_t", "CnstIsNonclustKey", "1")]
    [DataRow("ck_t", "CnstIsColumn", "1")]
    [DataRow("ck_multi", "CnstIsColumn", "0")]
    [DataRow("pk_t", "CnstIsColumn", "0")]
    [DataRow("ck_multi", "CnstIsDisabled", "1")]
    [DataRow("ck_multi", "CnstIsNotTrusted", "1")]
    [DataRow("fk_t", "CnstIsDeleteCascade", "1")]
    [DataRow("t", "CnstIsColumn", "NULL")]
    [DataRow("p", "ExecIsAfterTrigger", "0")]
    [DataRow("v", "ExecIsFirstInsertTrigger", "0")]
    [DataRow("t", "ExecIsAfterTrigger", "NULL")]
    [DataRow("tr_ud", "ExecIsDeleteTrigger", "1")]
    [DataRow("tr_io", "ExecIsInsteadOfTrigger", "1")]
    [DataRow("tr_ud", "ExecIsTriggerNotForRepl", "1")]
    [DataRow("f", "ExecIsStartup", "0")]
    [DataRow("t", "HasAfterTrigger", "1")]
    [DataRow("v2", "HasInsteadOfTrigger", "1")]
    [DataRow("v2", "HasInsertTrigger", "1")]
    [DataRow("p", "HasInsertTrigger", "NULL")]
    [DataRow("v", "IsIndexable", "1")]
    [DataRow("v2", "IsIndexable", "0")]
    [DataRow("par", "IsIndexed", "1")]
    [DataRow("h", "IsIndexed", "0")]
    [DataRow("f", "IsSystemVerified", "1")]
    [DataRow("itf", "IsSystemVerified", "0")]
    [DataRow("p", "IsSystemVerified", "NULL")]
    [DataRow("ck_t", "OwnerId", "1")]
    [DataRow("t", "TableHasDefaultCnst", "1")]
    [DataRow("t", "TableHasNonclustIndex", "1")]
    [DataRow("t", "TableHasTextImage", "1")]
    [DataRow("h", "TableHasTextImage", "0")]
    [DataRow("t", "TableHasTimestamp", "1")]
    [DataRow("t", "TableInsertTriggerCount", "1")]
    [DataRow("h", "TableDeleteTriggerCount", "0")]
    [DataRow("itf", "TableHasPrimaryKey", "0")]
    [DataRow("mtf", "TableIsFake", "1")]
    [DataRow("itf", "TableIsFake", "0")]
    [DataRow("v", "TableHasCheckCnst", "NULL")]
    [DataRow("t", "TableFulltextCatalogId", "0")]
    [DataRow("mtf", "TableHasActiveFulltextIndex", "0")]
    [DataRow("t", "TableTemporalType", "0")]
    [DataRow("h", "TableIsMemoryOptimized", "0")]
    public void AProperty_AnswersForTheKindsItConcerns(string objectName, string property, string expected)
        => Assert.AreEqual(expected, Objects.ExecuteScalar($"select isnull(cast(objectproperty(object_id('{objectName}'), '{property}') as varchar), 'NULL')"));

    [TestMethod]
    [DataRow("TableInsertTrigger", "tr_i")]
    [DataRow("TableDeleteTrigger", "tr_ud")]
    public void TableTrigger_NamesTheTrigger(string property, string expected)
        => Assert.AreEqual(expected, Objects.ExecuteScalar($"select object_name(objectproperty(object_id('t'), '{property}'))"));

    /// <summary>OBJECT_ID resolves every schema-scoped kind, a type filter matching its own sys.objects type.</summary>
    [TestMethod]
    [DataRow("s", null, true)]
    [DataRow("dbo.s", "SO", true)]
    [DataRow("mtf", "TF", true)]
    [DataRow("mtf", "U", false)]
    [DataRow("s", "V", false)]
    public void ObjectId_ResolvesEveryKind(string name, string? type, bool resolves)
        => Assert.AreEqual(resolves ? 1 : 0, Objects.ExecuteScalar(type is null
            ? $"select iif(object_id('{name}') is null, 0, 1)"
            : $"select iif(object_id('{name}', '{type}') is null, 0, 1)"));

    [TestMethod]
    public void TheExForm_Agrees()
        => Assert.AreEqual(1, Objects.ExecuteScalar("select cast(objectpropertyex(object_id('ck_t'), 'IsConstraint') as int)"));
}
