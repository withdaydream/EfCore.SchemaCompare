using System.Collections.Generic;
using EfSchemaCompare;
using EfSchemaCompare.Internal;
using Xunit;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

public class PostgresCheckConstraintComparerTests
{
    [Fact]
    public void Compare_EquivalentParsedCheckConstraintsDoesNotLogDrift()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(logs);

        var modelConstraints = new[]
        {
            Check("record", "CK_record_status", """(("status" IN ('IN_PROGRESS', 'CANCEL_REQUESTED')))""")
        };
        var dbConstraints = new[]
        {
            Check("record", "CK_record_status", """(((status)::text = ANY ((ARRAY['IN_PROGRESS'::character varying, 'CANCEL_REQUESTED'::character varying])::text[])))""")
        };

        //ATTEMPT
        comparer.Compare(dbConstraints, modelConstraints, CompareAttributes.CheckConstraint);

        //VERIFY
        logs.Count.ShouldEqual(0);
    }

    [Fact]
    public void Compare_DifferentCheckClauseWithSameNameLogsDifferent()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(logs);

        var modelConstraints = new[]
        {
            Check("record", "CK_record_status", """(("status" IN ('IN_PROGRESS', 'CANCEL_REQUESTED')))""")
        };
        var dbConstraints = new[]
        {
            Check("record", "CK_record_status", """(("status" = 'IN_PROGRESS'))""")
        };

        //ATTEMPT
        comparer.Compare(dbConstraints, modelConstraints, CompareAttributes.CheckConstraint);

        //VERIFY
        logs.Count.ShouldEqual(1);
        logs[0].State.ShouldEqual(CompareState.Different);
        logs[0].Attribute.ShouldEqual(CompareAttributes.CheckConstraint);
    }

    [Fact]
    public void Compare_UnsupportedDifferentCheckClauseFailsClosed()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(logs);

        var modelConstraints = new[]
        {
            Check("record", "CK_record_status", "status BETWEEN 'A' AND 'Z'")
        };
        var dbConstraints = new[]
        {
            Check("record", "CK_record_status", "status BETWEEN 'A' AND 'Y'")
        };

        //ATTEMPT
        comparer.Compare(dbConstraints, modelConstraints, CompareAttributes.CheckConstraint);

        //VERIFY
        logs.Count.ShouldEqual(1);
        logs[0].State.ShouldEqual(CompareState.Different);
    }

    [Fact]
    public void Compare_NameMismatchLogsMissingAndExtra()
    {
        //SETUP
        var logs = new List<CompareLog>();
        var comparer = CreateComparer(logs);

        var modelConstraints = new[]
        {
            Check("record", "CK_record_status", """(("status" = 'IN_PROGRESS'))""")
        };
        var dbConstraints = new[]
        {
            Check("record", "CK_record_status_renamed", """(("status" = 'IN_PROGRESS'))""")
        };

        //ATTEMPT
        comparer.Compare(dbConstraints, modelConstraints, CompareAttributes.CheckConstraint);

        //VERIFY
        logs.Count.ShouldEqual(2);
        logs[0].State.ShouldEqual(CompareState.ExtraInDatabase);
        logs[1].State.ShouldEqual(CompareState.NotInDatabase);
    }

    private static ConstraintComparer CreateComparer(IList<CompareLog> logs)
    {
        return new ConstraintComparer(new CompareLogger2(
            CompareType.DbContext,
            "TestContext",
            logs,
            new List<CompareLog>(),
            () => { }));
    }

    private static IConstraintReader.CheckConstraint Check(string table, string name, string clause)
    {
        return new IConstraintReader.CheckConstraint
        {
            TableName = table,
            ConstraintName = name,
            CheckClause = clause
        };
    }
}
