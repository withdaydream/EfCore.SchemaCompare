using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaCompare.Internal.Postgres;

namespace EfSchemaCompare.Internal;

/// <summary>
/// What EF Core sets on an index that <see cref="TableSettingsComparer"/> compares. Columns are the names of the columns,
/// not the properties, and each list has a value per column of the index, or is null if EF Core doesn't set it
/// </summary>
/// <param name="ColumnCount">The number of columns in the index, not counting its INCLUDE columns</param>
/// <param name="IncludeColumns">The columns of IncludeProperties</param>
/// <param name="IsUnique"></param>
/// <param name="NullsDistinct">AreNullsDistinct</param>
/// <param name="Filter">HasFilter</param>
/// <param name="Method">HasMethod</param>
/// <param name="Descending">IsDescending. Empty means every column is descending</param>
/// <param name="NullSortOrders">HasNullSortOrder, as the names of Npgsql's NullSortOrder, e.g. "NullsFirst"</param>
/// <param name="OperatorClasses">HasOperators</param>
internal record EfIndexSettings(int ColumnCount, IReadOnlyList<string> IncludeColumns, bool IsUnique, bool? NullsDistinct,
    string Filter, string Method, IReadOnlyList<bool> Descending, IReadOnlyList<string> NullSortOrders,
    IReadOnlyList<string> OperatorClasses);

/// <summary>
/// Compares the settings of a table and its indexes that the scaffolder doesn't read, using what the database's
/// <see cref="IConstraintReader"/> returns, so only PostgreSQL's are compared:
/// 1. An index's INCLUDE columns, NULLS [NOT] DISTINCT, filter, method, and each column's sort order, nulls order and
///    operator class, which EF Core sets via IncludeProperties, AreNullsDistinct, HasFilter, HasMethod, IsDescending,
///    HasNullSortOrder and HasOperators.
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

    /// <summary>
    /// The annotation Npgsql's HasMethod adds to an index
    /// </summary>
    internal const string MethodAnnotation = "Npgsql:IndexMethod";

    /// <summary>
    /// The annotation Npgsql's HasNullSortOrder adds to an index, holding a NullSortOrder per column
    /// </summary>
    internal const string NullSortOrderAnnotation = "Npgsql:IndexNullSortOrder";

    /// <summary>
    /// The annotation Npgsql's HasOperators adds to an index, holding an operator class per column
    /// </summary>
    internal const string OperatorsAnnotation = "Npgsql:IndexOperators";

    private const string NullsDistinct = "NULLS DISTINCT";
    private const string NullsNotDistinct = "NULLS NOT DISTINCT";
    private const string NullsFirst = "NULLS FIRST";
    private const string NullsLast = "NULLS LAST";
    private const string DefaultMethod = "btree";
    private const string DefaultOperatorClass = "default";

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
    /// Compares the settings of an index EF Core declares: its INCLUDE columns, NULLS [NOT] DISTINCT for a unique index,
    /// filter, method, and each column's sort order, nulls order and operator class.
    /// Does nothing if the index isn't in the database, as that is reported elsewhere
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="schema">The schema of the index's table in the database</param>
    /// <param name="table"></param>
    /// <param name="index"></param>
    /// <param name="ef">What EF Core sets on the index</param>
    /// <param name="compareEachColumn">False if the index's columns differ from the database's, so each column's sort order,
    /// nulls order and operator class aren't compared, as they would be compared with another column's</param>
    internal void CompareIndex(CompareLogger2 logger, string schema, string table, string index, EfIndexSettings ef,
        bool compareEachColumn)
    {
        if (!_indexes.TryGetValue((schema, table, index), out var dbIndex))
            return;

        //Set on both sides or neither, so an index without INCLUDE columns doesn't match one with them
        var efInclude = ef.IncludeColumns is { Count: > 0 } ? string.Join(",", ef.IncludeColumns) : null;
        logger.CheckDifferent(efInclude, dbIndex.IncludeColumns, CompareAttributes.IncludeColumns, StringComparison.Ordinal);

        //PostgreSQL stores whether nulls are distinct whether or not it was set, so it can't tell NULLS DISTINCT from
        //not setting it. That makes EF Core not setting it its default, NULLS DISTINCT. It only applies to a unique index
        if (ef.IsUnique)
            logger.CheckDifferent(ef.NullsDistinct == false ? NullsNotDistinct : NullsDistinct,
                dbIndex.NullsNotDistinct ? NullsNotDistinct : NullsDistinct, CompareAttributes.NullsDistinct, StringComparison.Ordinal);

        //Set on both sides or neither. PostgreSQL rewrites a partial index's WHERE clause when it stores it, like a check
        //constraint's, so they are compared as expressions. One that can't be parsed is compared as exact text
        var sameFilter = PostgresCheckExpressionStructuralComparer.AreEquivalent(ef.Filter, dbIndex.Filter)
                         ?? string.Equals(ef.Filter, dbIndex.Filter, StringComparison.Ordinal);
        if (!sameFilter)
            logger.CheckDifferent(ef.Filter, dbIndex.Filter, CompareAttributes.Filter, StringComparison.Ordinal);

        //PostgreSQL stores an index's method, and each column's sort order, nulls order and operator class, whether or not
        //they were set, so it can't tell e.g. USING btree from not setting it. That makes EF Core not setting one its default
        logger.CheckDifferent(ef.Method ?? DefaultMethod, dbIndex.Method, CompareAttributes.Method, StringComparison.Ordinal);

        if (!compareEachColumn)
            return;

        var descending = Enumerable.Range(0, ef.ColumnCount)
            .Select(i => ef.Descending != null && (ef.Descending.Count == 0 || (i < ef.Descending.Count && ef.Descending[i])))
            .ToList();
        logger.CheckDifferent(string.Join(",", descending.Select(x => x ? "DESC" : "ASC")), dbIndex.SortOrder,
            CompareAttributes.SortOrder, StringComparison.Ordinal);

        //A column's default nulls order is NULLS LAST if it is ascending, and NULLS FIRST if it is descending
        var nullsOrders = descending.Select((isDescending, i) => GetValue(ef.NullSortOrders, i) switch
        {
            "NullsFirst" => NullsFirst,
            "NullsLast" => NullsLast,
            _ => isDescending ? NullsFirst : NullsLast
        });
        logger.CheckDifferent(string.Join(",", nullsOrders), dbIndex.NullsOrder, CompareAttributes.NullsOrder, StringComparison.Ordinal);

        //A column EF Core doesn't give an operator class matches the database's if that is the default one for its type
        var dbOperatorClasses = dbIndex.OperatorClasses?.Split(',') ?? [];
        var dbIsDefault = dbIndex.DefaultOperatorClasses?.Split(',') ?? [];
        var efOperatorClasses = Enumerable.Range(0, ef.ColumnCount).Select(i => GetValue(ef.OperatorClasses, i)).ToList();
        var foundOperatorClasses = dbOperatorClasses.Select((name, i) =>
            i < efOperatorClasses.Count && efOperatorClasses[i] == null && GetValue(dbIsDefault, i) == "true" ? DefaultOperatorClass : name);
        logger.CheckDifferent(string.Join(",", efOperatorClasses.Select(x => x ?? DefaultOperatorClass)),
            string.Join(",", foundOperatorClasses), CompareAttributes.OperatorClasses, StringComparison.Ordinal);
    }

    //Returns the value for the column at index i, or null if there isn't one, e.g. HasOperators was given fewer values
    private static string GetValue(IReadOnlyList<string> values, int i)
    {
        return values != null && i < values.Count && !string.IsNullOrEmpty(values[i]) ? values[i] : null;
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
