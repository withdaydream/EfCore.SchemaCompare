using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaCompare;
using EfSchemaCompare.Internal;
using EfSchemaCompare.Internal.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

public class FunctionTriggerComparerTests
{
    [Theory]
    [InlineData("text", "text")]
    [InlineData("int4", "integer")]
    [InlineData("INT", "integer")]
    [InlineData("character varying(100)", "character varying")]
    [InlineData("varchar(100)", "character varying")]
    [InlineData("numeric(10,2)", "numeric")]
    [InlineData("timestamptz", "timestamp with time zone")]
    [InlineData("timestamp(3) with time zone", "timestamp with time zone")]
    [InlineData("timestamp", "timestamp without time zone")]
    [InlineData("int4[]", "integer[]")]
    [InlineData("_int4", "integer[]")]
    [InlineData("integer[3]", "integer[]")]
    [InlineData("double  precision", "double precision")]
    [InlineData("\"MyEnum\"", "\"MyEnum\"")]
    public void Normalize_MapsStoreTypesToFormatTypeNames(string storeType, string expected)
    {
        PostgresTypeNameNormalizer.Normalize(storeType).ShouldEqual(expected);
    }

    [Fact]
    public void Compare_UndeclaredFunctionsAndTriggersInDatabaseAreExtra()
    {
        //SETUP
        var contextLog = CreateContextLog();

        //ATTEMPT
        CreateComparer().Compare(
            new[] { Declarations(contextLog) },
            new[] { Function("touch_widget", "", "function returning trigger") },
            new[] { Trigger("widget", "trg_widget_touch", "CREATE TRIGGER trg_widget_touch ...") });

        //VERIFY
        contextLog.SubLogs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: Function 'public.touch_widget()'. Found = function returning trigger",
            "EXTRA IN DATABASE: Trigger 'public.widget.trg_widget_touch'. Found = CREATE TRIGGER trg_widget_touch ..."
        });
    }

    [Fact]
    public void Compare_DeclaredButMissingIsNotInDatabase()
    {
        //SETUP
        var contextLog = CreateContextLog();

        //ATTEMPT
        CreateComparer().Compare(
            new[] { Declarations(contextLog,
                new[] { Function("slugify", "text", "function returning text", schema: null) },
                new[] { Trigger("widget", "trg_widget_touch", "trigger declared on entity Widget", schema: null) }) },
            Array.Empty<IConstraintReader.FunctionDefinition>(),
            Array.Empty<IConstraintReader.TriggerDefinition>());

        //VERIFY
        contextLog.SubLogs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "NOT IN DATABASE: Function 'public.slugify(text)'. Expected = function returning text",
            "NOT IN DATABASE: Trigger 'public.widget.trg_widget_touch'. Expected = trigger declared on entity Widget"
        });
    }

    [Fact]
    public void Compare_MatchingDeclarationsLogNothing()
    {
        //SETUP
        var contextLog = CreateContextLog();
        var errors = 0;

        //ATTEMPT
        CreateComparer(() => errors++).Compare(
            new[] { Declarations(contextLog,
                new[] { Function("slugify", "text", "function returning text", schema: null) },
                new[] { Trigger("widget", "trg_widget_touch", "trigger declared on entity Widget", schema: null) }) },
            new[] { Function("slugify", "text", "function returning text") },
            new[] { Trigger("widget", "trg_widget_touch", "CREATE TRIGGER ...") });

        //VERIFY
        contextLog.SubLogs.Count.ShouldEqual(0);
        errors.ShouldEqual(0);
    }

    [Fact]
    public void Compare_UndeclaredOverloadOfDeclaredFunctionIsExtra()
    {
        //SETUP
        var contextLog = CreateContextLog();

        //ATTEMPT
        CreateComparer().Compare(
            new[] { Declarations(contextLog, new[] { Function("slugify", "text", "function returning text") }) },
            new[]
            {
                Function("slugify", "text", "function returning text"),
                Function("slugify", "text,integer", "function returning text")
            },
            Array.Empty<IConstraintReader.TriggerDefinition>());

        //VERIFY
        contextLog.SubLogs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: Function 'public.slugify(text,integer)'. Found = function returning text"
        });
    }

    [Fact]
    public void Compare_SameTriggerNameOnDifferentTablesAreDifferentTriggers()
    {
        //SETUP
        var contextLog = CreateContextLog();

        //ATTEMPT
        CreateComparer().Compare(
            new[] { Declarations(contextLog, triggers: new[] { Trigger("widget", "set_updated_at", "declared") }) },
            Array.Empty<IConstraintReader.FunctionDefinition>(),
            new[]
            {
                Trigger("widget", "set_updated_at", "CREATE TRIGGER ..."),
                Trigger("gadget", "set_updated_at", "CREATE TRIGGER ...")
            });

        //VERIFY
        contextLog.SubLogs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: Trigger 'public.gadget.set_updated_at'. Found = CREATE TRIGGER ..."
        });
    }

    [Fact]
    public void Compare_ObjectDeclaredByAnyContextIsNotExtra()
    {
        //SETUP
        var firstContextLog = CreateContextLog("FirstContext");
        var secondContextLog = CreateContextLog("SecondContext");

        //ATTEMPT
        CreateComparer().Compare(
            new[]
            {
                Declarations(firstContextLog),
                Declarations(secondContextLog, new[] { Function("slugify", "text", "function returning text"), Function("missing", "", "function returning text") })
            },
            new[] { Function("slugify", "text", "function returning text"), Function("rogue", "", "function returning void") },
            Array.Empty<IConstraintReader.TriggerDefinition>());

        //VERIFY
        firstContextLog.SubLogs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: Function 'public.rogue()'. Found = function returning void"
        });
        secondContextLog.SubLogs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "NOT IN DATABASE: Function 'public.missing()'. Expected = function returning text"
        });
    }

    [Fact]
    public void Compare_IgnoreTheseErrorsOnlyIgnoresTheNamedObject()
    {
        //SETUP
        //A definition with ", " and "->" in it, which the ignore decoding must cope with
        const string jsonTriggerDef = "CREATE TRIGGER trg_json AFTER INSERT OR UPDATE OF a, b ON public.widget FOR EACH ROW WHEN ((new.data ->> 'x'::text) IS NOT NULL) EXECUTE FUNCTION touch_widget()";
        var config = new CompareEfSqlConfig();
        config.IgnoreTheseErrors("EXTRA IN DATABASE: Function 'public.touch_widget()'. Found = function returning trigger");
        config.IgnoreTheseErrors($"EXTRA IN DATABASE: Trigger 'public.widget.trg_json'. Found = {jsonTriggerDef}");
        var contextLog = CreateContextLog();

        //ATTEMPT
        new FunctionTriggerComparer(config.LogsToIgnore, "public", () => { }).Compare(
            new[] { Declarations(contextLog) },
            new[] { Function("touch_widget", "", "function returning trigger"), Function("rogue", "", "function returning void") },
            new[] { Trigger("widget", "trg_json", jsonTriggerDef), Trigger("widget", "trg_other", "CREATE TRIGGER trg_other ...") });

        //VERIFY
        contextLog.SubLogs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "EXTRA IN DATABASE: Function 'public.rogue()'. Found = function returning void",
            "EXTRA IN DATABASE: Trigger 'public.widget.trg_other'. Found = CREATE TRIGGER trg_other ..."
        });
    }

    [Fact]
    public void GetFunctionsAndTriggers_ReadsDeclarationsFromModel()
    {
        //SETUP
        var builder = new DbContextOptionsBuilder<DeclaringContext>();
        builder.UseNpgsql("Host=localhost;Database=never-connected");
        using var context = new DeclaringContext(builder.Options);
        var model = context.GetService<IDesignTimeModel>().Model;

        //ATTEMPT
        var functions = model.GetFunctions();
        var triggers = model.GetTriggers();

        //VERIFY
        functions.Select(x => x.GetIdentity("public")).ToArray().ShouldEqual(new[] { "public.slugify(text,integer)" });
        functions[0].Description.ShouldEqual("function returning text");
        triggers.Select(x => x.GetIdentity("public")).ToArray().ShouldEqual(new[] { "public.widget.trg_widget_touch" });
        triggers[0].Description.ShouldEqual("trigger declared on entity Widget");
    }

    //------------------------------------------------------

    private static FunctionTriggerComparer CreateComparer(Action setErrorHasHappened = null)
    {
        return new FunctionTriggerComparer(new List<CompareLog>(), "public", setErrorHasHappened ?? (() => { }));
    }

    private static CompareLog CreateContextLog(string name = "TestContext")
    {
        return new CompareLog(CompareType.DbContext, CompareState.Ok, name);
    }

    private static FunctionTriggerComparer.ContextDeclarations Declarations(CompareLog contextLog,
        IReadOnlyList<IConstraintReader.FunctionDefinition> functions = null,
        IReadOnlyList<IConstraintReader.TriggerDefinition> triggers = null)
    {
        return new FunctionTriggerComparer.ContextDeclarations(contextLog,
            functions ?? Array.Empty<IConstraintReader.FunctionDefinition>(),
            triggers ?? Array.Empty<IConstraintReader.TriggerDefinition>());
    }

    private static IConstraintReader.FunctionDefinition Function(string name, string argumentTypes, string description, string schema = "public")
    {
        return new IConstraintReader.FunctionDefinition
        {
            SchemaName = schema,
            FunctionName = name,
            ArgumentTypes = argumentTypes,
            Description = description
        };
    }

    private static IConstraintReader.TriggerDefinition Trigger(string table, string name, string description, string schema = "public")
    {
        return new IConstraintReader.TriggerDefinition
        {
            SchemaName = schema,
            TableName = table,
            TriggerName = name,
            Description = description
        };
    }

    private class DeclaringContext : DbContext
    {
        public DeclaringContext(DbContextOptions<DeclaringContext> options) : base(options) { }

        public DbSet<Widget> Widgets { get; set; }

        public static string Slugify(string value, int maxLength) => throw new NotSupportedException();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().ToTable("widget", tb => tb.HasTrigger("trg_widget_touch"));
            modelBuilder.HasDbFunction(typeof(DeclaringContext).GetMethod(nameof(Slugify))!).HasName("slugify");
        }
    }

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
