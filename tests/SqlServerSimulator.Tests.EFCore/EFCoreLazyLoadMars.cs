using Microsoft.EntityFrameworkCore;
using SqlServerSimulator.EFCore;

namespace SqlServerSimulator;

/// <summary>
/// EF Core lazy-loading proxies over the simulator — the canonical MARS
/// (Multiple Active Result Sets) shape: iterating a parent query keeps its
/// reader open while touching a navigation property fires a second query per
/// row. Under vanilla <c>UseSqlServer</c> that throws "There is already an open
/// DataReader" unless MARS is enabled; the in-process
/// <see cref="SimulatedDbConnection"/> permits the overlap natively (a MARS-
/// enabled superset), and the wire endpoint now negotiates MARS so plain
/// <c>UseSqlServer</c> with <c>MultipleActiveResultSets=True</c> works too.
/// The same model also exercises <c>AsSplitQuery</c> and a nested foreach over
/// two independent queries — both natural MARS shapes.
/// </summary>
[TestClass]
public class EFCoreLazyLoadMars
{
    public TestContext TestContext { get; set; } = null!;

    // Lazy-loading proxies subclass the entities at runtime, so they must be
    // public/protected-constructible and their navigations virtual.
    public class Blog
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public virtual List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        public int Id { get; set; }

        public string Title { get; set; } = "";

        public int BlogId { get; set; }

        public virtual Blog? Blog { get; set; }
    }

    private sealed class BlogContext(Action<DbContextOptionsBuilder> configure) : DbContext
    {
        public DbSet<Blog> Blogs => this.Set<Blog>();

        public DbSet<Post> Posts => this.Set<Post>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            configure(optionsBuilder.UseLazyLoadingProxies());

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Blog>(b =>
            {
                _ = b.Property(x => x.Id).ValueGeneratedNever();
                _ = b.Property(x => x.Name).HasColumnType("nvarchar(50)");
            });
            _ = modelBuilder.Entity<Post>(p =>
            {
                _ = p.Property(x => x.Id).ValueGeneratedNever();
                _ = p.Property(x => x.Title).HasColumnType("nvarchar(50)");
            });
        }
    }

    private static void Seed(Simulation simulation)
    {
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Blogs (Id int not null primary key, Name nvarchar(50) not null);
                create table Posts (Id int not null primary key, Title nvarchar(50) not null, BlogId int not null);
                insert into Blogs values (1, 'Tech'), (2, 'Food'), (3, 'Travel');
                insert into Posts values
                    (10, 'A', 1), (11, 'B', 1), (12, 'C', 1),
                    (20, 'D', 2), (21, 'E', 2),
                    (30, 'F', 3);
                """)
            .ExecuteNonQuery();
    }

    private static Dictionary<int, int> ExpectedCounts() => new() { [1] = 3, [2] = 2, [3] = 1 };

    [TestMethod]
    public void InProcess_LazyLoad_NavigationPerRow_WhileReaderOpen()
    {
        var simulation = new Simulation();
        Seed(simulation);
        using var context = new BlogContext(o => o.UseSqlServerSimulator(simulation.CreateDbConnection()));

        var counts = new Dictionary<int, int>();
        // Streaming the outer query keeps its reader open; touching Posts fires a
        // lazy-load query per blog on the same connection — the overlap the
        // in-process connection permits natively.
        foreach (var blog in context.Blogs.OrderBy(b => b.Id))
            counts[blog.Id] = blog.Posts.Count;

        CollectionAssert.AreEquivalent(ExpectedCounts(), counts);
    }

    [TestMethod]
    public async Task OverWire_LazyLoad_NavigationPerRow_RequiresMars()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString =
            $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=x;TrustServerCertificate=True;" +
            "MultipleActiveResultSets=True;Connect Timeout=15";

        using var context = new BlogContext(o => o.UseSqlServer(connectionString));

        var counts = new Dictionary<int, int>();
        foreach (var blog in context.Blogs.OrderBy(b => b.Id))
            counts[blog.Id] = blog.Posts.Count;

        CollectionAssert.AreEquivalent(ExpectedCounts(), counts);
    }

    [TestMethod]
    public async Task OverWire_SplitQuery_LoadsCollectionsAcrossSessions()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString =
            $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=x;TrustServerCertificate=True;" +
            "MultipleActiveResultSets=True;Connect Timeout=15";

        using var context = new BlogContext(o => o.UseSqlServer(connectionString));

        var blogs = await context.Blogs
            .Include(b => b.Posts)
            .AsSplitQuery()
            .OrderBy(b => b.Id)
            .ToListAsync(TestContext.CancellationToken);

        var counts = blogs.ToDictionary(b => b.Id, b => b.Posts.Count);
        CollectionAssert.AreEquivalent(ExpectedCounts(), counts);
    }

    [TestMethod]
    public async Task OverWire_NestedForeachOverTwoQueries_OnOneContext()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString =
            $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=x;TrustServerCertificate=True;" +
            "MultipleActiveResultSets=True;Connect Timeout=15";

        using var context = new BlogContext(o => o.UseSqlServer(connectionString));

        var pairs = new List<string>();
        // Two independent streaming queries interleaved on one context — the
        // inner reader opens while the outer reader is still draining.
        foreach (var blog in context.Blogs.OrderBy(b => b.Id))
        {
            foreach (var post in context.Posts.Where(p => p.BlogId == blog.Id).OrderBy(p => p.Id))
                pairs.Add($"{blog.Id}:{post.Id}");
        }

        CollectionAssert.AreEqual(
            new[] { "1:10", "1:11", "1:12", "2:20", "2:21", "3:30" },
            pairs);
    }
}
