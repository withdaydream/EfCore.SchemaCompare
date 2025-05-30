﻿using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace EfSchemaCompare.Internal;

public interface IConstraintReader
{
    IReadOnlyList<CheckConstraint> GetCheckConstraints(DbContext dbContext);

    IReadOnlyList<ForeignKeyConstraint> GetForeignKeyConstraints(DbContext dbContext);

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
            return $"{ConstraintName} Table({TableName}) Columns({ColumnName}) ForeignTable({ForeignTableName}) ForeignColumns({ForeignColumnName}) OnDelete({OnDelete})";
        }
    }
}