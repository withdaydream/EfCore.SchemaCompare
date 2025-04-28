using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

namespace EfSchemaCompare.Internal;

public interface IDatabaseColumnFormatter
{
    string GetColumnType(DatabaseColumn column);
}