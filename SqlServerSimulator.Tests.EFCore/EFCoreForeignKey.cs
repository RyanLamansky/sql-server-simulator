using Microsoft.EntityFrameworkCore;
using SqlServerSimulator.EFCore;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end test for EF Core's relationship modeling against the simulator.
/// <c>HasOne</c>/<c>WithMany</c> emits a FOREIGN KEY constraint in the
/// SqlServer-flavored CREATE TABLE shape; the simulator's parser + enforcer
/// handle it on the round-tripped SaveChanges path. Tables are bootstrapped
/// via raw CREATE TABLE statements (EF Core's <c>EnsureCreated</c> isn't
/// modeled — see <see cref="EFCoreHiLo"/> for the same pattern).
/// </summary>
[TestClass]
public sealed class EFCoreForeignKey
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new LibraryContext(new Simulation()));

    private sealed class Author
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Book> Books { get; } = [];
    }

    private sealed class Book
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int AuthorId { get; set; }
        public Author Author { get; set; } = null!;
    }

    private sealed class LibraryContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Author> Authors => Set<Author>();
        public DbSet<Book> Books => Set<Book>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlServerSimulator(this.Simulation.CreateDbConnection());

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Author>(b =>
            {
                _ = b.Property(a => a.Name).HasColumnType("nvarchar(50)").IsRequired();
            });
            _ = modelBuilder.Entity<Book>(b =>
            {
                _ = b.Property(p => p.Title).HasColumnType("nvarchar(100)").IsRequired();
                _ = b.HasOne(p => p.Author)
                    .WithMany(a => a.Books)
                    .HasForeignKey(p => p.AuthorId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }

    private static Simulation CreateLibrarySimulation()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table Authors (
                Id int not null identity(1,1) primary key,
                Name nvarchar(50) not null
            );
            create table Books (
                Id int not null identity(1,1) primary key,
                Title nvarchar(100) not null,
                AuthorId int not null,
                constraint fk_book_author foreign key (AuthorId) references Authors(Id) on delete cascade
            )
            """);
        return simulation;
    }

    [TestMethod]
    public void SaveChanges_ParentAndChild_RoundTripsThroughFkColumn()
    {
        using var context = new LibraryContext(CreateLibrarySimulation());
        var author = new Author { Name = "Ada Lovelace" };
        author.Books.Add(new Book { Title = "Notes on the Analytical Engine" });
        author.Books.Add(new Book { Title = "Sketch of the Engine" });
        _ = context.Authors.Add(author);
        _ = context.SaveChanges();

        Assert.AreEqual(1, context.Authors.AsNoTracking().Count());
        Assert.AreEqual(2, context.Books.AsNoTracking().Count());
        Assert.AreEqual(2, context.Books.AsNoTracking().Count(b => b.AuthorId == author.Id));
    }

    [TestMethod]
    public void SaveChanges_BookWithoutAuthor_RaisesDbUpdateException()
    {
        // FK violation on the child INSERT surfaces as DbUpdateException
        // (the simulator's Msg 547 in the inner exception).
        using var context = new LibraryContext(CreateLibrarySimulation());
        _ = context.Books.Add(new Book { Title = "Orphan", AuthorId = 999 });
        var ex = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("FOREIGN KEY constraint", ex.InnerException.Message);
    }

    [TestMethod]
    public void DeleteAuthor_CascadesToBooks()
    {
        // ON DELETE CASCADE on the FK propagates the parent delete to child
        // rows. EF Core's client-side cascade fixup is bypassed by using
        // raw SQL DELETE on the connection — exercising the simulator's
        // server-side cascade engine.
        using var context = new LibraryContext(CreateLibrarySimulation());
        var author = new Author { Name = "Marie Curie" };
        author.Books.Add(new Book { Title = "Recherches sur les substances radioactives" });
        _ = context.Authors.Add(author);
        _ = context.SaveChanges();
        Assert.AreEqual(1, context.Books.AsNoTracking().Count());

        using var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "delete from Authors";
        _ = cmd.ExecuteNonQuery();

        Assert.AreEqual(0, context.Authors.AsNoTracking().Count());
        Assert.AreEqual(0, context.Books.AsNoTracking().Count());
    }
}
