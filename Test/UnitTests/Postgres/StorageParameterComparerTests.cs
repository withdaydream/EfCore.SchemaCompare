using System.Collections.Generic;
using System.Linq;
using EfSchemaCompare;
using EfSchemaCompare.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

public class StorageParameterComparerTests
{
    //PostgreSQL holds the value as the text it was set with, so these are different even though PostgreSQL reads them the same
    [Theory]
    [InlineData("autovacuum_enabled", false, "false", "off")]
    [InlineData("autovacuum_enabled", true, "true", "1")]
    [InlineData("autovacuum_vacuum_scale_factor", 0.05, "0.05", "0.050")]
    [InlineData("autovacuum_vacuum_scale_factor", 0.05, "0.05", "5e-2")]
    [InlineData("vacuum_index_cleanup", "auto", "auto", "AUTO")]
    public void CompareTable_SameValueWrittenDifferentlyIsDifferent(string name, object efValue, string efText, string dbValue)
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table(name, dbValue));

        //ATTEMPT
        comparer.CompareTable(CreateLogger(CompareType.Table, logs), "public", "widget", EfParameters((name, efValue)));

        //VERIFY
        logs.Single().ToString().ShouldEqual(
            $"DIFFERENT: Table 'widget', storage parameter. Expected = {name}={efText}, found = {name}={dbValue}");
    }

    [Fact]
    public void CompareTable_MatchingParametersLogNothing()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table("fillfactor", "70"), Table("autovacuum_enabled", "false"));

        //ATTEMPT
        comparer.CompareTable(CreateLogger(CompareType.Table, logs), "public", "widget",
            EfParameters(("fillfactor", 70), ("autovacuum_enabled", false)));

        //VERIFY
        logs.Count.ShouldEqual(0);
    }

    [Fact]
    public void CompareTable_DefaultSetOnBothSidesMatches()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table("fillfactor", "100"));

        //ATTEMPT
        comparer.CompareTable(CreateLogger(CompareType.Table, logs), "public", "widget", EfParameters(("fillfactor", 100)));

        //VERIFY
        logs.Count.ShouldEqual(0);
    }

    [Fact]
    public void CompareTable_DefaultSetInDatabaseButNotInEfIsExtra()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table("fillfactor", "100"));

        //ATTEMPT
        comparer.CompareTable(CreateLogger(CompareType.Table, logs), "public", "widget", EfParameters());

        //VERIFY
        logs.Single().ToString().ShouldEqual("EXTRA IN DATABASE: Table 'widget', storage parameter. Found = fillfactor=100");
    }

    [Fact]
    public void CompareTable_DefaultSetInEfButNotInDatabaseIsNotInDatabase()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table(null, null));

        //ATTEMPT
        comparer.CompareTable(CreateLogger(CompareType.Table, logs), "public", "widget", EfParameters(("fillfactor", 100)));

        //VERIFY
        logs.Single().ToString().ShouldEqual("NOT IN DATABASE: Table 'widget', storage parameter. Expected = fillfactor=100");
    }

    [Fact]
    public void CompareTable_ReportsEachKindOfDifference()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table("fillfactor", "50"), Table("parallel_workers", "4"));

        //ATTEMPT
        comparer.CompareTable(CreateLogger(CompareType.Table, logs), "public", "widget",
            EfParameters(("fillfactor", 70), ("autovacuum_vacuum_scale_factor", 0.05)));

        //VERIFY
        logs.Select(x => x.ToString()).ToArray().ShouldEqual(new[]
        {
            "NOT IN DATABASE: Table 'widget', storage parameter. Expected = autovacuum_vacuum_scale_factor=0.05",
            "DIFFERENT: Table 'widget', storage parameter. Expected = fillfactor=70, found = fillfactor=50",
            "EXTRA IN DATABASE: Table 'widget', storage parameter. Found = parallel_workers=4",
        });
    }

    [Fact]
    public void CompareIndex_OnlyComparesTheIndexsOwnParameters()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table("fillfactor", "70"), Index("ix_widget_name", "fillfactor", "90"));

        //ATTEMPT
        comparer.CompareIndex(CreateLogger(CompareType.Index, logs), "public", "widget", "ix_widget_name", EfParameters());

        //VERIFY
        logs.Single().ToString().ShouldEqual("EXTRA IN DATABASE: Index 'widget', storage parameter. Found = fillfactor=90");
    }

    [Fact]
    public void CompareTable_TableNotInDatabaseLogsNothing()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(Table("fillfactor", "50"));

        //ATTEMPT
        comparer.CompareTable(CreateLogger(CompareType.Table, logs), "public", "other", EfParameters(("fillfactor", 70)));

        //VERIFY
        logs.Count.ShouldEqual(0);
    }

    //------------------------------------------------------

    private static StorageParameterComparer CreateComparer(params IConstraintReader.StorageParameter[] dbParameters)
    {
        return new StorageParameterComparer(dbParameters);
    }

    private static CompareLogger2 CreateLogger(CompareType type, List<CompareLog> logs)
    {
        return new CompareLogger2(type, "widget", logs, null, () => { });
    }

    private static IEnumerable<IAnnotation> EfParameters(params (string Name, object Value)[] parameters)
    {
        return parameters.Select(x => new Annotation(StorageParameterComparer.AnnotationPrefix + x.Name, x.Value));
    }

    /// <param name="name">Null for a table without any storage parameters</param>
    /// <param name="value"></param>
    private static IConstraintReader.StorageParameter Table(string name, string value)
    {
        return new IConstraintReader.StorageParameter
        {
            SchemaName = "public", TableName = "widget", ParameterName = name, ParameterValue = value
        };
    }

    private static IConstraintReader.StorageParameter Index(string index, string name, string value)
    {
        return new IConstraintReader.StorageParameter
        {
            SchemaName = "public", TableName = "widget", IndexName = index, ParameterName = name, ParameterValue = value
        };
    }
}
