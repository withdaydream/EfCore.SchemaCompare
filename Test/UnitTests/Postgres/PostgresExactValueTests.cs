using System;
using System.Linq;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

/// <summary>
/// Values are compared as exact text. Defaults and computed columns are compared as parsed expressions, as PostgreSQL
/// rewrites their SQL when it stores it, e.g. adding brackets, spaces and casts
/// </summary>
public class PostgresExactValueTests
{
    //"caf" plus the single letter e with an acute accent, and the same word written as an e followed by a combining accent
    private static readonly string CafeComposed = "caf" + (char)0x00E9;
    private static readonly string CafeDecomposed = "cafe" + (char)0x0301;

    private readonly ITestOutputHelper _output;

    public PostgresExactValueTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DefaultsAndComputedColumnsRewrittenByPostgresMatch()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext(connectionString);

        //ATTEMPT
        var errors = Compare(connectionString, context);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Fact]
    public void ValuesThatDifferOnlyInSpacesOrEncodingAreReported()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext(connectionString);
        //U&'cafe\0301' is CafeDecomposed, while EF Core's comment is CafeComposed
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE item ALTER COLUMN status SET DEFAULT 'instock';
            COMMENT ON COLUMN item.price IS 'Totalprice';
            COMMENT ON COLUMN item.first_name IS U&'cafe\0301';
            """);

        //ATTEMPT
        var errors = Compare(connectionString, context);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            $"DIFFERENT: Item->Property 'FirstName', comment. Expected = {CafeComposed}, found = {CafeDecomposed}",
            "DIFFERENT: Item->Property 'Price', comment. Expected = Total price, found = Totalprice",
            "DIFFERENT: Item->Property 'Status', default value sql. Expected = 'in stock', found = 'instock'::text",
        });
    }

    [Fact]
    public void DifferentComputedColumnIsReported()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext(connectionString);
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE item DROP COLUMN total;
            ALTER TABLE item ADD COLUMN total numeric NOT NULL GENERATED ALWAYS AS (price * 2) STORED;
            """);

        //ATTEMPT
        var errors = Compare(connectionString, context);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "DIFFERENT: Item->Property 'Total', computed column sql. Expected = price * quantity, found = price * (2)::numeric",
        });
    }

    //------------------------------------------------------

    private string[] Compare(string connectionString, DbContext context)
    {
        var comparer = new CompareEfSql();
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);
        _output.WriteLine(comparer.GetAllErrors);
        //Sorted, as the order the errors are found in isn't what is being tested
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private static ItemContext CreateContext(string connectionString)
    {
        var context = new ItemContext(new DbContextOptionsBuilder<ItemContext>().UseNpgsql(connectionString).Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        return context;
    }

    private class Item
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName { get; set; }
        public string LowerName { get; set; }
        public decimal Price { get; set; }
        public int Quantity { get; set; }
        public decimal Total { get; set; }
        public string Status { get; set; }
        public int Stock { get; set; }
        public decimal Discount { get; set; }
        public bool Active { get; set; }
        public DateTime CreatedAt { get; set; }
        public Guid Reference { get; set; }
    }

    private class ItemContext : DbContext
    {
        public ItemContext(DbContextOptions<ItemContext> options) : base(options) { }

        public DbSet<Item> Items { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>(entity =>
            {
                entity.ToTable("item");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.FirstName).HasColumnName("first_name").HasComment(CafeComposed);
                entity.Property(e => e.LastName).HasColumnName("last_name");
                entity.Property(e => e.FullName).HasColumnName("full_name")
                    .HasComputedColumnSql("first_name||' '||last_name", stored: true);
                entity.Property(e => e.LowerName).HasColumnName("lower_name")
                    .HasComputedColumnSql("LOWER(first_name)", stored: true);
                entity.Property(e => e.Price).HasColumnName("price").HasComment("Total price");
                entity.Property(e => e.Quantity).HasColumnName("quantity");
                entity.Property(e => e.Total).HasColumnName("total")
                    .HasComputedColumnSql("price * quantity", stored: true);
                entity.Property(e => e.Status).HasColumnName("status").HasDefaultValue("in stock");
                entity.Property(e => e.Stock).HasColumnName("stock").HasDefaultValue(10);
                entity.Property(e => e.Discount).HasColumnName("discount").HasDefaultValue(0.5m);
                entity.Property(e => e.Active).HasColumnName("active").HasDefaultValue(true);
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
                entity.Property(e => e.Reference).HasColumnName("reference").HasDefaultValueSql("gen_random_uuid()");
            });
        }
    }
}
