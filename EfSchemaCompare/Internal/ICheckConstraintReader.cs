using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace EfSchemaCompare.Internal;

public interface ICheckConstraintReader
{
    IReadOnlyList<Constraint> GetCheckConstraints(DbContext dbContext);

    public record Constraint
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