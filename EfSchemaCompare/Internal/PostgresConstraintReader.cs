using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace EfSchemaCompare.Internal;

public class PostgresConstraintReader : IConstraintReader
{
    public IReadOnlyList<IConstraintReader.Constraint> GetCheckConstraints(DbContext dbContext)
    {
        var tableNames = dbContext.Model.GetEntityTypes().Select(object (x) => x.GetSchemaQualifiedTableName()).ToList();

        return dbContext.Database.SqlQuery<IConstraintReader.Constraint>(
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

    public IReadOnlyList<IConstraintReader.ForeignKey> GetForeignKeyConstraints(DbContext dbContext)
    {
        var tableNames = dbContext.Model.GetEntityTypes().Select(object (x) => x.GetSchemaQualifiedTableName()).ToList();

        return dbContext.Database.SqlQuery<IConstraintReader.ForeignKey>(
            FormattableStringFactory.Create(
                $$"""
                  SELECT 
                      table_name,
                      constraint_name,
                      column_name,
                      foreign_table_name,
                      foreign_column_name,
                      on_update,
                      on_delete
                  FROM (
                      SELECT
                          conname AS constraint_name,
                          replace(conrelid::regclass::text, '"', '') AS table_name,
                          a.attname AS column_name,
                          replace(confrelid::regclass::text, '"', '') AS foreign_table_name,
                          af.attname AS foreign_column_name,
                          CASE c.confupdtype
                              WHEN 'a' THEN 'NO ACTION'
                              WHEN 'r' THEN 'RESTRICT'
                              WHEN 'c' THEN 'CASCADE'
                              WHEN 'n' THEN 'SET NULL'
                              WHEN 'd' THEN 'SET DEFAULT'
                          END AS on_update,
                          CASE c.confdeltype
                              WHEN 'a' THEN 'NO ACTION'
                              WHEN 'r' THEN 'RESTRICT'
                              WHEN 'c' THEN 'CASCADE'
                              WHEN 'n' THEN 'SET NULL'
                              WHEN 'd' THEN 'SET DEFAULT'
                          END AS on_delete
                      FROM
                          pg_constraint AS c
                      JOIN
                          pg_attribute AS a ON a.attnum = ANY(c.conkey) AND a.attrelid = c.conrelid
                      JOIN
                          pg_attribute AS af ON af.attnum = ANY(c.confkey) AND af.attrelid = c.confrelid
                      WHERE
                         c.contype = 'f'
                  ) AS R
                  WHERE table_name IN ({{String.Join(", ", tableNames.Select((_, i) => $$"""{{{i}}}"""))}});
                  """,
                tableNames.ToArray()
            )
        ).ToList();
    }
}