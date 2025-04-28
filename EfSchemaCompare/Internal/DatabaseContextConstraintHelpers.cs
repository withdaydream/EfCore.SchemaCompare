using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfSchemaCompare.Internal;

public static class DatabaseContextConstraintHelpers
{
    public static IReadOnlyList<ICheckConstraintReader.Constraint> GetCheckConstraints(this IModel model)
    {
        return model
            .GetRelationalModel()
            .Tables
            .SelectMany(t => t.CheckConstraints.Select(cc => new ICheckConstraintReader.Constraint
            {
                TableName = t.Name,
                ConstraintName = cc.Name,
                CheckClause = $"(({cc.Sql}))" // Hack to make comparison with db work string to string.
            }))
            .OrderBy(c => c.ConstraintName)
            .ToList();
    }
}