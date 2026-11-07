// Copyright (c) 2020 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EfSchemaCompare.Internal.Postgres;
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
        private readonly bool _isPostgres;

        private string _defaultSchema;
        private Dictionary<string, DatabaseTable> _tableViewDict;
        private StorageParameterComparer _storageParameterComparer;
        private TableSettingsComparer _tableSettingsComparer;
        private bool _hasErrors;

        private readonly List<CompareLog> _logs;
        private readonly IConstraintReader _constraintReader;
        public IReadOnlyList<CompareLog> Logs => _logs.ToImmutableList();

        /// <summary>
        /// The top-level log for this DbContext, set by <see cref="CompareModelToDatabase"/>
        /// </summary>
        public CompareLog DbContextLog { get; private set; }

        public Stage1Comparer(DbContext context, CompareEfSqlConfig config = null, List<CompareLog> logs = null)
        {
            _dbContext = context;
            _designTimeModel = context.GetService<IDesignTimeModel>().Model;
            _model = context.Model;
            _dbContextName = context.GetType().Name;
            _relationalTypeMapping = context.GetService<IRelationalTypeMappingSource>();
            _logs = logs ?? new List<CompareLog>();
            _ignoreList = config?.LogsToIgnore ?? new List<CompareLog>();
            //Names and values are compared as exact text, so e.g. two ways of writing the same accented letter don't match
            _caseComparer = StringComparer.Ordinal;
            _caseComparison = _caseComparer.GetStringComparison();

            _isPostgres = IsPostgres(context);
            _constraintReader = CreateConstraintReader(context);
        }

        internal static IConstraintReader CreateConstraintReader(DbContext context)
        {
            return IsPostgres(context)
                ? new PostgresConstraintReader()
                : new ModelConstraintReader();
        }

        private static bool IsPostgres(DbContext context)
        {
            return string.Equals(context.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);
        }

        public bool CompareModelToDatabase(DatabaseModel databaseModel)
        {
            _defaultSchema = databaseModel.DefaultSchema;
            var dbLogger = new CompareLogger2(CompareType.DbContext, _dbContextName, _logs, _ignoreList, () => _hasErrors = true);

            //Check things about the database, such as sequences
            DbContextLog = dbLogger.MarkAsOk(_dbContextName);
            CheckDatabaseOk(DbContextLog, _model, databaseModel);
            //The scaffolder only provides the database's collation if it isn't the server's default
            dbLogger.CheckDifferent(_designTimeModel.GetCollation(), databaseModel.Collation, CompareAttributes.Collation, _caseComparison);

            _tableViewDict = databaseModel.Tables.ToDictionary(x => x.FormSchemaTableFromDatabase(_defaultSchema), _caseComparer);
            _storageParameterComparer = new StorageParameterComparer(_constraintReader.GetStorageParameters(_dbContext));
            _tableSettingsComparer = new TableSettingsComparer(_constraintReader.GetIndexDetails(_dbContext),
                _constraintReader.GetSettingsEfCoreCantSet(_dbContext));
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
                    if (entityType.GetTableName() != null)
                        CompareAlternateKeys(log, entityType, databaseTable);
                }
                else
                {
                    logger.NotInDatabase(entityType.FormSchemaTableFromModel(), CompareAttributes.TableName);
                }
            }

            CompareTables();

            var dbCheckConstraints = _constraintReader.GetCheckConstraints(_dbContext);
            var modelCheckConstraints = _designTimeModel.GetCheckConstraints();

            var dbForeignKeyConstraints = _constraintReader.GetForeignKeyConstraints(_dbContext);
            var modelForeignKeys = _designTimeModel.GetForeignKeyConstraints();

            var constraintComparer = new ConstraintComparer(dbLogger);
            constraintComparer.Compare(dbCheckConstraints, modelCheckConstraints, CompareAttributes.CheckConstraint);
            constraintComparer.Compare(dbForeignKeyConstraints, modelForeignKeys, CompareAttributes.ForeignKey);

            return _hasErrors;
        }

        //Not implemented
        private void CheckDatabaseOk(CompareLog log, IModel modelRel, DatabaseModel databaseModel)
        {
            //Check sequences
            //var logger = new CompareLogger2(CompareType.Sequence, <sequence name>, _logs);
        }

        //This compares the table's comment and storage parameters, and reports its settings that EF Core can't set.
        //It is done per table, not per entity, as with TPH and table splitting many entities map to one table
        private void CompareTables()
        {
            //Comments and storage parameters are design-time annotations, so they aren't in the runtime model
            foreach (var table in _designTimeModel.GetRelationalModel().Tables)
            {
                var schemaTable = table.Schema.FormSchemaTable(table.Name);
                if (!_tableViewDict.TryGetValue(schemaTable, out var databaseTable))
                    continue; //A missing table is reported by the entity that maps to it
                var logger = new CompareLogger2(CompareType.Table, schemaTable, DbContextLog.SubLogs, _ignoreList, () => _hasErrors = true);
                logger.CheckDifferent(table.Comment, databaseTable.Comment, CompareAttributes.Comment, _caseComparison);
                _storageParameterComparer.CompareTable(logger, databaseTable.Schema, databaseTable.Name,
                    table.EntityTypeMappings.SelectMany(x => x.TypeBase.GetAnnotations()));
                //An extra index is logged like the ones stage 2 finds, i.e. under the table's name
                var indexLogger = new CompareLogger2(CompareType.Index, schemaTable, DbContextLog.SubLogs, _ignoreList, () => _hasErrors = true);
                _tableSettingsComparer.ReportSettingsEfCoreCantSet(logger, indexLogger, databaseTable.Schema, databaseTable.Name,
                    table.Indexes.Select(x => x.Name).ToHashSet(StringComparer.Ordinal));
            }
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
                var constraintName = GetForeignKeyConstraintName(entityFKey, table);
                var logger = new CompareLogger2(CompareType.ForeignKey, constraintName, log.SubLogs, _ignoreList, () => _hasErrors = true);
                if (IgnoreForeignKeyIfInSameTableOrTpT(entityType, entityFKey, table) 
                    || constraintName == null) //constraintName is null if the entity isn't linked to a table (some views are like that)
                    continue;
                if (fKeyDict.ContainsKey(constraintName))
                {
                    //Now check every foreign key
                    var error = false;
                    var databaseFKey = fKeyDict[constraintName];
                    var thisKeyCols = databaseFKey.Columns.ToDictionary(x => x.Name, _caseComparer);
                    foreach (var fKeyProp in entityFKeyProps)
                    {
                        var columnName = GetColumnNameTakingIntoAccountSchema( fKeyProp, table);
                        if (!thisKeyCols.ContainsKey(columnName))
                        {
                            logger.NotInDatabase(columnName);
                            error = true;
                        }
                    }
                    //A missing column is reported above, so these find the columns in a different order, or extra columns.
                    //The principal key's columns are found in the database's principal table, so they are only compared if EF Core maps it
                    if (!error)
                    {
                        error |= logger.CheckDifferent(
                            string.Join(",", entityFKeyProps.Select(x => GetColumnNameTakingIntoAccountSchema(x, table))),
                            string.Join(",", databaseFKey.Columns.Select(x => x.Name)), CompareAttributes.Columns, _caseComparison);
                        var principalColumns = entityFKey.PrincipalKey.Properties
                            .Select(x => GetColumnNameTakingIntoAccountSchema(x, databaseFKey.PrincipalTable)).ToList();
                        if (principalColumns.All(x => x != null))
                            error |= logger.CheckDifferent(string.Join(",", principalColumns),
                                string.Join(",", databaseFKey.PrincipalColumns.Select(x => x.Name)), CompareAttributes.PrincipalColumns, _caseComparison);
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
            var indexDict = DatabaseIndexData.GetIndexes(table).ToDictionary(x => x.Name, _caseComparer);
            foreach (var entityIdx in entityType.ContainingEntityType.GetIndexes())
            {
                var entityIdxprops = entityIdx.Properties;
                var allColumnNames = string.Join(",", entityIdxprops
                    .Select(x => GetColumnNameTakingIntoAccountSchema(x, table)));
                var logger = new CompareLogger2(CompareType.Index, allColumnNames, log.SubLogs, _ignoreList, () => _hasErrors = true);
                var indexName = GetIndexDatabaseName(entityIdx, table);
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
                    //A missing column is reported above, so this finds the columns in a different order, or extra columns
                    if (!error)
                        error |= logger.CheckDifferent(allColumnNames,
                            string.Join(",", indexDict[indexName].Columns.Select(x => x.Name)), CompareAttributes.Columns, _caseComparison);
                    error |= logger.CheckDifferent(entityIdx.IsUnique.ToString(),
                        indexDict[indexName].IsUnique.ToString(), CompareAttributes.Unique, _caseComparison);
                    var designTimeIndex = FindDesignTimeIndex(entityIdx);
                    error |= logger.CheckDifferent(DatabaseIndexData.FormatCollations(designTimeIndex?[RelationalAnnotationNames.Collation]),
                        indexDict[indexName].Collations, CompareAttributes.Collation, _caseComparison);
                    //A storage parameter, INCLUDE or NULLS DISTINCT difference doesn't stop the index being marked OK,
                    //as otherwise stage 2 would say it isn't used
                    _storageParameterComparer.CompareIndex(logger, table.Schema, table.Name, indexName,
                        designTimeIndex?.GetAnnotations() ?? []);
                    _tableSettingsComparer.CompareIndex(logger, table.Schema, table.Name, indexName,
                        GetIncludeColumns(entityIdx, designTimeIndex, table), entityIdx.IsUnique,
                        designTimeIndex?[TableSettingsComparer.NullsDistinctAnnotation] as bool?);
                    if (!error)
                        logger.MarkAsOk(indexName);
                }
                else
                {
                    logger.NotInDatabase(indexName, CompareAttributes.IndexConstraintName);
                }
            }
        }

        //EF Core creates a UNIQUE constraint for an alternate key, e.g. HasAlternateKey, but a unique index for HasIndex(...).IsUnique()
        private void CompareAlternateKeys(CompareLog log, ITypeBase entityType, DatabaseTable table)
        {
            var constraintDict = table.UniqueConstraints.Where(x => !string.IsNullOrEmpty(x.Name))
                .ToDictionary(x => x.Name, _caseComparer);
            //Some providers' scaffolders, e.g. Sqlite's, don't read a unique constraint's name, so a key can't be found by name
            var hasUnnamedConstraints = table.UniqueConstraints.Any(x => string.IsNullOrEmpty(x.Name));
            //Keys are declared on the root entity type, so this doesn't compare them again for each derived type
            foreach (var key in entityType.ContainingEntityType.GetDeclaredKeys().Where(x => !x.IsPrimaryKey()))
            {
                var constraintName = key.GetName(GetTableStoreObjectIdentifier(table));
                if (constraintName == null)
                    continue; //The key isn't mapped to this table
                var logger = new CompareLogger2(CompareType.AlternateKey, constraintName, log.SubLogs, _ignoreList, () => _hasErrors = true);
                if (!constraintDict.TryGetValue(constraintName, out var constraint))
                {
                    if (hasUnnamedConstraints)
                        new CompareLogger2(CompareType.AlternateKey, constraintName, _logs.Last().SubLogs, _ignoreList, () => _hasErrors = true)
                            .MarkAsNotChecked(null, constraintName, CompareAttributes.ConstraintName);
                    else
                        logger.NotInDatabase(constraintName, CompareAttributes.ConstraintName);
                    continue;
                }
                var error = logger.CheckDifferent(
                    string.Join(",", key.Properties.Select(x => GetColumnNameTakingIntoAccountSchema(x, table))),
                    string.Join(",", constraint.Columns.Select(x => x.Name)), CompareAttributes.Columns, _caseComparison);
                if (!error)
                    logger.MarkAsOk(constraintName);
            }
        }

        //IncludeProperties holds the names of the properties, so they are turned into the names of their columns
        private List<string> GetIncludeColumns(IIndex index, IIndex designTimeIndex, DatabaseTable table)
        {
            return (designTimeIndex?[TableSettingsComparer.IncludeAnnotation] as IEnumerable<string>)?
                .Select(name => index.DeclaringEntityType.FindProperty(name) is { } property
                    ? GetColumnNameTakingIntoAccountSchema(property, table) ?? name
                    : name)
                .ToList();
        }

        //Collations and storage parameters are design-time annotations, so they aren't on the runtime model's index
        private IIndex FindDesignTimeIndex(IIndex index)
        {
            return _designTimeModel.FindEntityType(index.DeclaringEntityType.Name)?.GetDeclaredIndexes()
                .FirstOrDefault(x => x.Name == index.Name
                                     && x.Properties.Select(p => p.Name).SequenceEqual(index.Properties.Select(p => p.Name)));
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
            //An entity mapped to JSON is held in one column of its owner's table, named by ToJson, which is compared like any other column
            foreach (var navigation in typeBase.ContainingEntityType.GetNavigations()
                         .Where(x => x.TargetEntityType.IsMappedToJson()))
            {
                var jsonColumnName = navigation.TargetEntityType.GetContainerColumnName();
                var colLogger = new CompareLogger2(CompareType.Property, navigation.Name, log.SubLogs, _ignoreList, () => _hasErrors = true);
                if (jsonColumnName == null || !columnDict.TryGetValue(jsonColumnName, out var jsonColumn))
                {
                    colLogger.NotInDatabase(jsonColumnName, CompareAttributes.ColumnName);
                    continue;
                }
                var jsonRelColumn = GetRelationalColumn(jsonColumnName, table, isView);
                var jsonError = CheckColumnTypeDifferent(colLogger, jsonRelColumn.StoreType, jsonColumn.StoreType);
                jsonError |= colLogger.CheckDifferent(jsonRelColumn.IsNullable.NullableAsString(),
                    jsonColumn.IsNullable.NullableAsString(), CompareAttributes.Nullability, _caseComparison);
                if (!jsonError)
                    colLogger.MarkAsOk(jsonColumnName);
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

            //A missing key column is reported above, so this finds the key's columns in a different order, or extra columns
            var efPrimaryKey = typeBase.ContainingEntityType.FindPrimaryKey();
            if (!isView && efPrimaryKey != null && table.PrimaryKey != null)
            {
                var efKeyColumns = efPrimaryKey.Properties.Select(x => GetColumnNameTakingIntoAccountSchema(x, table)).ToList();
                if (efKeyColumns.All(x => x != null && primaryKeyDict.ContainsKey(x)))
                    pKeyLogger.CheckDifferent(string.Join(",", efKeyColumns),
                        string.Join(",", table.PrimaryKey.Columns.Select(x => x.Name)), CompareAttributes.Columns, _caseComparison);
            }

            if (!pKeyError)
                pKeyLogger.MarkAsOk(efPKeyConstraintName);
        }

        private bool ComparePropertyToColumn(IColumnBase relColumn, CompareLogger2 logger, 
            IProperty property, DatabaseColumn column, bool isView, bool isOwned)
        {
            var error = CheckColumnTypeDifferent(logger, property.GetColumnType(), column.StoreType);
            error |= logger.CheckDifferent(relColumn.IsNullable.NullableAsString(),
                column.IsNullable.NullableAsString(), CompareAttributes.Nullability, _caseComparison);
            error |= CheckSqlDifferent(logger, property.GetComputedColumnSql(), column.ComputedColumnSql, CompareAttributes.ComputedColumnSql);
            if (property.GetComputedColumnSql() != null)
                error |= logger.CheckDifferent(property.GetIsStored()?.ToString() ?? false.ToString()
                    , column.IsStored.ToString(),
                    CompareAttributes.PersistentComputedColumn, _caseComparison);
            var defaultValue = property.TryGetDefaultValue(out var propDefaultValue)
                ? _relationalTypeMapping.FindMapping(propDefaultValue.GetType())
                   .GenerateSqlLiteral(propDefaultValue)
                : property.GetDefaultValueSql();
            error |= CheckSqlDifferent(logger, defaultValue, column.DefaultValueSql, CompareAttributes.DefaultValueSql);
            if (!isView)
            {
                error |= CheckValueGenerated(logger, property, column, isOwned);
                error |= CompareColumnSettings(logger, property, column);
            }
            return error;
        }

        private static readonly Regex TypeModifiers = new(@"\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        //A database stores a type's size, precision and scale as numbers, not text, so the spaces between them don't matter,
        //e.g. EF Core's decimal(9,2) and decimal(9, 2) from SQL Server's scaffolder. The rest of the type is compared exactly
        private bool CheckColumnTypeDifferent(CompareLogger2 logger, string expected, string found)
        {
            if (string.Equals(RemoveSpacesInTypeModifiers(expected), RemoveSpacesInTypeModifiers(found), StringComparison.Ordinal))
                return false;

            return logger.CheckDifferent(expected, found, CompareAttributes.ColumnType, _caseComparison);
        }

        private static string RemoveSpacesInTypeModifiers(string storeType)
        {
            return storeType == null
                ? null
                : TypeModifiers.Replace(storeType, x => $"({Spaces.Replace(x.Groups[1].Value, "")})");
        }

        //Databases rewrite a default's or computed column's SQL when they store it, e.g. adding brackets, so outer brackets
        //are left out. PostgreSQL also adds spaces and casts, so there the two are compared as parsed expressions.
        //Anything else, including SQL that can't be parsed, is compared as exact text
        private bool CheckSqlDifferent(CompareLogger2 logger, string expected, string found, CompareAttributes attribute)
        {
            if (_isPostgres && PostgresCheckExpressionStructuralComparer.AreEquivalent(expected, found) == true)
                return false;

            return logger.CheckDifferent(expected.RemoveUnnecessaryBrackets(), found.RemoveUnnecessaryBrackets(),
                attribute, _caseComparison);
        }

        private const string NpgsqlCompressionAnnotation = "Npgsql:Compression:";
        private const string NpgsqlValueGenerationStrategyAnnotation = "Npgsql:ValueGenerationStrategy";

        //The collation, comment, compression method and identity of a table's column
        private bool CompareColumnSettings(CompareLogger2 logger, IProperty property, DatabaseColumn column)
        {
            var designTimeColumn = FindDesignTimeColumn(column.Name, column.Table);
            if (designTimeColumn == null)
                return false;

            var error = logger.CheckDifferent(designTimeColumn.Collation, column.Collation, CompareAttributes.Collation, _caseComparison);
            error |= logger.CheckDifferent(designTimeColumn.Comment, column.Comment, CompareAttributes.Comment, _caseComparison);
            error |= logger.CheckDifferent(designTimeColumn[NpgsqlCompressionAnnotation] as string, column[NpgsqlCompressionAnnotation] as string,
                CompareAttributes.Compression, StringComparison.OrdinalIgnoreCase);

            var efStrategy = GetColumnValueGenerationStrategy(designTimeColumn[NpgsqlValueGenerationStrategyAnnotation]);
            var dbStrategy = GetColumnValueGenerationStrategy(column[NpgsqlValueGenerationStrategyAnnotation]);
            error |= logger.CheckDifferent(efStrategy, dbStrategy, CompareAttributes.ValueGenerationStrategy, StringComparison.Ordinal);
            if (efStrategy == dbStrategy && efStrategy.StartsWith("Identity", StringComparison.Ordinal))
            {
                var efOptions = PostgresIdentityOptions.Parse(designTimeColumn[PostgresIdentityOptions.AnnotationName] as string, property.GetColumnType());
                var dbOptions = PostgresIdentityOptions.Parse(column[PostgresIdentityOptions.AnnotationName] as string, column.StoreType);
                foreach (var (efOption, dbOption) in efOptions.GetOptionTexts().Zip(dbOptions.GetOptionTexts()))
                    error |= logger.CheckDifferent(efOption, dbOption, CompareAttributes.IdentityOptions, StringComparison.Ordinal);
            }
            return error;
        }

        //Only the strategies that are part of the column's definition. Others, e.g. SequenceHiLo, don't change the column
        private static string GetColumnValueGenerationStrategy(object strategy)
        {
            var name = strategy?.ToString();
            return name is "IdentityAlwaysColumn" or "IdentityByDefaultColumn" or "SerialColumn" ? name : "None";
        }

        //Collations, comments, compression methods and identities are design-time annotations, so they aren't in the runtime model.
        //The relational model's column holds what EF Core would create, e.g. a TPT derived table's key column isn't an identity
        private IColumn FindDesignTimeColumn(string columnName, DatabaseTable table)
        {
            return _designTimeModel.GetRelationalModel().Tables
                .SingleOrDefault(x => x.Schema.FormSchemaTable(x.Name) == table.FormSchemaTableFromDatabase(_defaultSchema))
                ?.FindColumn(columnName);
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

        private string GetForeignKeyConstraintName(IForeignKey foreignKey, DatabaseTable table)
        {
            var principalTableName = foreignKey.PrincipalEntityType.FormSchemaTableFromModel();
            if (principalTableName == null || !_tableViewDict.TryGetValue(principalTableName, out var principalTable))
                return foreignKey.GetConstraintName();

            return foreignKey.GetConstraintName(
                GetTableStoreObjectIdentifier(table),
                GetTableStoreObjectIdentifier(principalTable));
        }

        private string GetIndexDatabaseName(IIndex index, DatabaseTable table)
        {
            return index.GetDatabaseName(GetTableStoreObjectIdentifier(table));
        }

        private StoreObjectIdentifier GetTableStoreObjectIdentifier(DatabaseTable table)
        {
            var modelSchema = table.Schema == _defaultSchema ? null : table.Schema;
            return StoreObjectIdentifier.Table(table.Name, modelSchema);
        }

    }
}