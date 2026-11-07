// Copyright (c) 2020 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

namespace EfSchemaCompare.Internal
{
    internal class Stage2Comparer
    {
        private readonly StringComparer _caseComparer;
        private readonly DatabaseModel _databaseModel;
        private readonly IReadOnlyList<CompareLog> _ignoreList;

        private readonly List<CompareLog> _logs = new List<CompareLog>();
        private readonly List<CompareLog> _warnings = new List<CompareLog>();

        private bool _hasErrors;

        public Stage2Comparer(DatabaseModel databaseModel, CompareEfSqlConfig config = null)
        {
            _databaseModel = databaseModel;
            _ignoreList = config?.LogsToIgnore ?? new List<CompareLog>();
            //Names are compared as exact text, like stage 1 does
            _caseComparer = StringComparer.Ordinal;
        }

        public IReadOnlyList<CompareLog> Logs => _logs.ToImmutableList();

        /// <summary>
        /// Things found that aren't errors, i.e. they don't make <see cref="CompareLogsToDatabase"/> return true
        /// </summary>
        public IReadOnlyList<CompareLog> Warnings => _warnings.ToImmutableList();

        public bool CompareLogsToDatabase(IReadOnlyList<CompareLog> firstStageLogs)
        {
            var logger = new CompareLogger2(CompareType.Database, _databaseModel.DatabaseName, _logs, _ignoreList, () => _hasErrors = true);
            logger.MarkAsOk(null);
            LookForUnusedTables(firstStageLogs);
            LookForUnusedColumns(firstStageLogs, _logs.Last());
            LookForUnusedIndexes(firstStageLogs, _logs.Last());
            LookForUnusedUniqueConstraints(firstStageLogs);

            return _hasErrors;
        }

        //A table or view that no DbContext maps to is a warning, not an error, as a database often has tables EF Core doesn't use
        private void LookForUnusedTables(IReadOnlyList<CompareLog> firstStageLogs)
        {
            var logger = new CompareLogger2(CompareType.Table, null, _warnings, _ignoreList, () => { });
            var databaseTableNames = _databaseModel.Tables.Select(x => x.FormSchemaTableFromDatabase(_databaseModel.DefaultSchema));
            var allEntityTableNames = firstStageLogs.SelectMany(p => p.SubLogs)
                .Where(x => x.State == CompareState.Ok && x.Type == CompareType.Entity)
                .Select(p => p.Expected).OrderBy(p => p).Distinct().ToList();
            //Sorted, as the scaffolder doesn't return the tables in a fixed order
            var tablesNotUsed = databaseTableNames.Where(p => !allEntityTableNames.Contains(p, _caseComparer))
                .OrderBy(p => p, StringComparer.Ordinal);

            foreach (var tableName in tablesNotUsed)
            {
                logger.ExtraInDatabase(null, CompareAttributes.NotSet, tableName);
            }
        }

        private void LookForUnusedColumns(IReadOnlyList<CompareLog> firstStageLogs, CompareLog log)
        {
            var logger = new CompareLogger2(CompareType.Column, null, _logs, _ignoreList, () => _hasErrors = true);
            var tableDict = _databaseModel.Tables.ToDictionary(x => x.FormSchemaTableFromDatabase(_databaseModel.DefaultSchema), _caseComparer);
            //because of table splitting and TPH we need to group properties by table name to correctly say what columns are missed
            var entityColsGrouped = firstStageLogs.SelectMany(p => p.SubLogs)
                .Where(x => x.State == CompareState.Ok && x.Type == CompareType.Entity)
                .GroupBy(x => x.Expected, y => y.SubLogs
                    .Where(x => x.State == CompareState.Ok && x.Type == CompareType.Property)
                    .Select(p => p.Expected));
            var entityColsByTableDict = entityColsGrouped.ToDictionary(x => x.Key, 
                y => y.SelectMany(x => x.ToList()), _caseComparer);

            foreach (var entityLog in firstStageLogs.SelectMany(p => p.SubLogs)
                .Where(x => x.State == CompareState.Ok && x.Type == CompareType.Entity))
            {
                if (tableDict.ContainsKey(entityLog.Expected))
                {
                    var dbColNames = tableDict[entityLog.Expected].Columns.Select(x => x.Name);
                    var colsNotUsed = dbColNames.Where(p => !entityColsByTableDict[entityLog.Expected].Contains(p, _caseComparer));
                    foreach (var colName in colsNotUsed)
                    {
                        logger.ExtraInDatabase(colName, CompareAttributes.ColumnName, entityLog.Expected);
                    }
                }               
            }
        }

        private void LookForUnusedIndexes(IReadOnlyList<CompareLog> firstStageLogs, CompareLog log)
        {
            var logger = new CompareLogger2(CompareType.Index, null, _logs, _ignoreList, () => _hasErrors = true);
            var tableDict = _databaseModel.Tables.ToDictionary(x => x.FormSchemaTableFromDatabase(_databaseModel.DefaultSchema), _caseComparer);
            foreach (var entityLog in firstStageLogs.SelectMany(p => p.SubLogs)
                .Where(x => x.State == CompareState.Ok && x.Type == CompareType.Entity))
            {
                if (tableDict.ContainsKey(entityLog.Expected))
                {
                    var indexCol = tableDict[entityLog.Expected].Indexes.Select(x => x.Name);
                    var allEfIndexNames = entityLog.SubLogs
                        .Where(x => x.State == CompareState.Ok && x.Type == CompareType.Index)
                        .Select(p => p.Expected).OrderBy(p => p).Distinct().ToList();
                    var indexesNotUsed = indexCol.Where(p => !allEfIndexNames.Contains(p, _caseComparer));
                    foreach (var indexName in indexesNotUsed)
                    {
                        logger.ExtraInDatabase(indexName, CompareAttributes.IndexConstraintName, entityLog.Expected);
                    }
                }
            }
        }

        private void LookForUnusedUniqueConstraints(IReadOnlyList<CompareLog> firstStageLogs)
        {
            var logger = new CompareLogger2(CompareType.AlternateKey, null, _logs, _ignoreList, () => _hasErrors = true);
            var tableDict = _databaseModel.Tables.ToDictionary(x => x.FormSchemaTableFromDatabase(_databaseModel.DefaultSchema), _caseComparer);
            //Grouped by table, as with TPH and table splitting many entities map to one table, and only one of them declares a key
            var alternateKeysByTable = firstStageLogs.SelectMany(p => p.SubLogs)
                .Where(x => x.State == CompareState.Ok && x.Type == CompareType.Entity)
                .GroupBy(x => x.Expected, _caseComparer)
                .Select(g => (TableName: g.Key, Used: g.SelectMany(e => e.SubLogs)
                    .Where(x => x.State == CompareState.Ok && x.Type == CompareType.AlternateKey)
                    .Select(x => x.Expected).ToHashSet(_caseComparer)));

            foreach (var (tableName, used) in alternateKeysByTable)
            {
                if (!tableDict.TryGetValue(tableName, out var table))
                    continue;
                foreach (var constraint in table.UniqueConstraints.Where(x => !string.IsNullOrEmpty(x.Name) && !used.Contains(x.Name)))
                    logger.ExtraInDatabase(constraint.Name, CompareAttributes.ConstraintName, tableName);
            }
        }
    }
}