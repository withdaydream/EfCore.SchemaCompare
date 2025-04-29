using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace EfSchemaCompare.Internal;

public class PostgresCheckConstraintReader : ICheckConstraintReader
{
    public IReadOnlyList<ICheckConstraintReader.Constraint> GetCheckConstraints(DbContext dbContext)
    {
        var tableNames = dbContext.Model.GetEntityTypes().Select(object (x) => x.GetSchemaQualifiedTableName()).ToList();

        return dbContext.Database.SqlQuery<ICheckConstraintReader.Constraint>(
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
                tableNames.ToArray()
            )
        ).ToList();
    }
}