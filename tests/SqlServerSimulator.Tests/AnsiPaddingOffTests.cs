using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Columns created under <c>SET ANSI_PADDING OFF</c>: what they report and
/// what they store. Every expectation probed 2026-10-06 against SQL Server
/// 2025.
/// </summary>
[TestClass]
public sealed class AnsiPaddingOffTests
{
    private const string Table = """
        set ansi_padding off;
        create table t (vc varchar(10), nvc nvarchar(10), c char(5) null, cn char(5) not null, vb varbinary(10), b binary(4) null, bn binary(4) not null, vm varchar(max), i int);
        set ansi_padding on;
        """;

    [TestMethod]
    public void Catalog_ReportsTheSingleByteColumnsUnpadded()
        => AreEqual("vc=0,nvc=1,c=0,cn=0,vb=0,b=0,bn=0,vm=0,i=0", new Simulation().ExecuteScalar($"""
            {Table}
            select string_agg(concat(name, '=', cast(is_ansi_padded as int)), ',') within group (order by column_id) from sys.columns where object_id = object_id('t')
            """));

    [TestMethod]
    public void Writes_TrimToOneCharacterOrByte_ButLeaveMaxAndNotNullAlone()
        => AreEqual("2|8|2|5|1|1|4|3|[ab]", new Simulation().ExecuteScalar($"""
            {Table}
            insert t values ('ab  ', N'ab  ', 'ab', 'ab', 0x0100, 0x01, 0x01, 'x  ', 1);
            select concat_ws('|', datalength(vc), datalength(nvc), datalength(c), datalength(cn), datalength(vb), datalength(b), datalength(bn), datalength(vm), '[' + c + ']') from t
            """));

    [TestMethod]
    public void BlankValues_KeepOneSpaceOrZero_EmptyStaysEmpty()
        => AreEqual("0|1|1,1|1|1", new Simulation().ExecuteScalar("""
            set ansi_padding off;
            create table t (id int, vc varchar(10), c char(3) null, vb varbinary(5));
            set ansi_padding on;
            insert t values (1, '', '', 0x00), (2, ' ', 'a ', 0x0000);
            select string_agg(concat_ws('|', datalength(vc), datalength(c), datalength(vb)), ',') within group (order by id) from t
            """));

    [TestMethod]
    public void Update_TrimsToo()
        => AreEqual("2|1", new Simulation().ExecuteScalar("""
            set ansi_padding off;
            create table t (vc varchar(10), c char(5) null);
            set ansi_padding on;
            insert t values ('a', 'a');
            update t set vc = 'zz   ', c = 'q ';
            select concat_ws('|', datalength(vc), datalength(c)) from t
            """));

    [TestMethod]
    public void UsesAnsiTrim_FollowsTheColumn_SqlVariantAside()
        => AreEqual("0|0|1|1", new Simulation().ExecuteScalar("""
            set ansi_padding off;
            create table t (c char(5) null, vm varchar(max), s sql_variant);
            set ansi_padding on;
            create table u (c char(5) null);
            select concat_ws('|', columnproperty(object_id('t'), 'c', 'UsesAnsiTrim'), columnproperty(object_id('t'), 'vm', 'UsesAnsiTrim'),
                columnproperty(object_id('t'), 's', 'UsesAnsiTrim'), columnproperty(object_id('u'), 'c', 'UsesAnsiTrim'))
            """));

    [TestMethod]
    public void SelectInto_KeepsASourceColumnsTrimmingForm_AndCopiesVarcharAsIs()
        => AreEqual("c=0:2,v=0:3", new Simulation().ExecuteScalar("""
            set ansi_padding off;
            create table t (c char(5) null);
            select cast('a  ' as varchar(10)) v into dbo.s;
            set ansi_padding on;
            insert t values ('ab');
            select c into dbo.r from t;
            select concat('c=', (select cast(is_ansi_padded as int) from sys.columns where object_id = object_id('dbo.r')), ':', (select datalength(c) from dbo.r),
                ',v=', (select cast(is_ansi_padded as int) from sys.columns where object_id = object_id('dbo.s')), ':', (select datalength(v) from dbo.s))
            """));

    [TestMethod]
    public void AlterColumn_PadsAgain()
        => AreEqual("1|1|3|6", new Simulation().ExecuteScalar("""
            set ansi_padding off;
            create table t (vv varchar(5), cc char(3) null);
            alter table t alter column vv varchar(6);
            set ansi_padding on;
            alter table t alter column cc char(6) null;
            insert t values ('a  ', 'b');
            select concat_ws('|', (select cast(is_ansi_padded as int) from sys.columns where object_id = object_id('t') and name = 'vv'),
                (select cast(is_ansi_padded as int) from sys.columns where object_id = object_id('t') and name = 'cc'), datalength(vv), datalength(cc)) from t
            """));

    [TestMethod]
    public void AlterTableAdd_FollowsTheSession()
        => AreEqual("1|1|3", new Simulation().ExecuteScalar("""
            create table t (a int);
            set ansi_padding off;
            alter table t add vc varchar(10), c char(5) null;
            set ansi_padding on;
            alter table t add vc2 varchar(10);
            insert t values (1, 'x  ', 'y', 'z  ');
            select concat_ws('|', datalength(vc), datalength(c), datalength(vc2)) from t
            """));

    [TestMethod]
    public void TableVariables_StayPadded()
        => AreEqual("3|4", new Simulation().ExecuteScalar("""
            set ansi_padding off;
            declare @t table (v varchar(10), c char(4) null);
            insert @t values ('q  ', 'w');
            select concat_ws('|', datalength(v), datalength(c)) from @t
            """));
}
