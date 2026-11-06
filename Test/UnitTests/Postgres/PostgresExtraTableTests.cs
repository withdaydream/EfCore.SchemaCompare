using System;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

/// <summary>
/// Tables and views that no DbContext maps to are warnings, which don't make CompareEfWithDb return true
/// </summary>
public class PostgresExtraTableTests
{
    private const string CreateExtrasSql =
        """
        CREATE TABLE audit_log (id integer PRIMARY KEY, message text);
        CREATE VIEW widget_names AS SELECT name FROM widget;
        """;

    private readonly ITestOutputHelper _output;

    public PostgresExtraTableTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ExtraTablesAndViewsAreWarnings()
    {
        //ATTEMPT
        var (hasErrors, comparer) = CompareAfterAddingExtras(new CompareEfSqlConfig());

        //VERIFY
        hasErrors.ShouldBeFalse(comparer.GetAllErrors);
        comparer.GetAllErrors.ShouldEqual("");
        comparer.GetAllWarnings.ShouldEqual(
            "EXTRA IN DATABASE: Table 'audit_log'" + Environment.NewLine +
            "EXTRA IN DATABASE: Table 'widget_names'");
    }

    [Fact]
    public void TablesToIgnoreLeavesThemOut()
    {
        //ATTEMPT
        var (hasErrors, comparer) = CompareAfterAddingExtras(
            new CompareEfSqlConfig { TablesToIgnoreCommaDelimited = "audit_log, widget_names" });

        //VERIFY
        hasErrors.ShouldBeFalse(comparer.GetAllErrors);
        comparer.GetAllWarnings.ShouldEqual("");
    }

    [Fact]
    public void IgnoreTheseErrorsHidesTheWarning()
    {
        //SETUP
        var config = new CompareEfSqlConfig();
        config.IgnoreTheseErrors("EXTRA IN DATABASE: Table 'audit_log'");

        //ATTEMPT
        var (hasErrors, comparer) = CompareAfterAddingExtras(config);

        //VERIFY
        hasErrors.ShouldBeFalse(comparer.GetAllErrors);
        comparer.GetAllWarnings.ShouldEqual("EXTRA IN DATABASE: Table 'widget_names'");
    }

    //------------------------------------------------------

    private (bool HasErrors, CompareEfSql Comparer) CompareAfterAddingExtras(CompareEfSqlConfig config)
    {
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = new WidgetContext(new DbContextOptionsBuilder<WidgetContext>().UseNpgsql(connectionString).Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw(CreateExtrasSql);

        var comparer = new CompareEfSql(config);
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);
        _output.WriteLine("ERRORS: " + comparer.GetAllErrors);
        _output.WriteLine("WARNINGS: " + comparer.GetAllWarnings);
        return (hasErrors, comparer);
    }

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; }
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
                entity.Property(e => e.Name).HasColumnName("name");
            });
        }
    }
}
