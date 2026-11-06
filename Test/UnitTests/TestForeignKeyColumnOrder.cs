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
/// Sqlite isn't tested, as it doesn't name foreign keys, so they aren't compared
/// </summary>
public class TestForeignKeyColumnOrder
{
    private const string ForeignKeyText =
        "fk_child_parent Table(child) Columns({0}) ForeignTable(parent) ForeignColumns({1}) OnUpdate(NO ACTION) OnDelete(CASCADE)";

    private readonly ITestOutputHelper _output;

    public TestForeignKeyColumnOrder(ITestOutputHelper output)
    {
        _output = output;
    }

    public enum Provider { SqlServer, PostgreSql }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void SameColumnOrderHasNoErrors(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider, null);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void DifferentColumnOrderIsReported(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider,
            """
            ALTER TABLE child DROP CONSTRAINT fk_child_parent;
            ALTER TABLE child ADD CONSTRAINT fk_child_parent FOREIGN KEY (c2, c1) REFERENCES parent (p1, p2) ON DELETE CASCADE;
            """);

        //VERIFY
        errors.ShouldEqual(WithPostgresConstraintError(provider,
            "DIFFERENT: Child->ForeignKey 'fk_child_parent', columns. Expected = c1,c2, found = c2,c1",
            "c1, c2", "p1, p2", "c2, c1", "p1, p2"));
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void DifferentPrincipalColumnOrderIsReported(Provider provider)
    {
        //ATTEMPT
        var errors = CompareAfterRunning(provider,
            """
            ALTER TABLE parent ADD CONSTRAINT uq_parent_p2_p1 UNIQUE (p2, p1);
            ALTER TABLE child DROP CONSTRAINT fk_child_parent;
            ALTER TABLE child ADD CONSTRAINT fk_child_parent FOREIGN KEY (c1, c2) REFERENCES parent (p2, p1) ON DELETE CASCADE;
            """);

        //VERIFY
        errors.ShouldEqual(WithPostgresConstraintError(provider,
            "DIFFERENT: Child->ForeignKey 'fk_child_parent', principal columns. Expected = p1,p2, found = p2,p1",
            "c1, c2", "p1, p2", "c1, c2", "p2, p1"));
    }

    //------------------------------------------------------

    /// <summary>
    /// PostgreSQL's foreign keys are also compared as a whole, which reports the same difference
    /// </summary>
    private static string[] WithPostgresConstraintError(Provider provider, string error,
        string expectedColumns, string expectedPrincipalColumns, string foundColumns, string foundPrincipalColumns)
    {
        if (provider != Provider.PostgreSql)
            return new[] { error };
        return new[]
        {
            error,
            "DIFFERENT: DbContext 'ForeignKeyContext', foreign key. Expected = " +
            string.Format(ForeignKeyText, expectedColumns, expectedPrincipalColumns) + ", found = " +
            string.Format(ForeignKeyText, foundColumns, foundPrincipalColumns)
        };
    }

    private string[] CompareAfterRunning(Provider provider, string sql)
    {
        //Npgsql removes the password from an opened connection's connection string, so it is passed to the comparer
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var options = provider == Provider.SqlServer
            ? this.CreateUniqueClassOptions<ForeignKeyContext>()
            : new DbContextOptionsBuilder<ForeignKeyContext>().UseNpgsql(postgresConnectionString).Options;
        using var context = new ForeignKeyContext(options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        if (sql != null)
            context.Database.ExecuteSqlRaw(sql);

        var comparer = new CompareEfSql();
        var hasErrors = provider == Provider.SqlServer
            ? comparer.CompareEfWithDb(context)
            : comparer.CompareEfWithDb(postgresConnectionString, context);
        _output.WriteLine(comparer.GetAllErrors);
        //Sorted, as the order the errors are found in isn't what is being tested
        return hasErrors
            ? comparer.GetAllErrors.Split(Environment.NewLine).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private class Parent
    {
        public int P1 { get; set; }
        public int P2 { get; set; }
    }

    private class Child
    {
        public int Id { get; set; }
        public int C1 { get; set; }
        public int C2 { get; set; }
    }

    private class ForeignKeyContext : DbContext
    {
        public ForeignKeyContext(DbContextOptions<ForeignKeyContext> options) : base(options) { }

        public DbSet<Parent> Parents { get; set; }
        public DbSet<Child> Children { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Parent>(entity =>
            {
                entity.ToTable("parent");
                entity.Property(e => e.P1).HasColumnName("p1");
                entity.Property(e => e.P2).HasColumnName("p2");
                entity.HasKey(e => new { e.P1, e.P2 }).HasName("pk_parent");
            });
            modelBuilder.Entity<Child>(entity =>
            {
                entity.ToTable("child");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.C1).HasColumnName("c1");
                entity.Property(e => e.C2).HasColumnName("c2");
                entity.HasOne<Parent>().WithMany().HasForeignKey(e => new { e.C1, e.C2 }).HasConstraintName("fk_child_parent");
            });
        }
    }
}
