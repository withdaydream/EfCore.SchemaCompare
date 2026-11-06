using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EfSchemaCompare.Internal;

/// <summary>
/// Compares the storage parameters, e.g. fillfactor, that EF Core sets via HasStorageParameter on an entity or index
/// with the ones set on the table or index in the database. A parameter must be set on both sides or neither, even if
/// the one side sets it to PostgreSQL's default, e.g. fillfactor=100 on a table. PostgreSQL holds a value as the text it
/// was set with, and values must be the same text, e.g. "on" doesn't match "true" and "0.050" doesn't match "0.05".
/// </summary>
internal class StorageParameterComparer
{
    /// <summary>
    /// The prefix of the annotations that Npgsql's HasStorageParameter adds, followed by the parameter's name
    /// </summary>
    internal const string AnnotationPrefix = "Npgsql:StorageParameter:";

    private readonly Dictionary<(string Schema, string Table, string Index), Dictionary<string, string>> _dbRelations;

    internal StorageParameterComparer(IReadOnlyList<IConstraintReader.StorageParameter> dbStorageParameters)
    {
        _dbRelations = dbStorageParameters
            .GroupBy(x => (x.SchemaName, x.TableName, x.IndexName))
            .ToDictionary(g => g.Key, g => g.Where(x => x.ParameterName != null)
                .ToDictionary(x => x.ParameterName, x => x.ParameterValue, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Compares a table's storage parameters. Does nothing if the table isn't in the database, as that is reported elsewhere
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="schema">The table's schema in the database</param>
    /// <param name="table"></param>
    /// <param name="efAnnotations">The design-time annotations of the entities mapped to the table</param>
    internal void CompareTable(CompareLogger2 logger, string schema, string table, IEnumerable<IAnnotation> efAnnotations)
    {
        if (_dbRelations.TryGetValue((schema, table, null), out var dbParameters))
            Compare(logger, dbParameters, efAnnotations);
    }

    /// <summary>
    /// Compares an index's storage parameters. Does nothing if the index isn't in the database, as that is reported elsewhere
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="schema">The schema of the index's table in the database</param>
    /// <param name="table"></param>
    /// <param name="index"></param>
    /// <param name="efAnnotations">The index's design-time annotations</param>
    internal void CompareIndex(CompareLogger2 logger, string schema, string table, string index, IEnumerable<IAnnotation> efAnnotations)
    {
        if (_dbRelations.TryGetValue((schema, table, index), out var dbParameters))
            Compare(logger, dbParameters, efAnnotations);
    }

    private static void Compare(CompareLogger2 logger, Dictionary<string, string> dbParameters, IEnumerable<IAnnotation> efAnnotations)
    {
        //PostgreSQL lower-cases a parameter's name, as it isn't quoted
        var efParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var annotation in efAnnotations.Where(x => x.Name.StartsWith(AnnotationPrefix, StringComparison.Ordinal) && x.Value != null))
            efParameters.TryAdd(annotation.Name.Substring(AnnotationPrefix.Length).ToLowerInvariant(), FormatEfValue(annotation.Value));

        foreach (var name in efParameters.Keys.Union(dbParameters.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            efParameters.TryGetValue(name, out var efValue);
            dbParameters.TryGetValue(name, out var dbValue);

            if (efValue == null)
                logger.ExtraInDatabase($"{name}={dbValue}", CompareAttributes.StorageParameter);
            else if (dbValue == null)
                logger.NotInDatabase($"{name}={efValue}", CompareAttributes.StorageParameter);
            else
                logger.CheckDifferent($"{name}={efValue}", $"{name}={dbValue}", CompareAttributes.StorageParameter, StringComparison.Ordinal);
        }
    }

    private static string FormatEfValue(object value)
    {
        return value switch
        {
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }
}
