﻿// Copyright (c) 2023 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
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
        comparer.GetAllErrors.ShouldEqual(@"NOT IN DATABASE: Entity 'Author', table name. Expected = Schema1.SchemaTest
NOT IN DATABASE: Entity 'Book', table name. Expected = SchemaTest
NOT IN DATABASE: Entity 'Review', table name. Expected = Schema2.SchemaTest");
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
        comparer.GetAllErrors.ShouldEqual(@"NOT IN DATABASE: Entity 'Author', table name. Expected = Schema1.SchemaTest
NOT IN DATABASE: Entity 'Book', table name. Expected = SchemaTest
NOT IN DATABASE: Entity 'Review', table name. Expected = Schema2.SchemaTest");
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
        comparer.GetAllErrors.ShouldEqual(@"NOT IN DATABASE: DbContext 'CheckConstraintsContext', check constraint. Expected = Book CK_book_CheckConstraint (((""Title"" IS NOT NULL) = (""PublishedOn"" IS NOT NULL)))");
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
}