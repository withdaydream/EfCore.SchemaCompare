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
/// An entity mapped to JSON (ToJson) is held in one column of its owner's table, which is compared like any other column
/// </summary>
public class PostgresJsonColumnTests
{
    private readonly ITestOutputHelper _output;

    public PostgresJsonColumnTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void JsonColumnMatches()
    {
        //ATTEMPT
        var errors = CompareAfterRunning(null);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Theory]
    [InlineData("ALTER TABLE customer ALTER COLUMN address_json TYPE json USING address_json::json",
        "DIFFERENT: Customer->Property 'Address', column type. Expected = jsonb, found = json")]
    [InlineData("ALTER TABLE customer ALTER COLUMN address_json SET NOT NULL",
        "DIFFERENT: Customer->Property 'Address', nullability. Expected = NULL, found = NOT NULL")]
    [InlineData("ALTER TABLE customer DROP COLUMN address_json",
        "NOT IN DATABASE: Customer->Property 'Address', column name. Expected = address_json")]
    public void JsonColumnDifferenceIsReported(string sql, string expectedError)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(sql);

        //VERIFY
        errors.ShouldEqual(new[] { expectedError });
    }

    //------------------------------------------------------

    private string[] CompareAfterRunning(string sql)
    {
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = new JsonContext(new DbContextOptionsBuilder<JsonContext>().UseNpgsql(connectionString).Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        if (sql != null)
            context.Database.ExecuteSqlRaw(sql);

        var comparer = new CompareEfSql();
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);
        _output.WriteLine(comparer.GetAllErrors);
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private class Address
    {
        public string Street { get; set; }
        public string City { get; set; }
    }

    private class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public Address Address { get; set; }
    }

    private class JsonContext : DbContext
    {
        public JsonContext(DbContextOptions<JsonContext> options) : base(options) { }

        public DbSet<Customer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>(entity =>
            {
                entity.ToTable("customer");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name");
                //The column's name isn't the navigation's name, which wasn't supported before
                entity.OwnsOne(e => e.Address, address => address.ToJson("address_json"));
            });
        }
    }
}
