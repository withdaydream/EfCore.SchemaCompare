using System;
using System.Linq;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TestSupport.EfHelpers;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests;

public class TestIndexAndKeyColumnOrder
{
    //Sorted, as the order indexes are compared in isn't part of what is being tested
    private static readonly string[] DifferentOrderErrors =
    {
        "DIFFERENT: Pair->Index 'first', columns. Expected = first, found = first,second",
        "DIFFERENT: Pair->Index 'second,first', columns. Expected = second,first, found = first,second",
        "DIFFERENT: Pair->PrimaryKey 'pk_pair', columns. Expected = second,first, found = first,second",
    };

    private readonly ITestOutputHelper _output;

    public TestIndexAndKeyColumnOrder(ITestOutputHelper output)
    {
        _output = output;
    }

    public enum Provider { SqlServer, PostgreSql, Sqlite }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    [InlineData(Provider.Sqlite)]
    public void SameColumnOrderHasNoErrors(Provider provider)
    {
        //ATTEMPT
        var errors = CompareWithDatabase<DatabaseContext>(provider, o => new DatabaseContext(o));

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    [InlineData(Provider.Sqlite)]
    public void DifferentOrderAndExtraColumnsAreReported(Provider provider)
    {
        //ATTEMPT
        var errors = CompareWithDatabase<ReorderedContext>(provider, o => new ReorderedContext(o));

        //VERIFY
        errors.ShouldEqual(DifferentOrderErrors);
    }

    //------------------------------------------------------

    /// <summary>
    /// Creates the database with <see cref="DatabaseContext"/>, then compares it with the given DbContext
    /// </summary>
    private string[] CompareWithDatabase<TContext>(Provider provider, Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        using (var context = new DatabaseContext(CreateOptions<DatabaseContext>(provider)))
        {
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();
        }

        using var compareContext = create(CreateOptions<TContext>(provider));
        var config = new CompareEfSqlConfig();
        //Sqlite's scaffolder doesn't read a primary key's name, so it is always reported as different
        if (provider == Provider.Sqlite)
            config.AddIgnoreCompareLog(new CompareLog(CompareType.MatchAnything, CompareState.Different, null,
                CompareAttributes.ConstraintName));
        var comparer = new CompareEfSql(config);
        var hasErrors = comparer.CompareEfWithDb(compareContext);
        _output.WriteLine(comparer.GetAllErrors);
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private DbContextOptions<TContext> CreateOptions<TContext>(Provider provider) where TContext : DbContext
    {
        return provider switch
        {
            Provider.SqlServer => this.CreateUniqueClassOptions<TContext>(),
            Provider.PostgreSql => new DbContextOptionsBuilder<TContext>()
                .UseNpgsql(this.GetUniquePostgreSqlConnectionString()).Options,
            _ => new DbContextOptionsBuilder<TContext>()
                .UseSqlite($"Data Source={TestData.GetTestDataDir()}/IndexAndKeyColumnOrder.db").Options
        };
    }

    private class Pair
    {
        public int First { get; set; }
        public int Second { get; set; }
        public int Third { get; set; }
    }

    private static void MapPair(EntityTypeBuilder<Pair> entity)
    {
        entity.ToTable("pair");
        entity.Property(e => e.First).HasColumnName("first");
        entity.Property(e => e.Second).HasColumnName("second");
        entity.Property(e => e.Third).HasColumnName("third");
    }

    //An included column isn't one of the index's columns, so it mustn't be reported as an extra one
    private static void AddIndexWithIncludedColumn(EntityTypeBuilder<Pair> entity, DatabaseFacade database)
    {
        var index = entity.HasIndex(e => e.First, "ix_pair_include");
        if (database.IsSqlServer())
            SqlServerIndexBuilderExtensions.IncludeProperties(index, e => e.Third);
        else if (database.IsNpgsql())
            NpgsqlIndexBuilderExtensions.IncludeProperties(index, e => e.Third);
    }

    private class DatabaseContext : DbContext
    {
        public DatabaseContext(DbContextOptions<DatabaseContext> options) : base(options) { }

        public DbSet<Pair> Pairs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Pair>(entity =>
            {
                MapPair(entity);
                entity.HasKey(e => new { e.First, e.Second }).HasName("pk_pair");
                entity.HasIndex(e => new { e.First, e.Second }, "ix_pair_order");
                entity.HasIndex(e => new { e.First, e.Second }, "ix_pair_extra");
                AddIndexWithIncludedColumn(entity, Database);
            });
        }
    }

    /// <summary>
    /// Same names as <see cref="DatabaseContext"/>, but the key and ix_pair_order have their columns in the other order,
    /// and ix_pair_extra doesn't have the database index's second column
    /// </summary>
    private class ReorderedContext : DbContext
    {
        public ReorderedContext(DbContextOptions<ReorderedContext> options) : base(options) { }

        public DbSet<Pair> Pairs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Pair>(entity =>
            {
                MapPair(entity);
                entity.HasKey(e => new { e.Second, e.First }).HasName("pk_pair");
                entity.HasIndex(e => new { e.Second, e.First }, "ix_pair_order");
                entity.HasIndex(e => e.First, "ix_pair_extra");
                AddIndexWithIncludedColumn(entity, Database);
            });
        }
    }
}
