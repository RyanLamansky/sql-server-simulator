using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Per-constraint <c>create_date</c> / <c>modify_date</c>: a constraint
/// declared inside <c>CREATE TABLE</c> shares the table's instant, an
/// <c>ALTER TABLE … ADD CONSTRAINT</c> carries the later one, and a trust
/// toggle (<c>{NOCHECK|CHECK} CONSTRAINT</c>) advances the constraint's
/// <c>modify_date</c> alone. The dates surface through <c>sys.objects</c> and
/// the four per-family views — <c>sys.check_constraints</c>,
/// <c>sys.key_constraints</c>, <c>sys.default_constraints</c>,
/// <c>sys.foreign_keys</c>. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ConstraintCatalogDateTests
{
    /// <summary>
    /// A table with one constraint of each family declared inline, plus a
    /// second table whose constraints all arrive through ALTER TABLE. The
    /// 20 ms wait puts the ALTERs in a later <c>datetime</c> tick (the type
    /// rounds to 1/300 s).
    /// </summary>
    private static Simulation Fixture()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table p (
                id int constraint pk_p primary key,
                v int constraint ck_p check (v > 0),
                w int constraint df_p default 5,
                u int constraint uq_p unique);
            create table c (id int primary key, pid int);
            waitfor delay '00:00:00.020';
            alter table c add constraint fk_c foreign key (pid) references p(id);
            alter table c add constraint ck_c check (pid > 0);
            alter table c add constraint df_c default 1 for pid
            """);
        return simulation;
    }

    private static DateTime TableCreateDate(Simulation simulation, string name)
        => (DateTime)simulation.ExecuteScalar($"select create_date from sys.tables where name = '{name}'")!;

    private static DateTime CreateDate(Simulation simulation, string view, string name)
        => (DateTime)simulation.ExecuteScalar($"select create_date from sys.{view} where name = '{name}'")!;

    [TestMethod]
    public void InlineConstraints_ShareTheTablesCreateDate()
    {
        var simulation = Fixture();
        var table = TableCreateDate(simulation, "p");
        AreEqual(table, CreateDate(simulation, "key_constraints", "pk_p"));
        AreEqual(table, CreateDate(simulation, "key_constraints", "uq_p"));
        AreEqual(table, CreateDate(simulation, "check_constraints", "ck_p"));
        AreEqual(table, CreateDate(simulation, "default_constraints", "df_p"));
    }

    [TestMethod]
    public void AlterTableAddConstraint_CarriesTheLaterCreateDate()
    {
        var simulation = Fixture();
        var table = TableCreateDate(simulation, "c");
        IsGreaterThan(table, CreateDate(simulation, "foreign_keys", "fk_c"));
        IsGreaterThan(table, CreateDate(simulation, "check_constraints", "ck_c"));
        IsGreaterThan(table, CreateDate(simulation, "default_constraints", "df_c"));
    }

    /// <summary>
    /// <c>sys.objects</c> agrees with the per-family view on both dates for the
    /// constraint kinds it carries a row for (PK / UQ / CHECK / FK — a DEFAULT
    /// has no <c>sys.objects</c> row here yet).
    /// </summary>
    [TestMethod]
    public void SysObjects_AgreesWithThePerFamilyViews()
        => AreEqual(3, Fixture().ExecuteScalar("""
            select
                (select count(*) from sys.check_constraints k join sys.objects o on o.object_id = k.object_id
                    where k.create_date = o.create_date and k.modify_date = o.modify_date and k.name = 'ck_c')
              + (select count(*) from sys.key_constraints k join sys.objects o on o.object_id = k.object_id
                    where k.create_date = o.create_date and k.modify_date = o.modify_date and k.name = 'pk_p')
              + (select count(*) from sys.foreign_keys k join sys.objects o on o.object_id = k.object_id
                    where k.create_date = o.create_date and k.modify_date = o.modify_date and k.name = 'fk_c')
            """));

    [TestMethod]
    public void FreshConstraint_HasModifyDateEqualToCreateDate()
    {
        var simulation = Fixture();
        AreEqual(3, simulation.ExecuteScalar("""
            select
                (select count(*) from sys.key_constraints where name in ('pk_p', 'uq_p') and modify_date = create_date)
              + (select count(*) from sys.check_constraints where name = 'ck_p' and modify_date = create_date)
            """));
        AreEqual(1, simulation.ExecuteScalar(
            "select count(*) from sys.default_constraints where name = 'df_p' and modify_date = create_date"));
    }

    [TestMethod]
    public void NoCheckConstraint_AdvancesTheConstraintsModifyDate()
    {
        var simulation = Fixture();
        var created = CreateDate(simulation, "check_constraints", "ck_c");
        _ = simulation.ExecuteNonQuery("waitfor delay '00:00:00.020'; alter table c nocheck constraint ck_c");
        using var reader = simulation.ExecuteReader(
            "select create_date, modify_date from sys.check_constraints where name = 'ck_c'");
        IsTrue(reader.Read());
        AreEqual(created, reader.GetDateTime(0));
        IsGreaterThan(created, reader.GetDateTime(1));
    }

    [TestMethod]
    public void CheckConstraint_AdvancesTheForeignKeysModifyDate()
    {
        var simulation = Fixture();
        var created = CreateDate(simulation, "foreign_keys", "fk_c");
        _ = simulation.ExecuteNonQuery("waitfor delay '00:00:00.020'; alter table c with check check constraint fk_c");
        using var reader = simulation.ExecuteReader(
            "select create_date, modify_date from sys.foreign_keys where name = 'fk_c'");
        IsTrue(reader.Read());
        AreEqual(created, reader.GetDateTime(0));
        IsGreaterThan(created, reader.GetDateTime(1));
    }
}
