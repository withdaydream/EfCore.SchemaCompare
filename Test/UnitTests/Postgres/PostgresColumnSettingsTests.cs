using System;
using System.Linq;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

/// <summary>
/// Tests the comparison of collations, comments, compression methods and identity columns
/// </summary>
public class PostgresColumnSettingsTests
{
    private readonly ITestOutputHelper _output;

    public PostgresColumnSettingsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SettingsMatch()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<SettingsContext>(connectionString, o => new SettingsContext(o));

        //ATTEMPT
        var errors = Compare(connectionString, context);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Fact]
    public void DifferentSettingsAreReported()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<SettingsContext>(connectionString, o => new SettingsContext(o));
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE widget ALTER COLUMN id SET INCREMENT BY 10;
            ALTER TABLE widget ALTER COLUMN counter SET GENERATED ALWAYS;
            ALTER TABLE widget ALTER COLUMN name TYPE text COLLATE "POSIX";
            COMMENT ON COLUMN widget.name IS 'changed';
            COMMENT ON TABLE widget IS NULL;
            ALTER TABLE widget ALTER COLUMN body SET COMPRESSION pglz;
            DROP INDEX ix_widget_code;
            CREATE INDEX ix_widget_code ON widget (code COLLATE "C");
            """);

        //ATTEMPT
        var errors = Compare(connectionString, context);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "DIFFERENT: Table 'widget', comment. Expected = widgets, found = <null>",
            "DIFFERENT: Widget->Index 'code', collation. Expected = POSIX, found = C",
            "DIFFERENT: Widget->Property 'Body', compression. Expected = lz4, found = pglz",
            "DIFFERENT: Widget->Property 'Counter', value generation strategy. Expected = IdentityByDefaultColumn, found = IdentityAlwaysColumn",
            "DIFFERENT: Widget->Property 'Id', identity options. Expected = increment=5, found = increment=10",
            "DIFFERENT: Widget->Property 'Name', collation. Expected = C, found = POSIX",
            "DIFFERENT: Widget->Property 'Name', comment. Expected = the name, found = changed",
        });
    }

    [Fact]
    public void SettingsOnlyInDatabaseAreReportedEvenIfTheyAreTheDefault()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<NoSettingsContext>(connectionString, o => new NoSettingsContext(o));
        //pglz is the default compression method and en_US.utf8 is this database's default collation
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE widget ALTER COLUMN body SET COMPRESSION pglz;
            ALTER TABLE widget ALTER COLUMN name TYPE text COLLATE "en_US.utf8";
            COMMENT ON COLUMN widget.name IS 'a comment';
            COMMENT ON TABLE widget IS 'a table comment';
            """);

        //ATTEMPT
        var errors = Compare(connectionString, context);

        //VERIFY
        errors.ShouldEqual(new[]
        {
            "DIFFERENT: Table 'widget', comment. Expected = <null>, found = a table comment",
            "DIFFERENT: Widget->Property 'Body', compression. Expected = <null>, found = pglz",
            "DIFFERENT: Widget->Property 'Name', collation. Expected = <null>, found = en_US.utf8",
            "DIFFERENT: Widget->Property 'Name', comment. Expected = <null>, found = a comment",
        });
    }

    [Fact]
    public void IdentityOptionsSetToTheirDefaultsMatchOnesNotSet()
    {
        //SETUP
        //PostgreSQL stores every sequence option, so it can't tell an option set to its default from one that wasn't set
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using (CreateContext<NoSettingsContext>(connectionString, o => new NoSettingsContext(o))) { }
        using var context = new DefaultIdentityOptionsContext(
            new DbContextOptionsBuilder<DefaultIdentityOptionsContext>().UseNpgsql(connectionString).Options);

        //ATTEMPT
        var errors = Compare(connectionString, context);

        //VERIFY
        errors.ShouldEqual(Array.Empty<string>());
    }

    [Theory]
    [InlineData(false, "DIFFERENT: DbContext 'NoSettingsContext', collation. Expected = <null>, found = C")]
    [InlineData(true, null)]
    public void DatabaseCollation(bool setInEf, string expectedError)
    {
        //SETUP
        //EnsureCreated can't create a database whose collation differs from the template database's, so it is made here
        var connectionString = this.GetUniquePostgreSqlConnectionString("collation");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = builder.Database;
        builder.Database = "postgres";
        using (var connection = new NpgsqlConnection(builder.ConnectionString))
        {
            connection.Open();
            //CREATE DATABASE can't be run in a batch with another statement
            foreach (var sql in new[]
                     {
                         $"""DROP DATABASE IF EXISTS "{databaseName}" WITH (FORCE)""",
                         $"""CREATE DATABASE "{databaseName}" TEMPLATE template0 LC_COLLATE 'C' LC_CTYPE 'C'"""
                     })
            {
                using var command = new NpgsqlCommand(sql, connection);
                command.ExecuteNonQuery();
            }
        }
        //The drop closed any pooled connections to the database, e.g. from the other test case
        NpgsqlConnection.ClearAllPools();

        DbContext context = setInEf
            ? new CollationContext(new DbContextOptionsBuilder<CollationContext>().UseNpgsql(connectionString).Options)
            : new NoSettingsContext(new DbContextOptionsBuilder<NoSettingsContext>().UseNpgsql(connectionString).Options);
        using (context)
        {
            context.Database.EnsureCreated();

            //ATTEMPT
            var errors = Compare(connectionString, context);

            //VERIFY
            errors.ShouldEqual(expectedError == null ? Array.Empty<string>() : new[] { expectedError });
        }
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

    private static void MapWidget(ModelBuilder modelBuilder, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Widget>> configure = null)
    {
        modelBuilder.Entity<Widget>(entity =>
        {
            entity.ToTable("widget");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Counter).HasColumnName("counter");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.HasIndex(e => e.Code).HasDatabaseName("ix_widget_code");
            configure?.Invoke(entity);
        });
    }

    private class SettingsContext : DbContext
    {
        public SettingsContext(DbContextOptions<SettingsContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            MapWidget(modelBuilder, entity =>
            {
                entity.ToTable("widget", t => t.HasComment("widgets"));
                entity.Property(e => e.Id).UseIdentityAlwaysColumn().HasIdentityOptions(startValue: 100, incrementBy: 5);
                entity.Property(e => e.Counter).UseIdentityByDefaultColumn().ValueGeneratedOnAdd();
                entity.Property(e => e.Name).UseCollation("C").HasComment("the name");
                entity.Property(e => e.Body).UseCompressionMethod("lz4");
                entity.HasIndex(e => e.Code).HasDatabaseName("ix_widget_code").UseCollation("POSIX");
            });
        }
    }

    private class NoSettingsContext : DbContext
    {
        public NoSettingsContext(DbContextOptions<NoSettingsContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            MapWidget(modelBuilder);
        }
    }

    private class DefaultIdentityOptionsContext : DbContext
    {
        public DefaultIdentityOptionsContext(DbContextOptions<DefaultIdentityOptionsContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            MapWidget(modelBuilder, entity => entity.Property(e => e.Id).HasIdentityOptions(
                startValue: 1, incrementBy: 1, minValue: 1, maxValue: int.MaxValue, cyclic: false, numbersToCache: 1));
        }
    }

    private class CollationContext : DbContext
    {
        public CollationContext(DbContextOptions<CollationContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCollation("C");
            MapWidget(modelBuilder);
        }
    }

    private class Widget
    {
        public int Id { get; set; }
        public long Counter { get; set; }
        public string Name { get; set; }
        public string Code { get; set; }
        public string Body { get; set; }
    }
}
