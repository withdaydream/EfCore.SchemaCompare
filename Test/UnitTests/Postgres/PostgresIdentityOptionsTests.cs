using EfSchemaCompare.Internal.Postgres;
using Xunit;
using Xunit.Extensions.AssertExtensions;

namespace Test.UnitTests.Postgres;

public class PostgresIdentityOptionsTests
{
    [Theory]
    [InlineData("integer", 2147483647L)]
    [InlineData("bigint", 9223372036854775807L)]
    [InlineData("smallint", 32767L)]
    public void Parse_NoOptionsGivesPostgresDefaults(string storeType, long expectedMax)
    {
        //ATTEMPT
        var options = PostgresIdentityOptions.Parse(null, storeType);

        //VERIFY
        options.ShouldEqual(new PostgresIdentityOptions(1, 1, 1, expectedMax, false, 1));
    }

    [Fact]
    public void Parse_ReadsSetOptionsAndDefaultsTheRest()
    {
        //ATTEMPT
        var options = PostgresIdentityOptions.Parse("'100', '5', '', '', 'True', '1'", "integer");

        //VERIFY
        options.ShouldEqual(new PostgresIdentityOptions(100, 5, 1, int.MaxValue, true, 1));
    }

    [Fact]
    public void Parse_DescendingSequenceHasDescendingDefaults()
    {
        //ATTEMPT
        var options = PostgresIdentityOptions.Parse("'', '-1', '', '', 'False', ''", "integer");

        //VERIFY
        options.ShouldEqual(new PostgresIdentityOptions(-1, -1, int.MinValue, -1, false, 1));
    }

    [Fact]
    public void Parse_OptionsSetToTheirDefaultsEqualNoOptions()
    {
        //ATTEMPT
        var setToDefaults = PostgresIdentityOptions.Parse("'1', '1', '1', '2147483647', 'False', '1'", "integer");

        //VERIFY
        setToDefaults.ShouldEqual(PostgresIdentityOptions.Parse(null, "integer"));
    }

    [Fact]
    public void GetOptionTexts()
    {
        //ATTEMPT
        var texts = PostgresIdentityOptions.Parse("'100', '5', '', '', 'True', '1'", "integer").GetOptionTexts();

        //VERIFY
        texts.ShouldEqual(new[] { "start=100", "increment=5", "min=1", "max=2147483647", "cycle=true", "cache=1" });
    }
}
