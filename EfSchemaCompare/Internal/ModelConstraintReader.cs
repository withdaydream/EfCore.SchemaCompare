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

    public IReadOnlyList<IConstraintReader.StorageParameter> GetStorageParameters(DbContext dbContext)
    {
        //Storage parameters are only compared for PostgreSQL. No tables or indexes means nothing is compared
        return new List<IConstraintReader.StorageParameter>();
    }

    public IReadOnlyList<IConstraintReader.IndexDetail> GetIndexDetails(DbContext dbContext)
    {
        //Index details are only compared for PostgreSQL. No indexes means nothing is compared
        return new List<IConstraintReader.IndexDetail>();
    }

    public IReadOnlyList<IConstraintReader.SettingEfCoreCantSet> GetSettingsEfCoreCantSet(DbContext dbContext)
    {
        //These are only looked for in PostgreSQL
        return new List<IConstraintReader.SettingEfCoreCantSet>();
    }
}
