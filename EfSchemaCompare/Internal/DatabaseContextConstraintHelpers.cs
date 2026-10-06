using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaCompare.Internal.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfSchemaCompare.Internal;

public static class DatabaseContextConstraintHelpers
{
    public static IReadOnlyList<IConstraintReader.CheckConstraint> GetCheckConstraints(this IModel model)
    {
        return model
            .GetRelationalModel()
            .Tables
            .SelectMany(t => t.CheckConstraints.Select(cc => new IConstraintReader.CheckConstraint
            {
                TableName = t.Name,
                ConstraintName = cc.Name,
                CheckClause = $"(({cc.Sql}))" // Hack to make comparison with db work string to string.
            }))
            .OrderBy(c => c.ConstraintName)
            .ToList();
    }

    public static IReadOnlyList<IConstraintReader.ForeignKeyConstraint> GetForeignKeyConstraints(this IModel model)
    {
        return model
            .GetRelationalModel()
            .Tables
            .SelectMany(t => t.ForeignKeyConstraints.Select(cc => new IConstraintReader.ForeignKeyConstraint
            {
                TableName = t.Name,
                ConstraintName = cc.Name,
                ForeignTableName = cc.PrincipalTable.Name,
                ColumnName = String.Join(", ", cc.Columns.Select(c => c.Name)),
                ForeignColumnName = String.Join(", ", cc.PrincipalColumns.Select(c => c.Name)),
                OnUpdate = GetActionName(ReferentialAction.NoAction), // No EF support for OnUpdate. Ref: https://stackoverflow.com/a/57434214.
                OnDelete = GetActionName(cc.OnDeleteAction)
            }))
            .OrderBy(c => c.ConstraintName)
            .ToList();
    }

    public static IReadOnlyList<IConstraintReader.FunctionDefinition> GetFunctions(this IModel model)
    {
        return model
            .GetRelationalModel()
            .Functions
            .Where(f => !f.IsBuiltIn)
            .Select(f => new IConstraintReader.FunctionDefinition
            {
                SchemaName = f.Schema,
                FunctionName = f.Name,
                ArgumentTypes = String.Join(",", f.Parameters.Select(p => PostgresTypeNameNormalizer.Normalize(p.StoreType))),
                Description = f.ReturnType == null ? "table-valued function" : $"function returning {f.ReturnType}"
            })
            .ToList();
    }

    public static IReadOnlyList<IConstraintReader.TriggerDefinition> GetTriggers(this IModel model)
    {
        return model
            .GetRelationalModel()
            .Tables
            .SelectMany(t => t.Triggers.Select(tr => new IConstraintReader.TriggerDefinition
            {
                SchemaName = t.Schema,
                TableName = t.Name,
                TriggerName = tr.GetDatabaseName(),
                Description = $"trigger declared on entity {tr.EntityType.DisplayName()}"
            }))
            .ToList();
    }

    private static string GetActionName(ReferentialAction action)
    {
        switch (action)
        {
            case ReferentialAction.Restrict:
                return "RESTRICT";
            case ReferentialAction.Cascade:
                return "CASCADE";
            case ReferentialAction.SetNull:
                return "SET NULL";
            case ReferentialAction.SetDefault:
                return "SET DEFAULT";
            default:
            case ReferentialAction.NoAction:
                return "NO ACTION";
        }
    }
}