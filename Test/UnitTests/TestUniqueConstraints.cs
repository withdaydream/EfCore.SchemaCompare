using System;
using System.Linq;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.EfHelpers;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests;

/// <summary>
/// EF Core creates a UNIQUE constraint for an alternate key, and a unique index for HasIndex(...).IsUnique()
/// </summary>
public class TestUniqueConstraints
{
    private readonly ITestOutputHelper _output;

    public TestUniqueConstraints(ITestOutputHelper output)
    {
        _output = output;
    }

    public enum Provider { SqlServer, PostgreSql, Sqlite }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void MatchingKeysAndIndexesHaveNoErrors(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider, null);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Fact]
    public void SqliteAlternateKeyIsNotChecked()
    {
        //ATTEMPT
        //Sqlite's scaffolder doesn't read a unique constraint's name, so the alternate key can't be found
        var errors = CompareAfterRunning(Provider.Sqlite, null);

        //VERIFY
        errors.ShouldEqual(new[] { "NOT CHECKED: Entity 'ak_widget_code_name', constraint name. Expected = <null>, found = ak_widget_code_name" });
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void UniqueConstraintDoesNotMatchUniqueIndex(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider,
            (provider == Provider.SqlServer ? "DROP INDEX ix_widget_email ON widget;" : "DROP INDEX ix_widget_email;") +
            "ALTER TABLE widget ADD CONSTRAINT ix_widget_email UNIQUE (email);");

        //VERIFY
        errors.ShouldEqual(new[] { "NOT IN DATABASE: Widget->Index 'email', index constraint name. Expected = ix_widget_email" });
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void AlternateKeyColumnOrderIsReported(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider,
            """
            ALTER TABLE widget DROP CONSTRAINT ak_widget_code_name;
            ALTER TABLE widget ADD CONSTRAINT ak_widget_code_name UNIQUE (name, code);
            """);

        //VERIFY
        errors.ShouldEqual(new[] { "DIFFERENT: Widget->AlternateKey 'ak_widget_code_name', columns. Expected = code,name, found = name,code" });
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void MissingAlternateKeyIsReported(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider, "ALTER TABLE widget DROP CONSTRAINT ak_widget_code_name;");

        //VERIFY
        errors.ShouldEqual(new[] { "NOT IN DATABASE: Widget->AlternateKey 'ak_widget_code_name', constraint name. Expected = ak_widget_code_name" });
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void ExtraUniqueConstraintIsReported(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider, "ALTER TABLE widget ADD CONSTRAINT uq_widget_name UNIQUE (name);");

        //VERIFY
        errors.ShouldEqual(new[] { "EXTRA IN DATABASE: AlternateKey 'widget', constraint name. Found = uq_widget_name" });
    }

    //------------------------------------------------------

    private string[] CompareAfterRunning(Provider provider, string sql)
    {
        //Npgsql removes the password from an opened connection's connection string, so it is passed to the comparer
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var options = provider switch
        {
            Provider.SqlServer => this.CreateUniqueClassOptions<UniqueContext>(),
            Provider.PostgreSql => new DbContextOptionsBuilder<UniqueContext>().UseNpgsql(postgresConnectionString).Options,
            _ => new DbContextOptionsBuilder<UniqueContext>()
                .UseSqlite($"Data Source={TestData.GetTestDataDir()}/UniqueConstraints.db").Options
        };
        using var context = new UniqueContext(options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        if (sql != null)
            context.Database.ExecuteSqlRaw(sql);

        var config = new CompareEfSqlConfig();
        //Sqlite's scaffolder doesn't read a primary key's name, so it is always reported as different
        if (provider == Provider.Sqlite)
            config.AddIgnoreCompareLog(new CompareLog(CompareType.MatchAnything, CompareState.Different, null,
                CompareAttributes.ConstraintName));
        var comparer = new CompareEfSql(config);
        var hasErrors = provider == Provider.PostgreSql
            ? comparer.CompareEfWithDb(postgresConnectionString, context)
            : comparer.CompareEfWithDb(context);
        _output.WriteLine(comparer.GetAllErrors);
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private class Widget
    {
        public int Id { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
    }

    private class UniqueContext : DbContext
    {
        public UniqueContext(DbContextOptions<UniqueContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.ToTable("widget");
                entity.Property(e => e.Id).HasColumnName("id");
                //A SQL Server key can't use nvarchar(max)
                entity.Property(e => e.Code).HasColumnName("code").HasMaxLength(50);
                entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(50);
                //Required, so SQL Server's unique index doesn't get an IS NOT NULL filter
                entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(50).IsRequired();
                entity.HasAlternateKey(e => new { e.Code, e.Name }).HasName("ak_widget_code_name");
                entity.HasIndex(e => e.Email).IsUnique().HasDatabaseName("ix_widget_email");
            });
        }
    }
}
