using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's many-to-many skip-navigation mapping.
/// EF auto-generates the join table (default name <c>PostTag</c> for
/// <c>Post</c> ↔ <c>Tag</c>) with shadow FK columns and a composite PK,
/// and translates LINQ that walks either skip navigation into the
/// appropriate JOIN chain. No new SQL shape — exercises join-table
/// emission and the implicit FK / composite-PK handling EF derives from
/// the navigation pair.
/// </summary>
[TestClass]
public class EFCoreManyToMany
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Post
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(50)")]
        public string Title { get; set; } = "";

        public ICollection<Tag> Tags { get; set; } = [];
    }

    private sealed class Tag
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(20)")]
        public string Name { get; set; } = "";

        public ICollection<Post> Posts { get; set; } = [];
    }

    private sealed class BlogContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Post> Posts => Set<Post>();
        public DbSet<Tag> Tags => Set<Tag>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Post>().Property(p => p.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Tag>().Property(t => t.Id).ValueGeneratedNever();
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Posts (
                    Id int not null primary key,
                    Title nvarchar(50) not null
                );
                create table Tags (
                    Id int not null primary key,
                    Name nvarchar(20) not null
                );
                create table PostTag (
                    PostsId int not null,
                    TagsId int not null,
                    primary key (PostsId, TagsId)
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static BlogContext SeededContext()
    {
        var context = new BlogContext(CreateSimulation());
        var ops = new Tag { Id = 1, Name = "ops" };
        var dev = new Tag { Id = 2, Name = "dev" };
        var docs = new Tag { Id = 3, Name = "docs" };
        context.Tags.AddRange(ops, dev, docs);
        context.Posts.AddRange(
            new Post { Id = 10, Title = "Deploy", Tags = { ops, dev } },
            new Post { Id = 20, Title = "Style guide", Tags = { docs } },
            new Post { Id = 30, Title = "Roadmap", Tags = { dev, docs } });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void PostsIncludingTags_HydratesNavigation()
    {
        using var context = SeededContext();
        var posts = context.Posts.Include(p => p.Tags).OrderBy(p => p.Id).ToArray();
        Assert.HasCount(3, posts);
        CollectionAssert.AreEquivalent(new[] { "ops", "dev" }, posts[0].Tags.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public void FilterPostsByTagName_WalksJoinNavigation()
    {
        using var context = SeededContext();
        var titles = context.Posts
            .Where(p => p.Tags.Any(t => t.Name == "docs"))
            .OrderBy(p => p.Id)
            .Select(p => p.Title)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Style guide", "Roadmap" }, titles);
    }

    [TestMethod]
    public void FilterTagsByPostId_WalksReverseNavigation()
    {
        using var context = SeededContext();
        var names = context.Tags
            .Where(t => t.Posts.Any(p => p.Id == 10))
            .OrderBy(t => t.Name)
            .Select(t => t.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "dev", "ops" }, names);
    }

    [TestMethod]
    public void CountTagsPerPost_AggregatesAcrossJoin()
    {
        using var context = SeededContext();
        var counts = context.Posts
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Tags.Count })
            .ToArray();
        Assert.AreEqual(2, counts[0].Count);
        Assert.AreEqual(1, counts[1].Count);
        Assert.AreEqual(2, counts[2].Count);
    }

    [TestMethod]
    public void AddTagToExistingPost_InsertsJoinRow()
    {
        using var context = SeededContext();
        var roadmap = context.Posts.Include(p => p.Tags).Single(p => p.Id == 20);
        var dev = context.Tags.Single(t => t.Name == "dev");
        roadmap.Tags.Add(dev);
        _ = context.SaveChanges();

        using var refresh = new BlogContext(context.Simulation);
        var tagNames = refresh.Posts.Include(p => p.Tags).Single(p => p.Id == 20)
            .Tags.Select(t => t.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(new[] { "dev", "docs" }, tagNames);
    }
}
