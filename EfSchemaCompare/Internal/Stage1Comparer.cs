﻿// Copyright (c) 2020 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

[assembly: InternalsVisibleTo("Test")]

namespace EfSchemaCompare.Internal
{
    internal class Stage1Comparer
    {
        private const string NoPrimaryKey = "- no primary key -";

        private readonly DbContext _dbContext;
        private readonly IModel _designTimeModel;
        private readonly IModel _model;
        private readonly string _dbContextName;
        private readonly IRelationalTypeMappingSource _relationalTypeMapping;
        private readonly List<IEntityType> _entitiesContainingJsonMappedStrings = new List<IEntityType>();
        private readonly IReadOnlyList<CompareLog> _ignoreList;
        private readonly StringComparer _caseComparer;
        private readonly StringComparison _caseComparison;

        private string _defaultSchema;
        private Dictionary<string, DatabaseTable> _tableViewDict;
        private bool _hasErrors;

        private readonly List<CompareLog> _logs;
        public IReadOnlyList<CompareLog> Logs => _logs.ToImmutableList();

        public Stage1Comparer(DbContext context, CompareEfSqlConfig config = null, List<CompareLog> logs = null)
        {
            _dbContext = context;
            _designTimeModel = context.GetService<IDesignTimeModel>().Model;
            _model = context.Model;
            _dbContextName = context.GetType().Name;
            _relationalTypeMapping = context.GetService<IRelationalTypeMappingSource>();
            _logs = logs ?? new List<CompareLog>();
            _ignoreList = config?.LogsToIgnore ?? new List<CompareLog>();
            _caseComparer = StringComparer.CurrentCulture;          //Turned off CaseComparer as doesn't work with EF Core 5
            _caseComparison = _caseComparer.GetStringComparison();
        }

        public bool CompareModelToDatabase(DatabaseModel databaseModel)
        {
            _defaultSchema = databaseModel.DefaultSchema;
            var dbLogger = new CompareLogger2(CompareType.DbContext, _dbContextName, _logs, _ignoreList, () => _hasErrors = true);

            //Check things about the database, such as sequences
            dbLogger.MarkAsOk(_dbContextName);
            CheckDatabaseOk(_logs.Last(), _model, databaseModel);

            _tableViewDict = databaseModel.Tables.ToDictionary(x => x.FormSchemaTableFromDatabase(_defaultSchema), _caseComparer);
            var entitiesNotMappedToTableOrView = _model.GetEntityTypes().
                Where(x => x.FormSchemaTableFromModel() == null).ToList();
            if (entitiesNotMappedToTableOrView.Any())
                dbLogger.MarkAsNotChecked(null, string.
                    Join(", ", entitiesNotMappedToTableOrView.Select(x => x.ClrType.Name)),
                    CompareAttributes.NotMappedToDatabase);

            #region JsonMapping
            //Json Mapping Start----------------------------------------------------------------------------
            //Get a list of entities that are using Json Mapping. 
            foreach (var entityType in _model.GetEntityTypes())
            {
                foreach (var navigation in entityType.ContainingEntityType.GetNavigations()
                             .Where(x => x.TargetEntityType.IsMappedToJson()))
                {
                    //remove the Json Mapped entity 
                    entitiesNotMappedToTableOrView.Add(navigation.TargetEntityType);
                    //Remember the entities that has a string to hold the Json Mapped data
                    _entitiesContainingJsonMappedStrings.Add(navigation.DeclaringEntityType);
                }
            }
            // Json Mapping End----------------------------------------------------------------------------
            #endregion JsonMapping

            foreach (var entityType in _model.GetEntityTypes().Where(x => !entitiesNotMappedToTableOrView.Contains(x)))
            {
                var logger = new CompareLogger2(CompareType.Entity, entityType.ClrType.Name, _logs.Last().SubLogs, _ignoreList, () => _hasErrors = true);
                if (_tableViewDict.ContainsKey(entityType.FormSchemaTableFromModel()))
                {
                    var databaseTable = _tableViewDict[entityType.FormSchemaTableFromModel()];
                    //Checks for table matching
                    var log = logger.MarkAsOk(entityType.FormSchemaTableFromModel());
                    if(entityType.GetTableName() != null)
                    {
                        //It's not a view
                        logger.CheckDifferent(entityType.FindPrimaryKey()?.GetName() ?? NoPrimaryKey,
                            databaseTable.PrimaryKey?.Name ?? NoPrimaryKey,
                            CompareAttributes.ConstraintName, _caseComparison);
                    }
                    CompareColumns(log, entityType, databaseTable);
                    CompareForeignKeys(log, entityType, databaseTable);
                    CompareIndexes(log, entityType, databaseTable);
                }
                else
                {
                    logger.NotInDatabase(entityType.FormSchemaTableFromModel(), CompareAttributes.TableName);
                }
            }

            if (!_designTimeModel.GetEntityTypes().Any())
                return _hasErrors;

            var tableNames = _designTimeModel.GetEntityTypes().Select(x => x.GetSchemaQualifiedTableName()).ToList();

            var dbCheckConstraints = _dbContext.Database.SqlQuery<CheckConstraint>(
                FormattableStringFactory.Create(
                    $$"""
                      SELECT
                          tc.table_name,
                          cc.constraint_name,
                          cc.check_clause
                      FROM 
                          information_schema.table_constraints tc
                      JOIN 
                          information_schema.check_constraints cc 
                          ON tc.constraint_name = cc.constraint_name
                      WHERE 
                          tc.constraint_type = 'CHECK'
                          AND table_name IN ({{String.Join(", ", tableNames.Select((_, i) => $$"""{{{i}}}"""))}})
                          AND (cc.check_clause NOT LIKE '% IS NOT NULL' AND cc.constraint_name NOT LIKE '%_not_null') -- exclude default not null constraints
                      ORDER BY cc.constraint_name
                      """,
                    tableNames.Cast<object>().ToArray()
                )
            ).ToList();

            var designTimeTables = _designTimeModel.GetRelationalModel().Tables.ToList();
            var modelCheckConstraints = designTimeTables
                .SelectMany(t => t.CheckConstraints.Select(cc => new CheckConstraint
                {
                    TableName = t.Name,
                    ConstraintName = cc.Name,
                    CheckClause = $"(({cc.Sql}))"
                }))
                .OrderBy(c => c.ConstraintName)
                .ToList();

            var extraDbConstraints = dbCheckConstraints.Except(modelCheckConstraints).ToList();
            if (extraDbConstraints.Any())
                foreach (CheckConstraint cc in extraDbConstraints)
                    dbLogger.ExtraInDatabase(cc.GetCompareText(), CompareAttributes.CheckConstraint);

            var missingInDb = modelCheckConstraints.Except(dbCheckConstraints).ToList();
            if (missingInDb.Any())
                foreach (CheckConstraint cc in missingInDb)
                    dbLogger.NotInDatabase(cc.GetCompareText(), CompareAttributes.CheckConstraint);

            return _hasErrors;
        }

        //Not implemented 
        private void CheckDatabaseOk(CompareLog log, IModel modelRel, DatabaseModel databaseModel)
        {
            //Check sequences
            //var logger = new CompareLogger2(CompareType.Sequence, <sequence name>, _logs);
        }

        private void CompareForeignKeys(CompareLog log, ITypeBase entityType, DatabaseTable table)
        {
            if (table.ForeignKeys.Any(x => string.IsNullOrEmpty(x.Name)))
            {
                var logger = new CompareLogger2(CompareType.ForeignKey, entityType.ClrType.Name, _logs.Last().SubLogs, _ignoreList, () => _hasErrors = true);
                logger.MarkAsNotChecked(null, entityType.ClrType.Name, CompareAttributes.ConstraintName);
                return;
            }
   
            var fKeyDict = table.ForeignKeys.ToDictionary(x => x.Name, _caseComparer);

            foreach (var entityFKey in entityType.ContainingEntityType.GetForeignKeys())
            {
                var entityFKeyProps = entityFKey.Properties;
                var constraintName = entityFKey.GetConstraintName();
                var logger = new CompareLogger2(CompareType.ForeignKey, constraintName, log.SubLogs, _ignoreList, () => _hasErrors = true);
                if (IgnoreForeignKeyIfInSameTableOrTpT(entityType, entityFKey, table) 
                    || constraintName == null) //constraintName is null if the entity isn't linked to a table (some views are like that)
                    continue;
                if (fKeyDict.ContainsKey(constraintName))
                {
                    //Now check every foreign key
                    var error = false;
                    var thisKeyCols = fKeyDict[constraintName].Columns.ToDictionary(x => x.Name, _caseComparer);
                    foreach (var fKeyProp in entityFKeyProps)
                    {
                        var columnName = GetColumnNameTakingIntoAccountSchema( fKeyProp, table);
                        if (!thisKeyCols.ContainsKey(columnName))
                        {
                            logger.NotInDatabase(columnName);
                            error = true;
                        }
                    }
                    error |= logger.CheckDifferent(entityFKey.DeleteBehavior.ToString(),
                        fKeyDict[constraintName].OnDelete.ConvertReferentialActionToDeleteBehavior(entityFKey.DeleteBehavior),
                            CompareAttributes.DeleteBehavior, _caseComparison);
                    if (!error)
                        logger.MarkAsOk(constraintName);
                }
                else
                {
                    logger.NotInDatabase(constraintName, CompareAttributes.ConstraintName);
                }
            }
        }

        private bool IgnoreForeignKeyIfInSameTableOrTpT(ITypeBase entityType, IForeignKey entityFKey, DatabaseTable table)
        {
            //see https://github.com/aspnet/EntityFrameworkCore/issues/10345#issuecomment-345841191
            var fksPropsInOneTable = entityFKey.Properties.All(x =>
                string.Equals(x.DeclaringType.FormSchemaTableFromModel(), table.FormSchemaTableFromDatabase(_defaultSchema), _caseComparison));
            var fksPropsColumnNames = entityFKey.Properties.Select(p => GetColumnNameTakingIntoAccountSchema(p, table));
            var pkPropsColumnNames = new List<string>();
            foreach (var principalKeyProperty in entityFKey.PrincipalKey.Properties)
            {
                var declaringSchemaTable = principalKeyProperty.DeclaringType.FormSchemaTableFromModel();
                if (!_tableViewDict.ContainsKey(declaringSchemaTable))
                    //There is a missing table problem, but we don't handle it here. returns false which means the calling code will find the problem.
                    return false;
                pkPropsColumnNames.Add(GetColumnNameTakingIntoAccountSchema(principalKeyProperty,
                    _tableViewDict[declaringSchemaTable]));
            }
            
            return fksPropsInOneTable && fksPropsColumnNames.SequenceEqual(pkPropsColumnNames);
        }

        private void CompareIndexes(CompareLog log, ITypeBase entityType, DatabaseTable table)
        {
            var indexDict = DatabaseIndexData.GetIndexesAndUniqueConstraints(table).ToDictionary(x => x.Name, _caseComparer);
            foreach (var entityIdx in entityType.ContainingEntityType.GetIndexes())
            {
                var entityIdxprops = entityIdx.Properties;
                var allColumnNames = string.Join(",", entityIdxprops
                    .Select(x => GetColumnNameTakingIntoAccountSchema(x, table)));
                var logger = new CompareLogger2(CompareType.Index, allColumnNames, log.SubLogs, _ignoreList, () => _hasErrors = true);
                var indexName = entityIdx.GetDatabaseName();
                if (indexName != null && indexDict.ContainsKey(indexName))
                {
                    //Now check every column in an index
                    var error = false;
                    var thisIdxCols = indexDict[indexName].Columns.ToDictionary(x => x.Name, _caseComparer);
                    foreach (var idxProp in entityIdxprops)
                    {
                        var columnName = GetColumnNameTakingIntoAccountSchema(idxProp, table);
                        if (!thisIdxCols.ContainsKey(columnName))
                        {
                            logger.NotInDatabase(columnName);
                            error = true;
                        }
                    }
                    error |= logger.CheckDifferent(entityIdx.IsUnique.ToString(),
                        indexDict[indexName].IsUnique.ToString(), CompareAttributes.Unique, _caseComparison);
                    if (!error)
                        logger.MarkAsOk(indexName);
                }
                else
                {
                    logger.NotInDatabase(indexName, CompareAttributes.IndexConstraintName);
                }
            }
        }

        private void CompareColumns(CompareLog log, ITypeBase typeBase, DatabaseTable table)
        {
            var isView = typeBase.GetTableName() == null;
            var primaryKeyDict = table.PrimaryKey?.Columns.ToDictionary(x => x.Name, _caseComparer)
                                 ?? new Dictionary<string, DatabaseColumn>();
            var efPKeyConstraintName = isView ? NoPrimaryKey :  typeBase.ContainingEntityType.FindPrimaryKey()?.GetName() ?? NoPrimaryKey;
            bool pKeyError = false;
            var pKeyLogger = new CompareLogger2(CompareType.PrimaryKey, efPKeyConstraintName, log.SubLogs, _ignoreList,
                () =>
                {
                    pKeyError = true;  //extra set of pKeyError
                    _hasErrors = true;
                });
            if (!isView)
                pKeyLogger.CheckDifferent(efPKeyConstraintName, table.PrimaryKey?.Name ?? NoPrimaryKey,
                    CompareAttributes.ConstraintName, _caseComparison);

            // Imported from SqlServerAnnotationNames from SqlServer provider
            const string sqlServerTemporalPeriodStartPropertyName = "SqlServer:TemporalPeriodStartPropertyName";
            const string sqlServerTemporalPeriodEndPropertyName = "SqlServer:TemporalPeriodEndPropertyName";
            
            // SQL Server only feature. Will not affect other databases
            var temporalColumnIgnores = table.GetAnnotations()
#pragma warning disable EF1001 // Internal EF Core API usage.
               .Where(a => a.Name == sqlServerTemporalPeriodStartPropertyName ||
                           a.Name == sqlServerTemporalPeriodEndPropertyName)
#pragma warning restore EF1001 // Internal EF Core API usage.
               .Select(a => (string)a.Value)
               .ToArray();

            var columnDict = table.Columns.ToDictionary(x => x.Name, _caseComparer);
            var isOwned = typeBase.ContainingEntityType.IsOwned();

            #region JsonMapping
            //Json Mapping Start----------------------------------------------------------------------------
            //We look for entities that have Json Mapping and find out what properties holds the Json data
            var declaringEntityTypes = typeBase.ContainingEntityType.GetNavigations()
                .Where(x => x.TargetEntityType.IsMappedToJson())
                .Select(x => x.TargetEntityType.ClrType).ToArray();
            if (declaringEntityTypes.Any())
            {
                
                //There are properties that have a string to hold the Json Data
                foreach (var jsonProperty in typeBase.ClrType.GetProperties()
                             .Where(x => declaringEntityTypes.Contains(x.PropertyType)))
                {
                    var colLogger = new CompareLogger2(CompareType.Property, jsonProperty.Name, log.SubLogs, _ignoreList, () => _hasErrors = true);
                    colLogger.MarkAsOk(jsonProperty.Name);
                }
            }
            //Json Mapping end------------------------------------------------------------------------------
            #endregion JsonMapping

            //Now we look at each property in the entity (NOTE: this doesn't contain the Json Mapped properties 
            foreach (var property in typeBase.GetProperties())
            {
                // Ignore temporal shadow properties (SQL Server)
                if (property.IsShadowProperty() && temporalColumnIgnores.Contains(property.Name))
                    continue;

                var colLogger = new CompareLogger2(CompareType.Property, property.Name, log.SubLogs, _ignoreList, () => _hasErrors = true);
                var columnName = GetColumnNameTakingIntoAccountSchema(property, table, isView);
                if (columnName == null)
                {
                    //This catches properties in TPH, split tables, and Owned Types where the properties are not mapped to the current table
                    continue;
                }

                if (columnDict.ContainsKey(columnName))
                {
                    bool error = false;
                    {
                        //The property is stored in the database

                        var reColumn = GetRelationalColumn(columnName, table, isView);
                        error = ComparePropertyToColumn(reColumn, colLogger, property, columnDict[columnName], isView, isOwned);
                        //check for primary key
                        if ((property.FindContainingPrimaryKey() != null) &&
                            //This remove TPH, Owned Types primary key checks
                            !isView != primaryKeyDict.ContainsKey(columnName))
                        {
                            if (!primaryKeyDict.ContainsKey(columnName))
                            {
                                pKeyLogger.NotInDatabase(columnName, CompareAttributes.ColumnName);
                                error = true;
                            }
                            else
                            {
                                pKeyLogger.ExtraInDatabase(columnName, CompareAttributes.ColumnName,
                                    table.PrimaryKey.Name);
                            }
                        }
                    }

                    if (!error)
                    {
                        //There were no errors noted, so we mark it as OK
                        colLogger.MarkAsOk(columnName);
                    }
                }
                else
                {
                    colLogger.NotInDatabase(GetColumnNameTakingIntoAccountSchema(property, table), CompareAttributes.ColumnName);
                }
            }
            if (!pKeyError)
                pKeyLogger.MarkAsOk(efPKeyConstraintName);
        }

        private bool ComparePropertyToColumn(IColumnBase relColumn, CompareLogger2 logger, 
            IProperty property, DatabaseColumn column, bool isView, bool isOwned)
        {
            var error = logger.CheckDifferent(property.GetColumnType(), column.StoreType, CompareAttributes.ColumnType, _caseComparison);
            error |= logger.CheckDifferent(relColumn.IsNullable.NullableAsString(), 
                column.IsNullable.NullableAsString(), CompareAttributes.Nullability, _caseComparison);
            error |= logger.CheckDifferent(property.GetComputedColumnSql().RemoveUnnecessaryBrackets(),
                column.ComputedColumnSql.RemoveUnnecessaryBrackets(), CompareAttributes.ComputedColumnSql, _caseComparison);
            if (property.GetComputedColumnSql() != null)
                error |= logger.CheckDifferent(property.GetIsStored()?.ToString() ?? false.ToString()
                    , column.IsStored.ToString(),
                    CompareAttributes.PersistentComputedColumn, _caseComparison);
            var defaultValue = property.TryGetDefaultValue(out var propDefaultValue)
                ? _relationalTypeMapping.FindMapping(propDefaultValue.GetType())
                   .GenerateSqlLiteral(propDefaultValue)
                : property.GetDefaultValueSql().RemoveUnnecessaryBrackets();
            error |= logger.CheckDifferent(defaultValue,
                    column.DefaultValueSql.RemoveUnnecessaryBrackets(), CompareAttributes.DefaultValueSql, _caseComparison);
            if (!isView)
                error |= CheckValueGenerated(logger, property, column, isOwned);
            return error;
        }

        //this gets the relational column, which has correct IsNullable value (might have other things too)
        //see https://github.com/dotnet/efcore/issues/23758#issuecomment-769994456 
        private IColumnBase GetRelationalColumn(string columnName, DatabaseTable table, bool isView)
        {
            var xx = _model.GetRelationalModel().Views.ToList();
            IEnumerable<IColumnBase> columns;
            if (isView)
                columns = _model.GetRelationalModel().Views.Single(x =>
                        x.Schema.FormSchemaTable(x.Name) == table.FormSchemaTableFromDatabase(_defaultSchema))
                    .Columns;
            else
                columns = _model.GetRelationalModel().Tables.Single(x =>
                        x.Schema.FormSchemaTable(x.Name) == table.FormSchemaTableFromDatabase(_defaultSchema))
                    .Columns;
            return columns.Single(x => x.Name == columnName);
        }

        //thanks to https://stackoverflow.com/questions/1749966/c-sharp-how-to-determine-whether-a-type-is-a-number
        private static readonly HashSet<Type> IntegerTypes =
        [
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long),
            typeof(ulong)
        ];

        private bool CheckValueGenerated(CompareLogger2 logger, IProperty property, DatabaseColumn column, bool isOwned)
        {
            // Leave owned value generated properties to be checked by the owning entity
            if (property.IsPrimaryKey() && isOwned)
                return false;

            // Not strictly owned, but acts like owned - Specialized DB Context
            if (property.IsPrimaryKey() && property.FindFirstPrincipal()?.DeclaringType != property.DeclaringType)
                return false;

            var colValGen = column.ValueGenerated.ConvertNullableValueGenerated(column.ComputedColumnSql, column.DefaultValueSql);
            if (colValGen == ValueGenerated.Never.ToString()
                //There is a case where the property is part of the primary key and the key is not set in the database
                && property.ValueGenerated == ValueGenerated.OnAdd
                && property.IsKey()
                //We assume that a integer of some form should be provided by the database
                && !IntegerTypes.Contains(property.ClrType))
                return false;

            return logger.CheckDifferent(property.ValueGenerated.ToString(),
                colValGen, CompareAttributes.ValueGenerated, _caseComparison);
        }

        private string GetColumnNameTakingIntoAccountSchema(IProperty property, DatabaseTable table,
            bool isView = false)
        {
            var modelSchema = table.Schema == _defaultSchema ? null : table.Schema;
            var columnName = isView
                ? property.GetColumnName(StoreObjectIdentifier.View(table.Name, modelSchema))
                : property.GetColumnName(StoreObjectIdentifier.Table(table.Name, modelSchema));
            return columnName;
        }

    }

    public record CheckConstraint
    {
        [Column("table_name")]
        public string TableName { get; init; }

        [Column("constraint_name")]
        public string ConstraintName { get; init; }

        [Column("check_clause")]
        public string CheckClause { get; init; }

        public string GetCompareText()
        {
            return $"{TableName} {ConstraintName} {CheckClause}";
        }
    }
}