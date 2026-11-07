using System.Linq;
using EfSchemaCompare.Internal.Postgres;
using Xunit;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

public class PostgresCheckExpressionParserTests
{
    [Fact]
    public void Tokenize_HandlesQuotedIdentifiersStringsCastsAndArrays()
    {
        //SETUP
        const string sql = @"""status"" = ANY (ARRAY['IN_PROGRESS'::character varying, 'CANCELLED'::text]::text[])";

        //ATTEMPT
        var tokens = PostgresCheckExpressionTokenizer.Tokenize(sql);

        //VERIFY
        tokens.Count(t => t.Kind == PostgresCheckTokenKind.DoubleColon).ShouldEqual(3);
        tokens.Any(t => t.Kind == PostgresCheckTokenKind.Identifier && t.Text == "status").ShouldBeTrue();
        tokens.Any(t => t.Kind == PostgresCheckTokenKind.String && t.Text == "IN_PROGRESS").ShouldBeTrue();
        tokens.Any(t => t.Kind == PostgresCheckTokenKind.LeftBracket).ShouldBeTrue();
    }

    [Fact]
    public void Parse_RespectsAndOrPrecedence()
    {
        //ATTEMPT
        var result = PostgresCheckExpressionParser.TryParse("a = 1 OR b = 2 AND c = 3");

        //VERIFY
        result.Success.ShouldBeTrue(result.Error);
        var expression = (PostgresBinaryExpression)result.Expression!;
        expression.Operator.ShouldEqual("or");
        ((PostgresBinaryExpression)expression.Right).Operator.ShouldEqual("and");
    }

    [Fact]
    public void Parse_HandlesBinaryAddition()
    {
        //ATTEMPT
        var result = PostgresCheckExpressionParser.TryParse(
            "total_token_count = input_token_count + output_token_count");

        //VERIFY
        result.Success.ShouldBeTrue(result.Error);
        var comparison = (PostgresBinaryExpression)result.Expression!;
        ((PostgresBinaryExpression)comparison.Right).Operator.ShouldEqual("+");
    }

    [Fact]
    public void Parse_RespectsMultiplicationAdditionAndConcatenationPrecedence()
    {
        //ATTEMPT
        var result = PostgresCheckExpressionParser.TryParse("a || b * c + d");

        //VERIFY
        result.Success.ShouldBeTrue(result.Error);
        var concatenation = (PostgresBinaryExpression)result.Expression!;
        concatenation.Operator.ShouldEqual("||");
        var addition = (PostgresBinaryExpression)concatenation.Right;
        addition.Operator.ShouldEqual("+");
        ((PostgresBinaryExpression)addition.Left).Operator.ShouldEqual("*");
    }

    [Fact]
    public void Canonicalize_HandlesPostgresComputedColumnRewrite()
    {
        AssertEquivalent(
            "first_name||' '||last_name",
            "(((first_name || ' '::text) || last_name))");
    }

    [Fact]
    public void Canonicalize_AllowsCastsPostgresAddsAndTypeNamesItRewrites()
    {
        AssertEquivalent("price * quantity", "(price * (quantity)::numeric)");
        AssertEquivalent("price * 2", "(price * (2)::numeric)");
        AssertEquivalent("a::int > 0", "((a)::integer > 0)");
    }

    [Fact]
    public void Canonicalize_KeepsCastsOnlyEfCoreHasOrDifferentCastsDifferent()
    {
        //A cast only the database has is one PostgreSQL added, but one only EF Core has is a different expression
        PostgresCheckExpressionStructuralComparer.AreEquivalent(Parse("a::bigint > 0"), Parse("(a > 0)")).ShouldBeFalse();
        PostgresCheckExpressionStructuralComparer.AreEquivalent(Parse("a::bigint > 0"), Parse("((a)::integer > 0)")).ShouldBeFalse();
    }

    [Fact]
    public void Canonicalize_KeepsDifferentGroupingDifferent()
    {
        //SETUP
        var first = Parse("a * b + c");
        var second = Parse("a * (b + c)");

        //ATTEMPT / VERIFY
        PostgresCheckExpressionStructuralComparer.AreEquivalent(first, second).ShouldBeFalse();
    }

    [Fact]
    public void Parse_HandlesCaseExpression()
    {
        //ATTEMPT
        var result = PostgresCheckExpressionParser.TryParse("CASE WHEN hash_sha256 IS NULL THEN upload_time IS NULL ELSE true END");

        //VERIFY
        result.Success.ShouldBeTrue(result.Error);
        var expression = (PostgresCaseExpression)result.Expression!;
        expression.Whens.Count.ShouldEqual(1);
        expression.ElseResult.ShouldNotBeNull();
    }

    [Fact]
    public void Canonicalize_HandlesPostgresAnyArrayRewrite()
    {
        AssertEquivalent(
            """(("status" IN ('IN_PROGRESS', 'CANCEL_REQUESTED')))""",
            """(((status)::text = ANY ((ARRAY['IN_PROGRESS'::character varying, 'CANCEL_REQUESTED'::character varying])::text[])))""");
    }

    [Fact]
    public void Canonicalize_HandlesPostgresAllArrayRewrite()
    {
        AssertEquivalent(
            """(((cancel_requested_time IS NULL) = (status NOT IN ('CANCEL_REQUESTED', 'CANCELLED'))))""",
            """(((cancel_requested_time IS NULL) = ((status)::text <> ALL ((ARRAY['CANCEL_REQUESTED'::character varying, 'CANCELLED'::character varying])::text[]))))""");
    }

    [Fact]
    public void Canonicalize_HandlesNestedPostgresAnyArrayRewrite()
    {
        AssertEquivalent(
            """((("complete_time" IS NULL) = ("status" IN ('IN_PROGRESS', 'CANCEL_REQUESTED'))))""",
            """(((complete_time IS NULL) = ((status)::text = ANY ((ARRAY['IN_PROGRESS'::character varying, 'CANCEL_REQUESTED'::character varying])::text[]))))""");
    }

    [Fact]
    public void Canonicalize_KeepsDifferentClausesDifferent()
    {
        //SETUP
        var first = Parse("""(("status" IN ('IN_PROGRESS', 'CANCEL_REQUESTED')))""");
        var second = Parse("""(("status" = 'IN_PROGRESS'))""");

        //ATTEMPT / VERIFY
        PostgresCheckExpressionStructuralComparer.AreEquivalent(first, second).ShouldBeFalse();
    }

    [Fact]
    public void Parse_FailsClosedForUnsupportedSyntax()
    {
        //ATTEMPT
        var result = PostgresCheckExpressionParser.TryParse("status BETWEEN 'A' AND 'Z'");

        //VERIFY
        result.Success.ShouldBeFalse();
    }

    private static void AssertEquivalent(string modelSql, string databaseSql)
    {
        var model = Parse(modelSql);
        var database = Parse(databaseSql);
        PostgresCheckExpressionStructuralComparer.AreEquivalent(model, database).ShouldBeTrue();
    }

    private static PostgresCheckExpression Parse(string sql)
    {
        var result = PostgresCheckExpressionParser.TryParse(sql);
        result.Success.ShouldBeTrue(result.Error);
        return result.Expression!;
    }
}
