using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace EfSchemaCompare.Internal;

public interface IConstraintReader
{
    IReadOnlyList<CheckConstraint> GetCheckConstraints(DbContext dbContext);

    IReadOnlyList<ForeignKeyConstraint> GetForeignKeyConstraints(DbContext dbContext);

    IReadOnlyList<FunctionDefinition> GetFunctions(DbContext dbContext);

    /// <summary>
    /// The names a declared function can resolve to that GetFunctions leaves out, i.e. built into the database or from an extension
    /// </summary>
    IReadOnlyList<ExternalFunctionName> GetExternalFunctionNames(DbContext dbContext);

    IReadOnlyList<TriggerDefinition> GetTriggers(DbContext dbContext);

    /// <summary>
    /// The storage parameters, e.g. fillfactor, of every table and index in the database.
    /// A table or index without any is returned once, with a null ParameterName
    /// </summary>
    IReadOnlyList<StorageParameter> GetStorageParameters(DbContext dbContext);

    /// <summary>
    /// Every index on every table in the database, including the ones that back a primary key or UNIQUE constraint,
    /// and ones on expressions, which the scaffolder leaves out
    /// </summary>
    IReadOnlyList<IndexDetail> GetIndexDetails(DbContext dbContext);

    /// <summary>
    /// The settings of every table in the database, and of its constraints, that EF Core can't set, e.g. a row-level
    /// security policy. EF Core never creates these, so each one is extra in the database
    /// </summary>
    IReadOnlyList<SettingEfCoreCantSet> GetSettingsEfCoreCantSet(DbContext dbContext);

    public interface IConstraint
    {
        [Column("constraint_name")]
        string ConstraintName { get; }

        string GetCompareText();
    }

    public record CheckConstraint : IConstraint
    {
        [Column("table_name")]
        public required string TableName { get; init; }

        [Column("constraint_name")]
        public required string ConstraintName { get; init; }

        [Column("check_clause")]
        public required string CheckClause { get; init; }

        public string GetCompareText()
        {
            return $"{TableName} {ConstraintName} {CheckClause}";
        }
    }

    public record ForeignKeyConstraint : IConstraint
    {
        [Column("table_name")]
        public required string TableName { get; init; }

        [Column("constraint_name")]
        public required string ConstraintName { get; init; }

        [Column("column_name")]
        public required string ColumnName { get; init; }

        [Column("foreign_table_name")]
        public required string ForeignTableName { get; init; }

        [Column("foreign_column_name")]
        public required string ForeignColumnName { get; init; }

        [Column("on_update")]
        public required string OnUpdate { get; init; }

        [Column("on_delete")]
        public required string OnDelete { get; init; }

        public string GetCompareText()
        {
            return $"{ConstraintName} Table({TableName}) Columns({ColumnName}) ForeignTable({ForeignTableName}) ForeignColumns({ForeignColumnName}) OnUpdate({OnUpdate}) OnDelete({OnDelete})";
        }
    }

    public record FunctionDefinition
    {
        /// <summary>
        /// Null means the default schema
        /// </summary>
        [Column("schema_name")]
        public string SchemaName { get; init; }

        [Column("function_name")]
        public required string FunctionName { get; init; }

        /// <summary>
        /// Input argument types, comma separated with no spaces, using the names Postgres's format_type() returns.
        /// Postgres allows overloads, so the argument types are part of the identity.
        /// </summary>
        [Column("argument_types")]
        public required string ArgumentTypes { get; init; }

        /// <summary>
        /// Human-readable text shown in the log. Not part of the comparison
        /// </summary>
        [Column("description")]
        public required string Description { get; init; }

        public string GetIdentity(string defaultSchema)
        {
            return $"{SchemaName ?? defaultSchema}.{FunctionName}({ArgumentTypes})";
        }
    }

    public record ExternalFunctionName
    {
        [Column("schema_name")]
        public required string SchemaName { get; init; }

        [Column("name")]
        public required string Name { get; init; }
    }

    public record TriggerDefinition
    {
        /// <summary>
        /// Null means the default schema
        /// </summary>
        [Column("schema_name")]
        public string SchemaName { get; init; }

        [Column("table_name")]
        public required string TableName { get; init; }

        [Column("trigger_name")]
        public required string TriggerName { get; init; }

        /// <summary>
        /// Human-readable text shown in the log. Not part of the comparison
        /// </summary>
        [Column("description")]
        public required string Description { get; init; }

        public string GetIdentity(string defaultSchema)
        {
            return $"{SchemaName ?? defaultSchema}.{TableName}.{TriggerName}";
        }
    }

    public record StorageParameter
    {
        [Column("schema_name")]
        public required string SchemaName { get; init; }

        [Column("table_name")]
        public required string TableName { get; init; }

        /// <summary>
        /// Null for the table's own storage parameters
        /// </summary>
        [Column("index_name")]
        public string IndexName { get; init; }

        /// <summary>
        /// Null if the table or index has no storage parameters. The parameters of a table's TOAST table start with "toast."
        /// </summary>
        [Column("parameter_name")]
        public string ParameterName { get; init; }

        [Column("parameter_value")]
        public string ParameterValue { get; init; }
    }

    public record IndexDetail
    {
        [Column("schema_name")]
        public required string SchemaName { get; init; }

        [Column("table_name")]
        public required string TableName { get; init; }

        [Column("index_name")]
        public required string IndexName { get; init; }

        /// <summary>
        /// "p" if the index backs a primary key, "u" a UNIQUE constraint, "x" an exclusion constraint, otherwise null
        /// </summary>
        [Column("constraint_type")]
        public string ConstraintType { get; init; }

        /// <summary>
        /// True if any of the index's columns is an expression, e.g. lower(name)
        /// </summary>
        [Column("is_expression")]
        public required bool IsExpression { get; init; }

        /// <summary>
        /// True for a unique index created with NULLS NOT DISTINCT, so two nulls are duplicates
        /// </summary>
        [Column("nulls_not_distinct")]
        public required bool NullsNotDistinct { get; init; }

        /// <summary>
        /// The columns of the index's INCLUDE clause in order, e.g. "a,b". Null if it doesn't have one
        /// </summary>
        [Column("include_columns")]
        public string IncludeColumns { get; init; }
    }

    public record SettingEfCoreCantSet
    {
        [Column("schema_name")]
        public required string SchemaName { get; init; }

        [Column("table_name")]
        public required string TableName { get; init; }

        /// <summary>
        /// The name of the <see cref="CompareAttributes"/> it is reported with, e.g. "Policy"
        /// </summary>
        [Column("setting")]
        public required string Setting { get; init; }

        /// <summary>
        /// What is set, e.g. the policy's name, or "fk_order_customer DEFERRABLE"
        /// </summary>
        [Column("value")]
        public required string Value { get; init; }
    }
}