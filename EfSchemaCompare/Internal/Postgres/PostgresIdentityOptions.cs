using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EfSchemaCompare.Internal.Postgres;

/// <summary>
/// The options of an identity column's sequence, e.g. its start value, held in Npgsql's IdentitySequenceOptions annotation
/// by both HasIdentityOptions and the scaffolder. PostgreSQL stores every option's value, so it can't tell an option that
/// was set to its default from one that wasn't set. An option that isn't set is therefore given its default value.
/// </summary>
internal record PostgresIdentityOptions(long Start, long Increment, long Min, long Max, bool Cycle, long Cache)
{
    internal const string AnnotationName = "Npgsql:IdentitySequenceOptions";

    /// <summary>
    /// Parses the annotation's value, e.g. "'100', '5', '', '', 'True', '1'", which holds the start, increment,
    /// min, max, cycle and cache options in that order. An empty value means the option isn't set.
    /// </summary>
    /// <param name="serialized">The annotation's value, or null if no options are set</param>
    /// <param name="storeType">The column's type, which sets the default min and max values</param>
    internal static PostgresIdentityOptions Parse(string serialized, string storeType)
    {
        var values = serialized == null
            ? new string[6]
            : Regex.Matches(serialized, "'([^']*)'").Select(m => m.Groups[1].Value.Length == 0 ? null : m.Groups[1].Value).ToArray();
        if (values.Length != 6)
            throw new InvalidOperationException($"Unexpected format of the {AnnotationName} annotation: {serialized}");

        var (typeMin, typeMax) = (storeType ?? "").ToLowerInvariant() switch
        {
            "smallint" or "int2" => ((long)short.MinValue, (long)short.MaxValue),
            "bigint" or "int8" => (long.MinValue, long.MaxValue),
            _ => ((long)int.MinValue, (long)int.MaxValue)
        };
        var increment = ParseLong(values[1]) ?? 1;
        var min = ParseLong(values[2]) ?? (increment > 0 ? 1 : typeMin);
        var max = ParseLong(values[3]) ?? (increment > 0 ? typeMax : -1);
        var start = ParseLong(values[0]) ?? (increment > 0 ? min : max);
        var cycle = values[4] != null && bool.Parse(values[4]);
        var cache = ParseLong(values[5]) ?? 1;
        return new PostgresIdentityOptions(start, increment, min, max, cycle, cache);
    }

    /// <summary>
    /// Each option as name=value, in a fixed order, for comparing and showing in the logs
    /// </summary>
    internal IReadOnlyList<string> GetOptionTexts()
    {
        return
        [
            $"start={Start}",
            $"increment={Increment}",
            $"min={Min}",
            $"max={Max}",
            $"cycle={Cycle.ToString().ToLowerInvariant()}",
            $"cache={Cache}"
        ];
    }

    private static long? ParseLong(string value)
    {
        return value == null ? null : long.Parse(value, CultureInfo.InvariantCulture);
    }
}
