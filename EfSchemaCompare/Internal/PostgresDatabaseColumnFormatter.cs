using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

namespace EfSchemaCompare.Internal;

public class PostgresDatabaseColumnFormatter : IDatabaseColumnFormatter
{
    public string GetColumnType(DatabaseColumn column)
    {
        Annotation compressionAnnotation = column.FindAnnotation("Npgsql:Compression:");
        if (compressionAnnotation != null)
            return $"{column.StoreType} COMPRESSION {compressionAnnotation.Value}";

        return column.StoreType;
    }
}