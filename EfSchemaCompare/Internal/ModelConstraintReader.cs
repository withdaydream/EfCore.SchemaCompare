using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfSchemaCompare.Internal;

internal class ModelConstraintReader : IConstraintReader
{
    public IReadOnlyList<IConstraintReader.CheckConstraint> GetCheckConstraints(DbContext dbContext)
    {
        return dbContext.GetService<IDesignTimeModel>().Model.GetCheckConstraints();
    }

    public IReadOnlyList<IConstraintReader.ForeignKeyConstraint> GetForeignKeyConstraints(DbContext dbContext)
    {
        return dbContext.GetService<IDesignTimeModel>().Model.GetForeignKeyConstraints();
    }

    public IReadOnlyList<IConstraintReader.FunctionDefinition> GetFunctions(DbContext dbContext)
    {
        return dbContext.GetService<IDesignTimeModel>().Model.GetFunctions();
    }

    public IReadOnlyList<IConstraintReader.ExternalFunctionName> GetExternalFunctionNames(DbContext dbContext)
    {
        //GetFunctions already returns every declared function, so nothing else is needed for them to match
        return new List<IConstraintReader.ExternalFunctionName>();
    }

    public IReadOnlyList<IConstraintReader.TriggerDefinition> GetTriggers(DbContext dbContext)
    {
        return dbContext.GetService<IDesignTimeModel>().Model.GetTriggers();
    }
}
