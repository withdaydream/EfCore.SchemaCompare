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
/// Two tables have a check constraint with the same name, and one is named like PostgreSQL's NOT NULL constraints
/// </summary>
public class PostgresCheckConstraintReaderTests
{
    private readonly ITestOutputHelper _output;

    public PostgresCheckConstraintReaderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckConstraintsMatch()
    {
        //ATTEMPT
        var errors = CompareAfterRunning(null);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Fact]
    public void SameNamedConstraintIsComparedWithItsOwnTable()
    {
        //ATTEMPT
        var errors = CompareAfterRunning(
            """
            ALTER TABLE gadget DROP CONSTRAINT ck_positive;
            ALTER TABLE gadget ADD CONSTRAINT ck_positive CHECK (amount > 10);
            """);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "DIFFERENT: DbContext 'CheckContext', check constraint. Expected = gadget ck_positive ((amount > 0)), found = gadget ck_positive ((amount > 10))"
        });
    }

    [Fact]
    public void ExtraConstraintNamedLikeNotNullIsReported()
    {
        //ATTEMPT
        var errors = CompareAfterRunning("ALTER TABLE widget ADD CONSTRAINT ck_name_not_null CHECK (name IS NOT NULL);");

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: DbContext 'CheckContext', check constraint. Found = widget ck_name_not_null ((name IS NOT NULL))"
        });
    }

    //------------------------------------------------------

    private string[] CompareAfterRunning(string sql)
    {
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = new CheckContext(new DbContextOptionsBuilder<CheckContext>().UseNpgsql(connectionString).Options);
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

    private class Widget
    {
        public int Id { get; set; }
        public int Amount { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
    }

    private class Gadget
    {
        public int Id { get; set; }
        public int Amount { get; set; }
    }

    private class CheckContext : DbContext
    {
        public CheckContext(DbContextOptions<CheckContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }
        public DbSet<Gadget> Gadgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.ToTable("widget", t =>
                {
                    t.HasCheckConstraint("ck_positive", "amount > 0");
                    t.HasCheckConstraint("ck_email_not_null", "email IS NOT NULL");
                });
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Amount).HasColumnName("amount");
                entity.Property(e => e.Name).HasColumnName("name");
                entity.Property(e => e.Email).HasColumnName("email");
            });
            modelBuilder.Entity<Gadget>(entity =>
            {
                entity.ToTable("gadget", t => t.HasCheckConstraint("ck_positive", "amount > 0"));
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Amount).HasColumnName("amount");
            });
        }
    }
}
