using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace EfSchemaCompare.Internal.Postgres;

public class PostgresConstraintReader : IConstraintReader
{
    /// <summary>
    /// Returns the check constraints of the DbContext's tables. Only real check constraints (contype 'c') are read, so
    /// PostgreSQL's NOT NULL constraints aren't included, and each constraint is read with its own table, as two tables
    /// can have check constraints with the same name.
    /// </summary>
    public IReadOnlyList<IConstraintReader.CheckConstraint> GetCheckConstraints(DbContext dbContext)
    {
        var tableNames = dbContext.Model.GetEntityTypes().Select(object (x) => x.GetSchemaQualifiedTableName()).ToList();

        return dbContext.Database.SqlQuery<IConstraintReader.CheckConstraint>(
            FormattableStringFactory.Create(
                $$"""
                  SELECT
                      c.relname AS table_name,
                      con.conname AS constraint_name,
                      -- The same text information_schema.check_constraints gives, i.e. without the leading "CHECK "
                      substring(pg_get_constraintdef(con.oid) from 7) AS check_clause
                  FROM
                      pg_constraint AS con
                  JOIN
                      pg_class AS c ON c.oid = con.conrelid
                  WHERE
                      con.contype = 'c'
                      AND replace(con.conrelid::regclass::text, '"', '') IN ({{String.Join(", ", tableNames.Select((_, i) => $$"""{{{i}}}"""))}})
                  ORDER BY con.conname
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
                      -- One row per foreign key, with its columns in order, joined like the model's, e.g. "a, b"
                      SELECT
                          conname AS constraint_name,
                          replace(conrelid::regclass::text, '"', '') AS table_name,
                          (SELECT string_agg(a.attname::text, ', ' ORDER BY k.ord)
                           FROM unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord)
                           JOIN pg_attribute AS a ON a.attrelid = c.conrelid AND a.attnum = k.attnum) AS column_name,
                          replace(confrelid::regclass::text, '"', '') AS foreign_table_name,
                          (SELECT string_agg(a.attname::text, ', ' ORDER BY k.ord)
                           FROM unnest(c.confkey) WITH ORDINALITY AS k(attnum, ord)
                           JOIN pg_attribute AS a ON a.attrelid = c.confrelid AND a.attnum = k.attnum) AS foreign_column_name,
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

    /// <summary>
    /// Returns the storage parameters (reloptions) of every table and index in the database, other than in the system schemas.
    /// A table's TOAST table parameters, which are set as e.g. toast.autovacuum_enabled, are returned with that "toast." prefix.
    /// </summary>
    public IReadOnlyList<IConstraintReader.StorageParameter> GetStorageParameters(DbContext dbContext)
    {
        return dbContext.Database.SqlQueryRaw<IConstraintReader.StorageParameter>(
            """
            SELECT
                n.nspname AS schema_name,
                c.relname AS table_name,
                NULL AS index_name,
                o.option_name AS parameter_name,
                o.option_value AS parameter_value
            FROM
                pg_class AS c
            JOIN
                pg_namespace AS n ON n.oid = c.relnamespace
            LEFT JOIN
                pg_class AS toast ON toast.oid = c.reltoastrelid
            LEFT JOIN LATERAL (
                SELECT option_name, option_value FROM pg_options_to_table(c.reloptions)
                UNION ALL
                SELECT 'toast.' || option_name, option_value FROM pg_options_to_table(toast.reloptions)
            ) AS o ON true
            WHERE
                c.relkind IN ('r', 'p')
                AND n.nspname !~ '^pg_'
                AND n.nspname <> 'information_schema'
            UNION ALL
            SELECT
                n.nspname,
                t.relname,
                i.relname,
                o.option_name,
                o.option_value
            FROM
                pg_index AS x
            JOIN
                pg_class AS i ON i.oid = x.indexrelid
            JOIN
                pg_class AS t ON t.oid = x.indrelid
            JOIN
                pg_namespace AS n ON n.oid = t.relnamespace
            LEFT JOIN LATERAL
                pg_options_to_table(i.reloptions) AS o ON true
            WHERE
                t.relkind IN ('r', 'p')
                AND n.nspname !~ '^pg_'
                AND n.nspname <> 'information_schema'
            ORDER BY schema_name, table_name, index_name NULLS FIRST, parameter_name
            """
        ).ToList();
    }

    /// <summary>
    /// Returns every index on every table in the database, other than in the system schemas, including the ones that back a
    /// primary key or a UNIQUE or exclusion constraint. An index's INCLUDE columns are the ones after its key columns
    /// (indnkeyatts). NULLS NOT DISTINCT was added in PostgreSQL 15, so before that indnullsnotdistinct doesn't exist and
    /// is read as false. Each key column's sort order and nulls order are bits 1 (DESC) and 2 (NULLS FIRST) of indoption,
    /// and its operator class is in indclass, along with whether it is the default one for the column's type.
    /// </summary>
    public IReadOnlyList<IConstraintReader.IndexDetail> GetIndexDetails(DbContext dbContext)
    {
        return dbContext.Database.SqlQueryRaw<IConstraintReader.IndexDetail>(
            """
            SELECT
                n.nspname AS schema_name,
                t.relname AS table_name,
                i.relname AS index_name,
                con.contype::text AS constraint_type,
                x.indexprs IS NOT NULL AS is_expression,
                COALESCE((to_jsonb(x) ->> 'indnullsnotdistinct')::boolean, false) AS nulls_not_distinct,
                (SELECT string_agg(a.attname::text, ',' ORDER BY k.ord)
                 FROM unnest(x.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
                 JOIN pg_attribute AS a ON a.attrelid = x.indrelid AND a.attnum = k.attnum
                 WHERE k.ord > x.indnkeyatts) AS include_columns,
                pg_get_expr(x.indpred, x.indrelid) AS filter,
                am.amname::text AS method,
                (SELECT string_agg(CASE WHEN (o.flags & 1) = 1 THEN 'DESC' ELSE 'ASC' END, ',' ORDER BY o.ord)
                 FROM unnest(x.indoption::int2[]) WITH ORDINALITY AS o(flags, ord)) AS sort_order,
                (SELECT string_agg(CASE WHEN (o.flags & 2) = 2 THEN 'NULLS FIRST' ELSE 'NULLS LAST' END, ',' ORDER BY o.ord)
                 FROM unnest(x.indoption::int2[]) WITH ORDINALITY AS o(flags, ord)) AS nulls_order,
                (SELECT string_agg(oc.opcname::text, ',' ORDER BY c.ord)
                 FROM unnest(x.indclass::oid[]) WITH ORDINALITY AS c(opclass, ord)
                 JOIN pg_opclass AS oc ON oc.oid = c.opclass) AS operator_classes,
                (SELECT string_agg(oc.opcdefault::text, ',' ORDER BY c.ord)
                 FROM unnest(x.indclass::oid[]) WITH ORDINALITY AS c(opclass, ord)
                 JOIN pg_opclass AS oc ON oc.oid = c.opclass) AS default_operator_classes
            FROM
                pg_index AS x
            JOIN
                pg_class AS i ON i.oid = x.indexrelid
            JOIN
                pg_am AS am ON am.oid = i.relam
            JOIN
                pg_class AS t ON t.oid = x.indrelid
            JOIN
                pg_namespace AS n ON n.oid = t.relnamespace
            LEFT JOIN
                pg_constraint AS con ON con.conindid = x.indexrelid AND con.contype IN ('p', 'u', 'x')
            WHERE
                t.relkind IN ('r', 'p')
                AND n.nspname !~ '^pg_'
                AND n.nspname <> 'information_schema'
            ORDER BY schema_name, table_name, index_name
            """
        ).ToList();
    }

    /// <summary>
    /// Returns the settings that EF Core can't set of every table in the database, other than in the system schemas:
    /// row-level security being enabled or forced, its policies, rules (other than the _RETURN rule a view is made of),
    /// DEFERRABLE primary keys, UNIQUE constraints and foreign keys, and foreign keys that are NOT VALID.
    /// A check constraint's NOT VALID is part of its clause, so it is compared with the check constraint.
    /// </summary>
    public IReadOnlyList<IConstraintReader.SettingEfCoreCantSet> GetSettingsEfCoreCantSet(DbContext dbContext)
    {
        return dbContext.Database.SqlQueryRaw<IConstraintReader.SettingEfCoreCantSet>(
            """
            SELECT
                n.nspname AS schema_name,
                c.relname AS table_name,
                s.setting,
                s.value
            FROM
                pg_class AS c
            JOIN
                pg_namespace AS n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL (
                SELECT 'RowLevelSecurity' AS setting, 'ENABLE ROW LEVEL SECURITY' AS value WHERE c.relrowsecurity
                UNION ALL
                SELECT 'RowLevelSecurity', 'FORCE ROW LEVEL SECURITY' WHERE c.relforcerowsecurity
                UNION ALL
                SELECT 'Policy', p.polname::text FROM pg_policy AS p WHERE p.polrelid = c.oid
                UNION ALL
                SELECT 'Rule', r.rulename::text FROM pg_rewrite AS r WHERE r.ev_class = c.oid AND r.rulename <> '_RETURN'
                UNION ALL
                SELECT 'Deferrable', con.conname || CASE WHEN con.condeferred THEN ' DEFERRABLE INITIALLY DEFERRED' ELSE ' DEFERRABLE' END
                FROM pg_constraint AS con WHERE con.conrelid = c.oid AND con.condeferrable AND con.contype IN ('p', 'u', 'f')
                UNION ALL
                SELECT 'NotValid', con.conname || ' NOT VALID'
                FROM pg_constraint AS con WHERE con.conrelid = c.oid AND NOT con.convalidated AND con.contype = 'f'
            ) AS s
            WHERE
                c.relkind IN ('r', 'p')
                AND n.nspname !~ '^pg_'
                AND n.nspname <> 'information_schema'
            ORDER BY schema_name, table_name, s.setting, s.value
            """
        ).ToList();
    }
}
