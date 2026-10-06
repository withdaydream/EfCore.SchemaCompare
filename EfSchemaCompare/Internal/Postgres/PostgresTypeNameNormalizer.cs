using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EfSchemaCompare.Internal.Postgres;

/// <summary>
/// Turns a store type, e.g. "int4" or "character varying(100)", into the name Postgres's
/// format_type(oid, NULL) returns, e.g. "integer" or "character varying", so that function
/// signatures from the EF Core model can be matched against pg_proc.
/// </summary>
internal static class PostgresTypeNameNormalizer
{
    //The aliases listed in the PostgreSQL docs (Data Types, Table 8.1) plus the internal names
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["int"] = "integer",
        ["int4"] = "integer",
        ["int2"] = "smallint",
        ["int8"] = "bigint",
        ["float4"] = "real",
        ["float8"] = "double precision",
        ["float"] = "double precision",
        ["bool"] = "boolean",
        ["varchar"] = "character varying",
        ["char"] = "character",
        ["bpchar"] = "character",
        ["decimal"] = "numeric",
        ["timestamptz"] = "timestamp with time zone",
        ["timestamp"] = "timestamp without time zone",
        ["timetz"] = "time with time zone",
        ["time"] = "time without time zone",
        ["varbit"] = "bit varying",
    };

    private static readonly Regex TypeModifier = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex ArraySuffix = new(@"(\s*\[\s*\d*\s*\])+$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string storeType)
    {
        if (string.IsNullOrWhiteSpace(storeType))
            return storeType;

        var type = Whitespace.Replace(storeType.Trim(), " ");
        //Quoted identifiers are case-sensitive, so only fold case on unquoted names
        if (!type.Contains('"'))
            type = type.ToLowerInvariant();

        var isArray = false;
        if (ArraySuffix.IsMatch(type))
        {
            isArray = true;
            type = ArraySuffix.Replace(type, "");
        }
        else if (type.StartsWith('_'))
        {
            //Internal array type name, e.g. _int4
            isArray = true;
            type = type.Substring(1);
        }

        //Functions arguments have no type modifiers, e.g. varchar(100) is just varchar
        type = TypeModifier.Replace(type, "").Trim();
        if (Aliases.TryGetValue(type, out var canonical))
            type = canonical;

        return isArray ? type + "[]" : type;
    }
}
