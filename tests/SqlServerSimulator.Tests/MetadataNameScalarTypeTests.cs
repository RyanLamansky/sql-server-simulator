using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// The result type of the metadata-name scalars — <c>nvarchar(128)</c> at
/// coercible-default, never <c>sysname</c> — which is what lets one meet a
/// catalog column's collation without Msg 451. Probed 2026-09-25 against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class MetadataNameScalarTypeTests
{
    [TestMethod]
    public void Results_AreNvarchar128_AndOriginalLoginIsNvarchar4000()
        => AreEqual("nvarchar:256;nvarchar:256;nvarchar:256;nvarchar:256;nvarchar:256;nvarchar:256;nvarchar:8000;sysname:256", ExecuteScalar("""
            create table t (a int);
            select schema_name(1) a, object_name(object_id('t')) b, db_name() c, user_name() d, type_name(56) e, col_name(object_id('t'), 1) f,
                original_login() g, name h
            into #r from sys.objects where object_id = object_id('t');
            select string_agg(concat(type_name(user_type_id), ':', max_length), ';') within group (order by column_id)
            from tempdb.sys.columns where object_id = object_id('tempdb..#r')
            """));

    [TestMethod]
    public void Results_YieldToACatalogColumnsCollation()
        => AreEqual("dboU ", ExecuteScalar("""
            create table t (a int);
            select schema_name(schema_id) + type from sys.objects where object_id = object_id('t')
            """));

    [TestMethod]
    public void SuserId_ReadsTheServerPrincipals()
        => AreEqual("1|1|2|3|10|", ExecuteScalar(
            "select concat(suser_id(), '|', suser_id('sa'), '|', suser_id('public'), '|', suser_id('sysadmin'), '|', suser_id('bulkadmin'), '|', suser_id('nope'))"));
}
