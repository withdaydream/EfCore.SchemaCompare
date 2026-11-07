using System;
using System.Collections.Generic;
using System.Linq;

namespace EfSchemaCompare.Internal;

/// <summary>
/// Compares the settings of a table and its indexes that the scaffolder doesn't read, using what the database's
/// <see cref="IConstraintReader"/> returns, so only PostgreSQL's are compared:
/// 1. An index's INCLUDE columns and NULLS [NOT] DISTINCT, which EF Core sets via IncludeProperties and AreNullsDistinct.
/// 2. Settings EF Core can't set, e.g. a row-level security policy, or an index on an expression. EF Core never creates
///    these, so each one in the database is reported as extra.
/// </summary>
internal class TableSettingsComparer
{
    /// <summary>
    /// The annotation Npgsql's IncludeProperties adds to an index, holding the names of the properties
    /// </summary>
    internal const string IncludeAnnotation = "Npgsql:IndexInclude";

    /// <summary>
    /// The annotation Npgsql's AreNullsDistinct adds to an index
    /// </summary>
    internal const string NullsDistinctAnnotation = "Npgsql:NullsDistinct";

    private const string NullsDistinct = "NULLS DISTINCT";
    private const string NullsNotDistinct = "NULLS NOT DISTINCT";

    private readonly Dictionary<(string Schema, string Table, string Index), IConstraintReader.IndexDetail> _indexes;
    private readonly ILookup<(string Schema, string Table), IConstraintReader.IndexDetail> _indexesByTable;
    private readonly ILookup<(string Schema, string Table), IConstraintReader.SettingEfCoreCantSet> _settingsByTable;

    internal TableSettingsComparer(IReadOnlyList<IConstraintReader.IndexDetail> indexes,
        IReadOnlyList<IConstraintReader.SettingEfCoreCantSet> settings)
    {
        _indexes = indexes.ToDictionary(x => (x.SchemaName, x.TableName, x.IndexName));
        _indexesByTable = indexes.ToLookup(x => (x.SchemaName, x.TableName));
        _settingsByTable = settings.ToLookup(x => (x.SchemaName, x.TableName));
    }

    /// <summary>
    /// Compares the INCLUDE columns, and for a unique index NULLS [NOT] DISTINCT, of an index EF Core declares.
    /// Does nothing if the index isn't in the database, as that is reported elsewhere
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="schema">The schema of the index's table in the database</param>
    /// <param name="table"></param>
    /// <param name="index"></param>
    /// <param name="efIncludeColumns">The columns of the properties in the index's IncludeProperties, or null if it has none</param>
    /// <param name="isUnique"></param>
    /// <param name="efNullsDistinct">What the index's AreNullsDistinct sets, or null if it isn't set</param>
    internal void CompareIndex(CompareLogger2 logger, string schema, string table, string index,
        IReadOnlyList<string> efIncludeColumns, bool isUnique, bool? efNullsDistinct)
    {
        if (!_indexes.TryGetValue((schema, table, index), out var dbIndex))
            return;

        //Set on both sides or neither, so an index without INCLUDE columns doesn't match one with them
        var efInclude = efIncludeColumns is { Count: > 0 } ? string.Join(",", efIncludeColumns) : null;
        logger.CheckDifferent(efInclude, dbIndex.IncludeColumns, CompareAttributes.IncludeColumns, StringComparison.Ordinal);

        //PostgreSQL stores whether nulls are distinct whether or not it was set, so it can't tell NULLS DISTINCT from
        //not setting it. That makes EF Core not setting it its default, NULLS DISTINCT. It only applies to a unique index
        if (isUnique)
            logger.CheckDifferent(efNullsDistinct == false ? NullsNotDistinct : NullsDistinct,
                dbIndex.NullsNotDistinct ? NullsNotDistinct : NullsDistinct, CompareAttributes.NullsDistinct, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports each setting of a table, its constraints and its indexes that EF Core can't set
    /// </summary>
    /// <param name="tableLogger">Logs the table's settings</param>
    /// <param name="indexLogger">Logs an index on an expression as an extra index, as the scaffolder leaves them out,
    /// so stage 2 doesn't find them</param>
    /// <param name="schema">The table's schema in the database</param>
    /// <param name="table"></param>
    /// <param name="efIndexNames">The names of the indexes EF Core declares on the table</param>
    internal void ReportSettingsEfCoreCantSet(CompareLogger2 tableLogger, CompareLogger2 indexLogger,
        string schema, string table, ISet<string> efIndexNames)
    {
        foreach (var dbIndex in _indexesByTable[(schema, table)])
        {
            //EF Core can't create an index on an expression. If it declares one with the same name, that one is reported
            //as not in the database, as the scaffolder doesn't read the database's
            if (dbIndex.IsExpression && dbIndex.ConstraintType == null && !efIndexNames.Contains(dbIndex.IndexName))
                indexLogger.ExtraInDatabase(dbIndex.IndexName, CompareAttributes.IndexConstraintName);

            //EF Core can set these on an index, but not on a primary key or UNIQUE constraint
            if (dbIndex.ConstraintType is "p" or "u")
            {
                if (dbIndex.IncludeColumns != null)
                    tableLogger.ExtraInDatabase($"{dbIndex.IndexName} INCLUDE ({dbIndex.IncludeColumns})", CompareAttributes.IncludeColumns);
                if (dbIndex.NullsNotDistinct)
                    tableLogger.ExtraInDatabase($"{dbIndex.IndexName} {NullsNotDistinct}", CompareAttributes.NullsDistinct);
            }
        }

        foreach (var setting in _settingsByTable[(schema, table)])
            tableLogger.ExtraInDatabase(setting.Value, Enum.Parse<CompareAttributes>(setting.Setting));
    }
}
