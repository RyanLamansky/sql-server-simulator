using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's eager-loading surface:
/// <c>Include</c> / <c>ThenInclude</c> walk navigation collections
/// via LEFT JOIN in a single query (the default). <c>AsSplitQuery()</c>
/// switches to one query per Include level, avoiding the cartesian-
/// explosion that JOIN-based eager loading causes with multiple
/// collection navigations. Both shapes are common in real apps.
/// </summary>
[TestClass]
public class EFCoreEagerSplitQueries
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Blog
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(50)")]
        public string Name { get; set; } = "";

        public List<Post> Posts { get; set; } = [];
    }

    private sealed class Post
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(50)")]
        public string Title { get; set; } = "";

        public int BlogId { get; set; }
        public Blog? Blog { get; set; }

        public List<Comment> Comments { get; set; } = [];
    }

    private sealed class Comment
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(100)")]
        public string Text { get; set; } = "";

        public int PostId { get; set; }
        public Post? Post { get; set; }
    }

    private sealed class BlogContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Blog> Blogs => Set<Blog>();
        public DbSet<Post> Posts => Set<Post>();
        public DbSet<Comment> Comments => Set<Comment>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Blog>().Property(b => b.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Post>().Property(p => p.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Comment>().Property(c => c.Id).ValueGeneratedNever();
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Blogs (
                    Id int not null primary key,
                    Name nvarchar(50) not null
                );
                create table Posts (
                    Id int not null primary key,
                    Title nvarchar(50) not null,
                    BlogId int not null
                );
                create table Comments (
                    Id int not null primary key,
                    Text nvarchar(100) not null,
                    PostId int not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static BlogContext SeededContext()
    {
        var context = new BlogContext(CreateSimulation());
        _ = context.Blogs.Add(new Blog
        {
            Id = 1,
            Name = "Tech",
            Posts =
            {
                new Post
                {
                    Id = 10,
                    Title = "Hello",
                    BlogId = 1,
                    Comments = { new Comment { Id = 100, Text = "first", PostId = 10 } },
                },
                new Post
                {
                    Id = 11,
                    Title = "World",
                    BlogId = 1,
                    Comments =
                    {
                        new Comment { Id = 110, Text = "ok", PostId = 11 },
                        new Comment { Id = 111, Text = "thanks", PostId = 11 },
                    },
                },
            },
        });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Include_LoadsCollectionInSameQuery()
    {
        using var context = SeededContext();
        var blog = context.Blogs.Include(b => b.Posts).Single(b => b.Id == 1);
        Assert.HasCount(2, blog.Posts);
        CollectionAssert.AreEquivalent(new[] { "Hello", "World" }, blog.Posts.Select(p => p.Title).ToArray());
    }

    [TestMethod]
    public void IncludeThenInclude_LoadsTwoLevels()
    {
        using var context = SeededContext();
        var blog = context.Blogs
            .Include(b => b.Posts)
            .ThenInclude(p => p.Comments)
            .Single(b => b.Id == 1);
        var totalComments = blog.Posts.Sum(p => p.Comments.Count);
        Assert.AreEqual(3, totalComments);
    }

    [TestMethod]
    public void AsSplitQuery_LoadsCollectionsViaSeparateQueries()
    {
        using var context = SeededContext();
        var blog = context.Blogs
            .Include(b => b.Posts)
            .ThenInclude(p => p.Comments)
            .AsSplitQuery()
            .Single(b => b.Id == 1);
        var totalComments = blog.Posts.Sum(p => p.Comments.Count);
        Assert.AreEqual(3, totalComments);
    }

    [TestMethod]
    public void InverseInclude_WalksNonCollectionNavigation()
    {
        using var context = SeededContext();
        var post = context.Posts.Include(p => p.Blog).Single(p => p.Id == 10);
        Assert.IsNotNull(post.Blog);
        Assert.AreEqual("Tech", post.Blog!.Name);
    }
}
