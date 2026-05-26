// Copyright (c) 2023 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using DataLayer.SchemaDb;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using TestSupport.EfHelpers;
using TestSupport.Helpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests;

public class TestSchemas
{
    private readonly ITestOutputHelper _output;

    public TestSchemas(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CompareEfSqlServer()
    {
        //SETUP
        var options = this.CreateUniqueClassOptions<SchemaDbContext>();
        using var context = new SchemaDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(context);

        //VERIFY
        hasErrors.ShouldBeFalse();
    }

    [Fact]
    public void CompareEfSqlServerExcludeTableWithDefaultSchema()
    {
        //SETUP
        var options = this.CreateUniqueClassOptions<SchemaDbContext>();
        using var context = new SchemaDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        var config = new CompareEfSqlConfig
        {
            TablesToIgnoreCommaDelimited = "SchemaTest"
        };
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual("NOT IN DATABASE: Entity 'Book', table name. Expected = SchemaTest");
    }

    [Fact]
    public void CompareEfSqlServerExcludeTableWithSchema()
    {
        //SETUP
        var options = this.CreateUniqueClassOptions<SchemaDbContext>();
        using var context = new SchemaDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        var config = new CompareEfSqlConfig
        {
            TablesToIgnoreCommaDelimited = "Schema2.SchemaTest"
        };
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual("NOT IN DATABASE: Entity 'Review', table name. Expected = Schema2.SchemaTest");
    }

    [Fact]
    public void CompareEfSqlServerExcludeMultipleTables()
    {
        //SETUP
        var options = this.CreateUniqueClassOptions<SchemaDbContext>();
        using var context = new SchemaDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        var config = new CompareEfSqlConfig
        {
            TablesToIgnoreCommaDelimited = "SchemaTest,Schema1.SchemaTest , Schema2.SchemaTest "
        };
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(string.Join(Environment.NewLine,
            "NOT IN DATABASE: Entity 'Author', table name. Expected = Schema1.SchemaTest",
            "NOT IN DATABASE: Entity 'Book', table name. Expected = SchemaTest",
            "NOT IN DATABASE: Entity 'Review', table name. Expected = Schema2.SchemaTest"));
    }

    [Fact]
    public void CompareEfSqlServerExcludeBadTable()
    {
        //SETUP
        var options = this.CreateUniqueClassOptions<SchemaDbContext>();
        using var context = new SchemaDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        var config = new CompareEfSqlConfig
        {
            TablesToIgnoreCommaDelimited = "BadTableName"
        };
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        try
        {
            comparer.CompareEfWithDb(context);
        }
        catch (Exception e)
        {
            e.Message.ShouldEqual("The TablesToIgnoreCommaDelimited config property contains a table name of 'BadTableName', which was not found in the database");
            return;
        }

        //VERIFY
        true.ShouldBeFalse();
    }

    [Fact]
    public void CompareEfPostgreSqlExcludeMultipleTables()
    {
        //SETUP
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var builder = new
            DbContextOptionsBuilder<SchemaDbContext>();
        builder.UseNpgsql(postgresConnectionString);
        using var context = new SchemaDbContext(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        var config = new CompareEfSqlConfig
        {
            TablesToIgnoreCommaDelimited = "SchemaTest,Schema1.SchemaTest , Schema2.SchemaTest "
        };
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(postgresConnectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(string.Join(Environment.NewLine,
            "NOT IN DATABASE: Entity 'Author', table name. Expected = Schema1.SchemaTest",
            "NOT IN DATABASE: Entity 'Book', table name. Expected = SchemaTest",
            "NOT IN DATABASE: Entity 'Review', table name. Expected = Schema2.SchemaTest",
            "DIFFERENT: DbContext 'SchemaDbContext', foreign key. Expected = FK_SchemaTest_SchemaTest_AuthorId Table(SchemaTest) Columns(AuthorId) ForeignTable(SchemaTest) ForeignColumns(AuthorId) OnUpdate(NO ACTION) OnDelete(CASCADE), found = FK_SchemaTest_SchemaTest_AuthorId Table(SchemaTest) Columns(AuthorId) ForeignTable(Schema1.SchemaTest) ForeignColumns(AuthorId) OnUpdate(NO ACTION) OnDelete(CASCADE)",
            "DIFFERENT: DbContext 'SchemaDbContext', foreign key. Expected = FK_SchemaTest_SchemaTest_ReviewId Table(SchemaTest) Columns(ReviewId) ForeignTable(SchemaTest) ForeignColumns(ReviewId) OnUpdate(NO ACTION) OnDelete(NO ACTION), found = FK_SchemaTest_SchemaTest_ReviewId Table(SchemaTest) Columns(ReviewId) ForeignTable(Schema2.SchemaTest) ForeignColumns(ReviewId) OnUpdate(NO ACTION) OnDelete(NO ACTION)"));
    }

    [Fact]
    public void CompareEfPostgreSqlCheckConstraintsNotInDatabase()
    {
        //SETUP
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var builder = new
            DbContextOptionsBuilder<CheckConstraintsContext>();
        builder.UseNpgsql(postgresConnectionString);
        using var context = new CheckConstraintsContext(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        // Remove constraint to cause comparison error.
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE public."Book"
            DROP CONSTRAINT "CK_book_CheckConstraint";
            """
        );

        var config = new CompareEfSqlConfig();
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(postgresConnectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(@"NOT IN DATABASE: DbContext 'CheckConstraintsContext', check constraint. Expected = Book CK_book_CheckConstraint (((""Title"" IS NOT NULL AND ""PublishedOn"" IS NOT NULL) OR (""Title"" IS NULL AND ""PublishedOn"" IS NULL)))");
    }

    [Fact]
    public void CompareEfPostgreSqlCheckConstraintsExtraInDatabase()
    {
        //SETUP
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var builder = new
            DbContextOptionsBuilder<CheckConstraintsContext>();
        builder.UseNpgsql(postgresConnectionString);
        using var context = new CheckConstraintsContext(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        // Remove constraint to cause comparison error.
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE public."Book"
            ADD CONSTRAINT CK_New_One
            CHECK ("Description" IS NOT NULL);
            """
        );

        var config = new CompareEfSqlConfig();
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(postgresConnectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(@"EXTRA IN DATABASE: DbContext 'CheckConstraintsContext', check constraint. Found = Book ck_new_one ((""Description"" IS NOT NULL))");
    }

    [Fact]
    public void CompareEfPostgreSqlFkWithNoDeleteAction()
    {
        //SETUP
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var builder = new
            DbContextOptionsBuilder<CheckConstraintsContext>();
        builder.UseNpgsql(postgresConnectionString);
        using var context = new CheckConstraintsContext(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        // Update constraint to cause comparison error.
        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE IF EXISTS public."Book" 
                DROP CONSTRAINT IF EXISTS "FK_Book_Author_AuthorId";

            ALTER TABLE IF EXISTS public."Book"
                ADD CONSTRAINT "FK_Book_Author_AuthorId" FOREIGN KEY ("AuthorId")
                REFERENCES public."Author" ("AuthorId") MATCH SIMPLE
                ON UPDATE NO ACTION
                ON DELETE NO ACTION;
            """
        );

        var config = new CompareEfSqlConfig();
        var comparer = new CompareEfSql(config);

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(postgresConnectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        comparer.GetAllErrors.ShouldEqual(@"DIFFERENT: DbContext 'CheckConstraintsContext', foreign key. Expected = FK_Book_Author_AuthorId Table(Book) Columns(AuthorId) ForeignTable(Author) ForeignColumns(AuthorId) OnUpdate(NO ACTION) OnDelete(CASCADE), found = FK_Book_Author_AuthorId Table(Book) Columns(AuthorId) ForeignTable(Author) ForeignColumns(AuthorId) OnUpdate(NO ACTION) OnDelete(NO ACTION)");
    }

    [Fact]
    public void CompareEfPostgreSqlNormalizesEquivalentCheckConstraints()
    {
        //SETUP
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var builder = new
            DbContextOptionsBuilder<PostgreSqlConstraintNormalizationContext>();
        builder.UseNpgsql(postgresConnectionString);
        using var context = new PostgreSqlConstraintNormalizationContext(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(postgresConnectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeFalse();
    }

    [Fact]
    public void CompareEfPostgreSqlReportsDifferentCheckConstraintClauseWithSameName()
    {
        //SETUP
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var builder = new
            DbContextOptionsBuilder<PostgreSqlConstraintNormalizationContext>();
        builder.UseNpgsql(postgresConnectionString);
        using var context = new PostgreSqlConstraintNormalizationContext(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        context.Database.ExecuteSqlRaw(
            """
            ALTER TABLE public.check_normalization_record
            DROP CONSTRAINT "CK_check_normalization_status";

            ALTER TABLE public.check_normalization_record
            ADD CONSTRAINT "CK_check_normalization_status"
            CHECK (status = 'IN_PROGRESS');
            """
        );

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(postgresConnectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        Assert.Contains("DIFFERENT: DbContext 'PostgreSqlConstraintNormalizationContext', check constraint.", comparer.GetAllErrors);
        Assert.Contains("CK_check_normalization_status", comparer.GetAllErrors);
    }

    [Fact]
    public void CompareEfPostgreSqlReportsIndexNameMismatchEvenWhenStructureMatches()
    {
        //SETUP
        var postgresConnectionString = this.GetUniquePostgreSqlConnectionString();
        var builder = new
            DbContextOptionsBuilder<PostgreSqlConstraintNormalizationContext>();
        builder.UseNpgsql(postgresConnectionString);
        using var context = new PostgreSqlConstraintNormalizationContext(builder.Options);
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();

        context.Database.ExecuteSqlRaw(
            """
            ALTER INDEX public."IX_check_normalization_record_status"
            RENAME TO "IX_check_normalization_record_status_renamed";
            """
        );

        var comparer = new CompareEfSql();

        //ATTEMPT
        var hasErrors = comparer.CompareEfWithDb(postgresConnectionString, context);

        //VERIFY
        _output.WriteLine(comparer.GetAllErrors);
        hasErrors.ShouldBeTrue();
        Assert.Contains("NOT IN DATABASE: CheckNormalizationRecord->Index 'status', index constraint name. Expected = IX_check_normalization_record_status", comparer.GetAllErrors);
    }

    private class PostgreSqlConstraintNormalizationContext : DbContext
    {
        public PostgreSqlConstraintNormalizationContext(DbContextOptions<PostgreSqlConstraintNormalizationContext> options)
            : base(options) { }

        public DbSet<CheckNormalizationRecord> Records { get; set; }
        public DbSet<CheckNormalizationParent> Parents { get; set; }
        public DbSet<CheckNormalizationChild> Children { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CheckNormalizationRecord>(entity =>
            {
                entity.ToTable("check_normalization_record", table =>
                {
                    table.HasCheckConstraint("CK_check_normalization_status",
                        "\"status\" IN ('IN_PROGRESS', 'CANCEL_REQUESTED')");
                    table.HasCheckConstraint("CK_check_normalization_complete_status",
                        "(\"complete_time\" IS NULL) = (\"status\" IN ('IN_PROGRESS', 'CANCEL_REQUESTED'))");
                });
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Status).HasColumnName("status");
                entity.Property(e => e.CompleteTime).HasColumnName("complete_time");
                entity.HasIndex(e => e.Status).HasDatabaseName("IX_check_normalization_record_status");
            });

            modelBuilder.Entity<CheckNormalizationParent>(entity =>
            {
                entity.ToTable("check_normalization_parent");
                entity.Property(e => e.Id).HasColumnName("id");
            });

            modelBuilder.Entity<CheckNormalizationChild>(entity =>
            {
                entity.ToTable("check_normalization_child");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.ParentId).HasColumnName("parent_id");
                entity.HasOne(e => e.Parent)
                    .WithMany()
                    .HasForeignKey(e => e.ParentId)
                    .HasConstraintName("FK_check_normalization_child_parent_parent_id");
            });
        }
    }

    private class CheckNormalizationRecord
    {
        public int Id { get; set; }
        public string Status { get; set; }
        public DateTime? CompleteTime { get; set; }
    }

    private class CheckNormalizationParent
    {
        public int Id { get; set; }
    }

    private class CheckNormalizationChild
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public CheckNormalizationParent Parent { get; set; }
    }
}