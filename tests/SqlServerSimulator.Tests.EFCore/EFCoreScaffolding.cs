using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Drives EF Core 10's reverse engineering — the provider's database-model
/// factory, which <c>dotnet ef dbcontext scaffold</c> runs — against the
/// simulator. The factory's catalog queries are EF's own text; each assertion
/// is a model fact the same schema produced against SQL Server 2025 (compared
/// 2026-10-02).
/// </summary>
[TestClass]
public class EFCoreScaffolding
{
    private static DatabaseModel Scaffold(Simulation simulation)
    {
        using var context = new DbContext(new DbContextOptionsBuilder().UseSqlServer(simulation.CreateDbConnection()).Options);
#pragma warning disable EF1001 // The factory is provider-internal API; it is what the scaffolding tool resolves and runs.
        var factory = new Microsoft.EntityFrameworkCore.SqlServer.Scaffolding.Internal.SqlServerDatabaseModelFactory(
            context.GetService<IDiagnosticsLogger<DbLoggerCategory.Scaffolding>>(),
            context.GetService<IRelationalTypeMappingSource>());
        using var connection = simulation.CreateDbConnection();
        return factory.Create(connection, new DatabaseModelFactoryOptions());
#pragma warning restore EF1001
    }

    private static Simulation Schema()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create schema sales",
            "create sequence dbo.OrderNumbers as int start with 1000 increment by 5; create sequence sales.Big as bigint minvalue 1 maxvalue 999999 cycle; create sequence dbo.Later",
            """
            create table dbo.Customers (
                Id int identity not null constraint PK_Customers primary key,
                Name nvarchar(100) not null,
                Email varchar(200) collate Latin1_General_CS_AS null constraint UQ_Customers_Email unique,
                Balance decimal(18, 2) not null constraint DF_Balance default 0,
                Number bigint null constraint DF_Number default (next value for dbo.OrderNumbers),
                Score float null constraint DF_Score default 1.5e-2,
                Rv rowversion,
                Stored as (Balance * 2) persisted)
            """,
            """
            create table sales.Orders (
                OrderId bigint not null, Line int not null, CustomerId int null, Status varchar(10) not null,
                constraint PK_Orders primary key nonclustered (OrderId, Line),
                constraint FK_Orders_Customers foreign key (CustomerId) references dbo.Customers (Id) on delete set null);
            create clustered index CIX_Orders on sales.Orders (CustomerId);
            create unique index IX_Status on sales.Orders (Status, Line desc) where Status <> 'void'
            """,
            "create table dbo.Codes (Code varchar(5) not null, Label nvarchar(20) null); create unique index UX_Codes on dbo.Codes (Code)",
            "create table dbo.Uses (Code varchar(5) not null constraint FK_Uses_Codes references dbo.Codes (Code))",
            "exec sp_addextendedproperty 'MS_Description', 'The customers', 'SCHEMA', 'dbo', 'TABLE', 'Customers'");
        return simulation;
    }

    [TestMethod]
    public void Scaffold_ReadsDefaultsKeysIndexesAndComments()
    {
        var model = Scaffold(Schema());
        var customers = model.Tables.Single(t => t.Name == "Customers");
        Assert.AreEqual("The customers", customers.Comment);
        Assert.AreEqual("PK_Customers", customers.PrimaryKey!.Name);
        Assert.AreEqual("UQ_Customers_Email", customers.UniqueConstraints.Single().Name);
        Assert.AreEqual("Latin1_General_CS_AS", customers.Columns.Single(c => c.Name == "Email").Collation);
        Assert.AreEqual("((0))", customers.Columns.Single(c => c.Name == "Balance").DefaultValueSql);
        Assert.AreEqual("([Balance]*(2))", customers.Columns.Single(c => c.Name == "Stored").ComputedColumnSql);
        Assert.IsTrue(customers.Columns.Single(c => c.Name == "Stored").IsStored);

        // A float default reads the stored double to seventeen digits, and a
        // sequence default its bracketed name without the written parentheses.
        Assert.AreEqual("((1.4999999999999999e-002))", customers.Columns.Single(c => c.Name == "Score").DefaultValueSql);
        Assert.AreEqual("(NEXT VALUE FOR [dbo].[OrderNumbers])", customers.Columns.Single(c => c.Name == "Number").DefaultValueSql);

        var orders = model.Tables.Single(t => t.Name == "Orders");
        Assert.IsFalse((bool?)orders.PrimaryKey!["SqlServer:Clustered"]);
        var status = orders.Indexes.Single(i => i.Name == "IX_Status");
        Assert.AreEqual("([Status]<>'void')", status.Filter);
        CollectionAssert.AreEqual(new[] { false, true }, status.IsDescending.ToArray());
        Assert.AreEqual(Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.SetNull, orders.ForeignKeys.Single().OnDelete);
    }

    [TestMethod]
    public void Scaffold_AForeignKeyOnAUniqueIndex_ReadsItsPrincipalColumns()
    {
        var foreignKey = Scaffold(Schema()).Tables.Single(t => t.Name == "Uses").ForeignKeys.Single();
        Assert.AreEqual("FK_Uses_Codes", foreignKey.Name);
        Assert.AreEqual("Codes", foreignKey.PrincipalTable.Name);
        Assert.AreEqual("Code", foreignKey.PrincipalColumns.Single().Name);
    }

    [TestMethod]
    public void Scaffold_SequencesComeBackInCreationOrder()
    {
        var model = Scaffold(Schema());
        Assert.AreEqual("dbo.OrderNumbers,sales.Big,dbo.Later", string.Join(",", model.Sequences.Select(s => $"{s.Schema}.{s.Name}")));
        var big = model.Sequences.Single(s => s.Name == "Big");
        Assert.IsTrue(big.IsCyclic);
        Assert.AreEqual(999999L, big.MaxValue);
    }
}
