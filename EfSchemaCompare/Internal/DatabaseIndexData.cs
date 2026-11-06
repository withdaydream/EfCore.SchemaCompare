// Copyright (c) 2020 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;

namespace EfSchemaCompare.Internal
{
    internal class DatabaseIndexData
    {
        /// <summary>
        ///     The ordered list of columns that make up the index.
        /// </summary>
        public IList<DatabaseColumn> Columns;

        /// <summary>
        ///     Indicates whether or not the index constrains uniqueness.
        /// </summary>
        public bool IsUnique;

        /// <summary>The index name.</summary>
        public string Name;

        /// <summary>
        ///     The collation of each column, see <see cref="FormatCollations"/>. Null if none are set
        /// </summary>
        public string Collations;

        public DatabaseTable Table;

        public DatabaseIndexData(DatabaseIndex index)
        {
            Table = index.Table;
            Name = index.Name;
            Columns = index.Columns;
            IsUnique = index.IsUnique;
            Collations = FormatCollations(index[RelationalAnnotationNames.Collation]);
        }

        /// <summary>
        ///     Formats the Relational:Collation annotation of an index, which holds a collation per column, e.g. "C,default",
        ///     where "default" is a column without one. Returns null if there isn't one
        /// </summary>
        public static string FormatCollations(object collationAnnotation)
        {
            return collationAnnotation switch
            {
                string[] collations => string.Join(",", collations.Select(x => x ?? "default")),
                string collation => collation,
                _ => null
            };
        }

        /// <summary>
        ///     A unique constraint isn't included, as it isn't an index. EF Core creates one for an alternate key,
        ///     and a unique index for HasIndex(...).IsUnique(), so they are compared with alternate keys
        /// </summary>
        public static IEnumerable<DatabaseIndexData> GetIndexes(DatabaseTable table)
        {
            return table.Indexes.Select(x => new DatabaseIndexData(x)).ToList();
        }
    }
}