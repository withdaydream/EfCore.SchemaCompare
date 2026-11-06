using System;
using System.Linq;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestSupport.EfHelpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests;

/// <summary>
/// Collations and comments are compared for every database provider. PostgreSQL's are tested in PostgresColumnSettingsTests
/// </summary>
public class TestCollationsAndComments
{
    private readonly ITestOutputHelper _output;

    public TestCollationsAndComments(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SameCollationsAndCommentsHaveNoErrors()
    {
        //ATTEMPT
        var errors = CompareWithDatabase<SettingsContext>(o => new SettingsContext(o));

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Fact]
    public void CollationsAndCommentsNotInEfAreReported()
    {
        //ATTEMPT
        var errors = CompareWithDatabase<NoSettingsContext>(o => new NoSettingsContext(o));

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "DIFFERENT: Item->Property 'Name', collation. Expected = <null>, found = Latin1_General_BIN2",
            "DIFFERENT: Item->Property 'Name', comment. Expected = <null>, found = the name",
            "DIFFERENT: Table 'items', comment. Expected = <null>, found = items",
        });
    }

    //------------------------------------------------------

    /// <summary>
    /// Creates the database with <see cref="SettingsContext"/>, then compares it with the given DbContext
    /// </summary>
    private string[] CompareWithDatabase<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        using (var context = new SettingsContext(this.CreateUniqueClassOptions<SettingsContext>()))
        {
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();
        }

        using var compareContext = create(this.CreateUniqueClassOptions<TContext>());
        var comparer = new CompareEfSql();
        var hasErrors = comparer.CompareEfWithDb(compareContext);
        _output.WriteLine(comparer.GetAllErrors);
        //Sorted, as the order the errors are found in isn't what is being tested
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private class Item
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    private static void MapItem(EntityTypeBuilder<Item> entity)
    {
        entity.ToTable("items");
    }

    private class SettingsContext : DbContext
    {
        public SettingsContext(DbContextOptions<SettingsContext> options) : base(options) { }

        public DbSet<Item> Items { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>(entity =>
            {
                MapItem(entity);
                entity.ToTable("items", t => t.HasComment("items"));
                entity.Property(e => e.Name).UseCollation("Latin1_General_BIN2").HasComment("the name");
            });
        }
    }

    private class NoSettingsContext : DbContext
    {
        public NoSettingsContext(DbContextOptions<NoSettingsContext> options) : base(options) { }

        public DbSet<Item> Items { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>(MapItem);
        }
    }
}
