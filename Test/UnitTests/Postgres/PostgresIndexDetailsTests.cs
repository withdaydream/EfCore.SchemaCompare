using System;
using System.Linq;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

/// <summary>
/// An index's filter, method, and each column's sort order, nulls order and operator class are compared
/// </summary>
public class PostgresIndexDetailsTests
{
    private readonly ITestOutputHelper _output;

    public PostgresIndexDetailsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void IndexesEfCoreCreatedMatch()
    {
        //ATTEMPT
        //PostgreSQL rewrites the filters, e.g. as ((deleted_at IS NULL) AND (status <> 'draft'::text)), stores the method,
        //sort orders, nulls orders and operator classes EF Core doesn't set as their defaults, and gives ix_post_status_url's
        //url the url column's collation, C
        var errors = CompareAfterRunning(null);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Fact]
    public void DifferentIndexDetailsAreReported()
    {
        //ATTEMPT
        var errors = CompareAfterRunning(
            """
            DROP INDEX ix_post_slug;
            CREATE UNIQUE INDEX ix_post_slug ON post (slug);
            DROP INDEX ix_post_deleted_at;
            CREATE INDEX ix_post_deleted_at ON post (deleted_at) WHERE deleted_at IS NOT NULL;
            DROP INDEX ix_post_created_at_title;
            CREATE INDEX ix_post_created_at_title ON post (created_at, title);
            DROP INDEX ix_post_title;
            CREATE INDEX ix_post_title ON post (title NULLS FIRST);
            DROP INDEX ix_post_tags;
            CREATE INDEX ix_post_tags ON post USING btree (tags);
            DROP INDEX ix_post_body_slug;
            CREATE INDEX ix_post_body_slug ON post (body text_pattern_ops DESC, slug DESC);
            DROP INDEX ix_post_kind;
            CREATE INDEX ix_post_kind ON post (kind) WHERE kind = 'ROUTINE' AND payload->>'origin' = 'MANUAL';
            DROP INDEX ix_post_status_url;
            CREATE INDEX ix_post_status_url ON post (status COLLATE "C", url);
            """);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "DIFFERENT: Post->Index 'body,slug', operator classes. Expected = default,default, found = text_pattern_ops,default",
            "DIFFERENT: Post->Index 'created_at,title', sort order. Expected = DESC,ASC, found = ASC,ASC",
            "DIFFERENT: Post->Index 'deleted_at', filter. Expected = <null>, found = (deleted_at IS NOT NULL)",
            "DIFFERENT: Post->Index 'kind', filter. Expected = \"kind\" = 'ROUTINE' AND payload->>'origin' = 'SCHEDULED', found = (((kind)::text = 'ROUTINE'::text) AND ((payload ->> 'origin'::text) = 'MANUAL'::text))",
            "DIFFERENT: Post->Index 'slug', filter. Expected = deleted_at IS NULL AND status <> 'draft', found = <null>",
            "DIFFERENT: Post->Index 'status,url', collation. Expected = default,C, found = C,C",
            "DIFFERENT: Post->Index 'tags', method. Expected = gin, found = btree",
            "DIFFERENT: Post->Index 'title', nulls order. Expected = NULLS LAST, found = NULLS FIRST",
            "DIFFERENT: Post->Index 'title', operator classes. Expected = text_pattern_ops, found = text_ops",
        });
    }

    //------------------------------------------------------

    private string[] CompareAfterRunning(string sql)
    {
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = new PostContext(new DbContextOptionsBuilder<PostContext>().UseNpgsql(connectionString).Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        if (sql != null)
            context.Database.ExecuteSqlRaw(sql);

        var comparer = new CompareEfSql();
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);
        _output.WriteLine(comparer.GetAllErrors);
        //Sorted, as the order the errors are found in isn't what is being tested
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private class Post
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public string Slug { get; set; }
        public string Status { get; set; }
        public string Kind { get; set; }
        public string Payload { get; set; }
        public string Url { get; set; }
        public string[] Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    private class PostContext : DbContext
    {
        public PostContext(DbContextOptions<PostContext> options) : base(options) { }

        public DbSet<Post> Posts { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Post>(entity =>
            {
                entity.ToTable("post");
                entity.HasKey(e => e.Id).HasName("pk_post");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Title).HasColumnName("title");
                entity.Property(e => e.Body).HasColumnName("body");
                entity.Property(e => e.Slug).HasColumnName("slug");
                entity.Property(e => e.Status).HasColumnName("status");
                entity.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(50);
                entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
                entity.Property(e => e.Url).HasColumnName("url").UseCollation("C");
                entity.Property(e => e.Tags).HasColumnName("tags");
                entity.Property(e => e.CreatedAt).HasColumnName("created_at");
                entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

                entity.HasIndex(e => e.Slug).HasDatabaseName("ix_post_slug").IsUnique()
                    .HasFilter("deleted_at IS NULL AND status <> 'draft'");
                entity.HasIndex(e => e.DeletedAt).HasDatabaseName("ix_post_deleted_at");
                entity.HasIndex(e => new { e.CreatedAt, e.Title }).HasDatabaseName("ix_post_created_at_title")
                    .IsDescending(true, false).HasNullSortOrder(NullSortOrder.NullsLast, NullSortOrder.Unspecified);
                entity.HasIndex(e => e.Title).HasDatabaseName("ix_post_title").HasOperators("text_pattern_ops");
                entity.HasIndex(e => e.Tags).HasDatabaseName("ix_post_tags").HasMethod("gin");
                //No values means every column is descending
                entity.HasIndex(e => new { e.Body, e.Slug }).HasDatabaseName("ix_post_body_slug").IsDescending();
                //kind is a varchar, so PostgreSQL adds a cast to text, and ->> is a JSON operator
                entity.HasIndex(e => e.Kind).HasDatabaseName("ix_post_kind")
                    .HasFilter("\"kind\" = 'ROUTINE' AND payload->>'origin' = 'SCHEDULED'");
                //The index doesn't give url a collation, so it has the url column's
                entity.HasIndex(e => new { e.Status, e.Url }).HasDatabaseName("ix_post_status_url");
            });
        }
    }
}
