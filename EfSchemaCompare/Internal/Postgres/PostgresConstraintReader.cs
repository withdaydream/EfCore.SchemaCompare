using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace EfSchemaCompare.Internal.Postgres;

public class PostgresConstraintReader : IConstraintReader
{
    public IReadOnlyList<IConstraintReader.CheckConstraint> GetCheckConstraints(DbContext dbContext)
    {
        var tableNames = dbContext.Model.GetEntityTypes().Select(object (x) => x.GetSchemaQualifiedTableName()).ToList();

        return dbContext.Database.SqlQuery<IConstraintReader.CheckConstraint>(
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

    public IReadOnlyList<IConstraintReader.ForeignKeyConstraint> GetForeignKeyConstraints(DbContext dbContext)
    {
        var tableNames = dbContext.Model.GetEntityTypes().Select(object (x) => x.GetSchemaQualifiedTableName()).ToList();

        return dbContext.Database.SqlQuery<IConstraintReader.ForeignKeyConstraint>(
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

    /// <summary>
    /// Returns every function, procedure and aggregate in the database, other than ones built into Postgres
    /// (created by initdb, so their oid is below FirstNormalObjectId = 16384) or owned by an extension.
    /// Each overload is returned separately.
    /// </summary>
    public IReadOnlyList<IConstraintReader.FunctionDefinition> GetFunctions(DbContext dbContext)
    {
        return dbContext.Database.SqlQueryRaw<IConstraintReader.FunctionDefinition>(
            """
            SELECT
                n.nspname AS schema_name,
                p.proname AS function_name,
                COALESCE((
                    SELECT string_agg(format_type(a.type_oid, NULL), ',' ORDER BY a.ord)
                    FROM unnest(p.proargtypes::oid[]) WITH ORDINALITY AS a(type_oid, ord)
                ), '') AS argument_types,
                CASE p.prokind
                    WHEN 'p' THEN 'procedure'
                    WHEN 'a' THEN 'aggregate returning ' || pg_get_function_result(p.oid)
                    WHEN 'w' THEN 'window function returning ' || pg_get_function_result(p.oid)
                    ELSE 'function returning ' || pg_get_function_result(p.oid)
                END AS description
            FROM
                pg_proc AS p
            JOIN
                pg_namespace AS n ON n.oid = p.pronamespace
            WHERE
                p.oid >= 16384
                AND n.nspname !~ '^pg_(toast_)?temp_'
                AND NOT EXISTS (
                    SELECT 1 FROM pg_depend AS d
                    WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e'
                )
            ORDER BY schema_name, function_name, argument_types
            """
        ).ToList();
    }

    /// <summary>
    /// Returns the names of the functions that <see cref="GetFunctions"/> leaves out, i.e. ones built into Postgres or
    /// owned by an extension, plus the names of built-in and extension types, because Postgres treats a call like
    /// uuid('...') or timestamptz(text) as a cast to that type when no function matches.
    /// </summary>
    public IReadOnlyList<IConstraintReader.ExternalFunctionName> GetExternalFunctionNames(DbContext dbContext)
    {
        return dbContext.Database.SqlQueryRaw<IConstraintReader.ExternalFunctionName>(
            """
            SELECT n.nspname AS schema_name, p.proname AS name
            FROM
                pg_proc AS p
            JOIN
                pg_namespace AS n ON n.oid = p.pronamespace
            WHERE
                p.oid < 16384
                OR EXISTS (
                    SELECT 1 FROM pg_depend AS d
                    WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e'
                )
            UNION
            SELECT n.nspname AS schema_name, t.typname AS name
            FROM
                pg_type AS t
            JOIN
                pg_namespace AS n ON n.oid = t.typnamespace
            WHERE
                t.oid < 16384
                OR EXISTS (
                    SELECT 1 FROM pg_depend AS d
                    WHERE d.classid = 'pg_type'::regclass AND d.objid = t.oid AND d.deptype = 'e'
                )
            """
        ).ToList();
    }

    /// <summary>
    /// Returns every trigger on every table in the database, other than ones Postgres creates itself
    /// (e.g. for foreign keys), copies of a partitioned table's trigger on its partitions, and triggers
    /// on tables owned by an extension. Requires Postgres 13 or later (pg_trigger.tgparentid).
    /// </summary>
    public IReadOnlyList<IConstraintReader.TriggerDefinition> GetTriggers(DbContext dbContext)
    {
        return dbContext.Database.SqlQueryRaw<IConstraintReader.TriggerDefinition>(
            """
            SELECT
                n.nspname AS schema_name,
                c.relname AS table_name,
                t.tgname AS trigger_name,
                pg_get_triggerdef(t.oid) AS description
            FROM
                pg_trigger AS t
            JOIN
                pg_class AS c ON c.oid = t.tgrelid
            JOIN
                pg_namespace AS n ON n.oid = c.relnamespace
            WHERE
                NOT t.tgisinternal
                AND t.tgparentid = 0
                AND n.nspname !~ '^pg_(toast_)?temp_'
                AND NOT EXISTS (
                    SELECT 1 FROM pg_depend AS d
                    WHERE d.deptype = 'e'
                      AND ((d.classid = 'pg_class'::regclass AND d.objid = c.oid)
                        OR (d.classid = 'pg_trigger'::regclass AND d.objid = t.oid))
                )
            ORDER BY schema_name, table_name, trigger_name
            """
        ).ToList();
    }
}
