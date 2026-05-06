using Microsoft.EntityFrameworkCore;
using SqlServerSimulator.EFCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the SqlServerSimulator EF Core adapter's narrowed date/time
/// mappings: <c>DateOnly → date</c>, <c>DateTime → date</c>,
/// <c>DateTime → smalldatetime</c>, <c>TimeOnly → time(N)</c>, and
/// <c>TimeSpan → time(N)</c>. Each pair would throw at SaveChanges under
/// vanilla <c>UseSqlServer</c> because the SqlServer provider's mappings
/// downcast <see cref="System.Data.Common.DbParameter"/> to <c>SqlParameter</c>;
/// the adapter substitutes provider-agnostic mappings that set
/// <see cref="System.Data.DbType"/> directly.
/// </summary>
internal class Schedule
{
    public int Id { get; set; }

    [Column(TypeName = "date")]
    public DateOnly Birthday { get; set; }

    [Column(TypeName = "date")]
    public DateOnly? Anniversary { get; set; }

    [Column(TypeName = "date")]
    public DateTime PlanStart { get; set; }

    [Column(TypeName = "smalldatetime")]
    public DateTime CheckIn { get; set; }

    [Column(TypeName = "time(7)")]
    public TimeOnly DailyAlarm { get; set; }

    [Column(TypeName = "time(3)")]
    public TimeOnly? Snooze { get; set; }

    [Column(TypeName = "time(7)")]
    public TimeSpan ShiftLength { get; set; }

    /// <remarks>
    /// <c>Break</c> is a reserved T-SQL keyword (used by the loop control
    /// flow statement); the simulator's CREATE TABLE parser tokenizes it
    /// as such, so the column is bracket-quoted in
    /// <see cref="AdapterTestDbContext.CreateSchedulesSimulation"/>. EF
    /// Core's SqlServer provider already brackets every identifier it
    /// emits, so the SaveChanges/SELECT path is unaffected.
    /// </remarks>
    [Column(TypeName = "time(0)")]
    public TimeSpan? Break { get; set; }
}

/// <summary>
/// Exercises the SqlServerSimulator EF Core adapter's <c>decimal → money</c>
/// and <c>decimal → smallmoney</c> mappings. Both pairs route through the
/// substitute <see cref="Microsoft.EntityFrameworkCore.Storage.DecimalTypeMapping"/>
/// which sets <see cref="System.Data.DbType.Currency"/> rather than calling
/// the SqlServer provider's <c>SqlServerDecimalTypeMapping</c> downcast.
/// </summary>
internal class Invoice
{
    public int Id { get; set; }

    [Column(TypeName = "money")]
    public decimal Amount { get; set; }

    [Column(TypeName = "smallmoney")]
    public decimal Surcharge { get; set; }

    [Column(TypeName = "money")]
    public decimal? Tip { get; set; }

    [Column(TypeName = "smallmoney")]
    public decimal? Discount { get; set; }
}

/// <summary>
/// Variant of <see cref="TestDbContext"/> that registers the
/// SqlServerSimulator EF Core adapter via
/// <c>UseSqlServerSimulator</c> and exposes the <see cref="Schedule"/> /
/// <see cref="Invoice"/> entities — i.e. the entities whose property
/// types route through the (CLR, store) pairs the SqlServer provider
/// would otherwise downcast to <c>SqlParameter</c>. Tests that don't
/// touch those pairs should continue to use <see cref="TestDbContext"/>
/// directly so the no-adapter EF Core path stays exercised.
/// </summary>
internal class AdapterTestDbContext(Simulation simulation) : TestDbContext(simulation)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        _ = optionsBuilder.UseSqlServerSimulator(this.Simulation.CreateDbConnection());
    }

    public DbSet<Schedule> Schedules => Set<Schedule>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public static Simulation CreateSchedulesSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Schedules (
                    Id int,
                    Birthday date not null,
                    Anniversary date null,
                    PlanStart date not null,
                    CheckIn smalldatetime not null,
                    DailyAlarm time(7) not null,
                    Snooze time(3) null,
                    ShiftLength time(7) not null,
                    [Break] time(0) null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateInvoicesSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Invoices (
                    Id int,
                    Amount money not null,
                    Surcharge smallmoney not null,
                    Tip money null,
                    Discount smallmoney null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }
}
