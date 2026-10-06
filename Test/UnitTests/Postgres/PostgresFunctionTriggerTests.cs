using System;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

public class PostgresFunctionTriggerTests
{
    private const string CreateTriggerSql =
        """
        CREATE FUNCTION public.touch_widget() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RETURN NEW;
        END
        $$;

        CREATE TRIGGER trg_widget_touch BEFORE UPDATE ON public.widget
            FOR EACH ROW EXECUTE FUNCTION public.touch_widget();
        """;

    private const string CreateSlugifySql =
        """
        CREATE FUNCTION public.slugify(value text) RETURNS text LANGUAGE sql AS $$ SELECT lower(value) $$;
        """;

    private readonly ITestOutputHelper _output;

    public PostgresFunctionTriggerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CompareEfPostgreSqlReportsUndeclaredFunctionsAndTriggers()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<NoDeclarationsContext>(connectionString, o => new NoDeclarationsContext(o));
        context.Database.ExecuteSqlRaw(CreateTriggerSql + CreateSlugifySql);

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        var errors = comparer.GetAllErrors.Split(Environment.NewLine);
        errors.Length.ShouldEqual(3);
        errors[0].ShouldEqual("EXTRA IN DATABASE: Function 'public.slugify(text)'. Found = function returning text");
        errors[1].ShouldEqual("EXTRA IN DATABASE: Function 'public.touch_widget()'. Found = function returning trigger");
        Assert.StartsWith("EXTRA IN DATABASE: Trigger 'public.widget.trg_widget_touch'. Found = CREATE TRIGGER trg_widget_touch BEFORE UPDATE ON ", errors[2]);
    }

    [Fact]
    public void CompareEfPostgreSqlDeclaredFunctionAndTriggerMatch()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<DeclarationsContext>(connectionString, o => new DeclarationsContext(o));
        context.Database.ExecuteSqlRaw(CreateTriggerSql + CreateSlugifySql);

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        //A trigger's function can't be declared with HasDbFunction, so it is always reported
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual("EXTRA IN DATABASE: Function 'public.touch_widget()'. Found = function returning trigger");
    }

    [Fact]
    public void CompareEfPostgreSqlReportsUndeclaredOverload()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<DeclarationsContext>(connectionString, o => new DeclarationsContext(o));
        context.Database.ExecuteSqlRaw(CreateTriggerSql + CreateSlugifySql +
            "CREATE FUNCTION public.slugify(value text, max_length integer) RETURNS text LANGUAGE sql AS $$ SELECT left(lower(value), max_length) $$;");

        var config = new CompareEfSqlConfig();
        config.IgnoreTheseErrors("EXTRA IN DATABASE: Function 'public.touch_widget()'. Found = function returning trigger");
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual("EXTRA IN DATABASE: Function 'public.slugify(text,integer)'. Found = function returning text");
    }

    [Fact]
    public void CompareEfPostgreSqlReportsDeclaredButMissing()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<DeclarationsContext>(connectionString, o => new DeclarationsContext(o));

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(
            "NOT IN DATABASE: Function 'public.slugify(text)'. Expected = function returning text" + Environment.NewLine +
            "NOT IN DATABASE: Trigger 'public.widget.trg_widget_touch'. Expected = trigger declared on entity Widget");
    }

    [Fact]
    public void CompareEfPostgreSqlIgnoresExtensionFunctions()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<NoDeclarationsContext>(connectionString, o => new NoDeclarationsContext(o));
        context.Database.ExecuteSqlRaw("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(connectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeFalse();
    }

    [Fact]
    public void CompareEfPostgreSqlAcceptsDeclaredBuiltInAndExtensionFunctions()
    {
        //SETUP
        var connectionString = this.GetUniquePostgreSqlConnectionString();
        using var context = CreateContext<BuiltInFunctionsContext>(connectionString, o => new BuiltInFunctionsContext(o));
        context.Database.ExecuteSqlRaw("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

        var comparer = new CompareEfSql();

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

    private class NoDeclarationsContext : DbContext
    {
        public NoDeclarationsContext(DbContextOptions<NoDeclarationsContext> options) : base(options) { }

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

    private class DeclarationsContext : DbContext
    {
        public DeclarationsContext(DbContextOptions<DeclarationsContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        public static string Slugify(string value) => throw new NotSupportedException();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.ToTable("widget", tb => tb.HasTrigger("trg_widget_touch"));
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name");
            });
            modelBuilder.HasDbFunction(typeof(DeclarationsContext).GetMethod(nameof(Slugify))!).HasName("slugify");
        }
    }

    private class BuiltInFunctionsContext : DbContext
    {
        public BuiltInFunctionsContext(DbContextOptions<BuiltInFunctionsContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        public static string JsonText(object json, string key) => throw new NotSupportedException();
        public static DateTime ToTimestampTz(string value) => throw new NotSupportedException();
        public static Guid ToUuid(string value) => throw new NotSupportedException();
        public static byte[] GenRandomBytes(int count) => throw new NotSupportedException();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.ToTable("widget");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name");
            });

            //Built in as jsonb_extract_path_text(jsonb, VARIADIC text[])
            modelBuilder.HasDbFunction(typeof(BuiltInFunctionsContext).GetMethod(nameof(JsonText))!)
                .HasName("jsonb_extract_path_text")
                .HasParameter("json", p => p.HasStoreType("jsonb"));
            //Postgres treats timestamptz(text) and uuid(text) as casts, as neither function exists
            modelBuilder.HasDbFunction(typeof(BuiltInFunctionsContext).GetMethod(nameof(ToTimestampTz))!).HasName("timestamptz");
            modelBuilder.HasDbFunction(typeof(BuiltInFunctionsContext).GetMethod(nameof(ToUuid))!).HasName("uuid");
            //From the pgcrypto extension
            modelBuilder.HasDbFunction(typeof(BuiltInFunctionsContext).GetMethod(nameof(GenRandomBytes))!).HasName("gen_random_bytes");
        }
    }

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
