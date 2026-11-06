using System;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

public class PostgresStorageParameterTests
{
    //fillfactor is set to the PostgreSQL default for a table (100) and a btree index (90), which still doesn't match not set.
    //The table has a text column, so it has a TOAST table to set toast.* parameters on
    private const string SetParametersNotInEfSql =
        """
        ALTER TABLE widget SET (fillfactor = 100, toast.autovacuum_enabled = false);
        ALTER INDEX ix_widget_name SET (fillfactor = 90);
        """;

    private const string ParametersNotInEfErrors =
        "EXTRA IN DATABASE: Widget->Index 'name', storage parameter. Found = fillfactor=90\n" +
        "EXTRA IN DATABASE: Table 'widget', storage parameter. Found = fillfactor=100\n" +
        "EXTRA IN DATABASE: Table 'widget', storage parameter. Found = toast.autovacuum_enabled=false";

    private readonly ITestOutputHelper _output;

    public PostgresStorageParameterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CompareEfPostgreSqlStorageParametersMatch()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<ParametersContext>(connectionString, o => new ParametersContext(o));

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeFalse();
    }

    [Fact]
    public void CompareEfPostgreSqlStorageParametersDifferent()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<ParametersContext>(connectionString, o => new ParametersContext(o));
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE widget SET (fillfactor = 50);
            ALTER TABLE widget RESET (autovacuum_enabled);
            ALTER INDEX ix_widget_name SET (fillfactor = 60);
            -- The same value as EF Core's 0.05, but PostgreSQL keeps the text it was set with
            ALTER TABLE widget SET (autovacuum_vacuum_scale_factor = 0.050);
            """);

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(
            "DIFFERENT: Widget->Index 'name', storage parameter. Expected = fillfactor=80, found = fillfactor=60" + Environment.NewLine +
            "NOT IN DATABASE: Table 'widget', storage parameter. Expected = autovacuum_enabled=false" + Environment.NewLine +
            "DIFFERENT: Table 'widget', storage parameter. Expected = autovacuum_vacuum_scale_factor=0.05, found = autovacuum_vacuum_scale_factor=0.050" + Environment.NewLine +
            "DIFFERENT: Table 'widget', storage parameter. Expected = fillfactor=70, found = fillfactor=50");
    }

    [Fact]
    public void CompareEfPostgreSqlStorageParametersNotInEfMustNotBeSet()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<NoParametersContext>(connectionString, o => new NoParametersContext(o));
        context.Database.ExecuteSqlRaw(SetParametersNotInEfSql);

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(ParametersNotInEfErrors.Replace("\n", Environment.NewLine));
    }

    [Fact]
    public void CompareEfPostgreSqlStorageParameterErrorsCanBeIgnored()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<NoParametersContext>(connectionString, o => new NoParametersContext(o));
        context.Database.ExecuteSqlRaw(SetParametersNotInEfSql);

        var config = new CompareEfSqlConfig();
        config.IgnoreTheseErrors(ParametersNotInEfErrors);
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeFalse();
    }

    //------------------------------------------------------

    private static TContext CreateContext<TContext>(string connectionString, Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        builder.UseNpgsql(connectionString);
        var context = create(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        return context;
    }

    private class NoParametersContext : DbContext
    {
        public NoParametersContext(DbContextOptions<NoParametersContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.ToTable("widget");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name");
                entity.HasIndex(e => e.Name).HasDatabaseName("ix_widget_name");
            });
        }
    }

    private class ParametersContext : DbContext
    {
        public ParametersContext(DbContextOptions<ParametersContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.ToTable("widget");
                entity.HasStorageParameter("fillfactor", 70);
                entity.HasStorageParameter("autovacuum_enabled", false);
                entity.HasStorageParameter("autovacuum_vacuum_scale_factor", 0.05);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name");
                entity.HasIndex(e => e.Name).HasDatabaseName("ix_widget_name").HasStorageParameter("fillfactor", 80);
            });
        }
    }

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
