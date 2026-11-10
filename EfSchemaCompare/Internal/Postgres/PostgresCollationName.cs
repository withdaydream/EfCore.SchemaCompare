using System.Text;

namespace EfSchemaCompare.Internal.Postgres;

/// <summary>
/// From version 11, Npgsql's scaffolder writes a collation whose name contains a dot as a quoted identifier, e.g. "en_US.utf8",
/// so it isn't read as a schema and a name. Npgsql still treats a name without quotes, e.g. en_US.utf8, as one name, so
/// UseCollation("en_US.utf8") and UseCollation("\"en_US.utf8\"") create the same collation. A collation that is one quoted
/// identifier is therefore compared as the name inside the quotes.
/// </summary>
internal static class PostgresCollationName
{
    /// <summary>
    /// Returns the name inside a collation that is one quoted identifier, e.g. "en_US.utf8" returns en_US.utf8,
    /// and "a""b" returns a"b, as a quote inside a quoted identifier is written twice. Anything else is returned as it is,
    /// e.g. C, en_US.utf8, or a schema-qualified collation such as "my_schema"."my_collation"
    /// </summary>
    internal static string Unquote(string collation)
    {
        if (collation is not { Length: > 2 } || collation[0] != '"' || collation[^1] != '"')
            return collation;

        var name = new StringBuilder(collation.Length - 2);
        for (var i = 1; i < collation.Length - 1; i++)
        {
            if (collation[i] == '"')
            {
                //A single quote ends the identifier, so this is more than one, e.g. "my_schema"."my_collation"
                if (i + 1 == collation.Length - 1 || collation[i + 1] != '"')
                    return collation;
                i++;
            }
            name.Append(collation[i]);
        }
        return name.ToString();
    }
}
