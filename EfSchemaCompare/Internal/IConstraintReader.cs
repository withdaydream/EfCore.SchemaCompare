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
}