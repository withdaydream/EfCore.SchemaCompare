using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

/// <summary>
/// CompareEfWithDb only returns false, i.e. success, if nothing differs, whatever the settings
/// </summary>
public class PostgresCompareResultTests
{
    private readonly ITestOutputHelper _output;

    public PostgresCompareResultTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StageOneErrorMakesItReturnTrue(bool alwaysRunStage2)
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = new WidgetContext(new DbContextOptionsBuilder<WidgetContext>().UseNpgsql(connectionString).Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        //Stage 2 finds nothing, so before, with AlwaysRunStage2, its result replaced stage 1's
        context.Database.ExecuteSqlRaw("COMMENT ON TABLE widget IS 'x';");
        var comparer = new CompareEfSql(new CompareEfSqlConfig { AlwaysRunStage2 = alwaysRunStage2 });

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        //When stage 2 runs, the DbContext's name starts the error, as stage 2 adds its own log for the database
        comparer.GetAllErrors.EndsWith("Table 'widget', comment. Expected = <null>, found = x").ShouldBeTrue(comparer.GetAllErrors);
    }

    [Fact]
    public void NothingDifferentReturnsFalse()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = new WidgetContext(new DbContextOptionsBuilder<WidgetContext>().UseNpgsql(connectionString).Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        var comparer = new CompareEfSql(new CompareEfSqlConfig { AlwaysRunStage2 = true });

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        hasErrors.ShouldBeFalse(comparer.GetAllErrors);
    }

    private class Widget
    {
        public int Id { get; set; }
    }

    private class WidgetContext : DbContext
    {
        public WidgetContext(DbContextOptions<WidgetContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.ToTable("widget");
                entity.Property(e => e.Id).HasColumnName("id");
            });
        }
    }
}
