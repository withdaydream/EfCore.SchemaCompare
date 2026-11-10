using EfSchemaCompare.Internal.Postgres;
using Xunit;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

/// <summary>
/// Tests turning a collation that is one quoted identifier, as Npgsql 11's scaffolder writes a name containing a dot, into its name
/// </summary>
public class PostgresCollationNameTests
{
    [Theory]
    [InlineData("\"en_US.utf8\"", "en_US.utf8")]
    [InlineData("\"a\"\"b\"", "a\"b")]
    [InlineData("\"C\"", "C")]
    [InlineData("en_US.utf8", "en_US.utf8")]
    [InlineData("C", "C")]
    [InlineData("\"my_schema\".\"my_collation\"", "\"my_schema\".\"my_collation\"")]
    [InlineData("my_schema.\"my_collation\"", "my_schema.\"my_collation\"")]
    [InlineData("\"unterminated\"\"", "\"unterminated\"\"")]
    [InlineData("\"\"", "\"\"")]
    [InlineData("\"", "\"")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Unquote(string collation, string expected)
    {
        //ATTEMPT
        var name = PostgresCollationName.Unquote(collation);

        //VERIFY
        name.ShouldEqual(expected);
    }
}
