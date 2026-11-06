// Copyright (c) 2020 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using EfSchemaCompare.Internal;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace EfSchemaCompare
{
    /// <summary>
    /// This class holds the configuration information for the CompareEfSql class
    /// </summary>
    public class CompareEfSqlConfig
    {
        private readonly List<CompareLog> _logsToIgnore = new List<CompareLog>();

        /// <summary>
        /// By default, Stage 2 only runs if: either there were no errors in Stage 1, or if you
        /// have suppressed all the errors in the first stage. Set this to true to always run Stage 2.
        /// </summary>
        public bool AlwaysRunStage2 { get; set; } = false;

        /// <summary>
        /// Every table and view in the database is checked. Ones that no DbContext maps to are reported as warnings,
        /// see <see cref="CompareEfSql.Warnings"/>, not errors. This leaves tables out of the comparison altogether:
        /// 1. null or "" - no tables are left out. This is the default.
        /// 2. A comma-delimited list of tables, with optional schema, to leave out.
        ///    Typical format: "MyTable,MyOtherTable,MySchema.MyTableWithSchema"
        ///    (note: the schema/table match is case-insensitive)
        /// </summary>
        public string TablesToIgnoreCommaDelimited { get; set; }

        /// <summary>
        /// This contains all the log types that should be ignored by the comparer
        /// </summary>
        public IReadOnlyList<CompareLog> LogsToIgnore => _logsToIgnore.ToImmutableList();

        /// <summary>
        /// This allows you to clip a set of errors strings and add them as ignore items
        /// </summary>
        /// <param name="textWithNewlineBetweenErrors"></param>
        public void IgnoreTheseErrors(string textWithNewlineBetweenErrors)
        {
            foreach (var errorString in textWithNewlineBetweenErrors.Split('\n'))
            {
                AddIgnoreCompareLog(FindErrorsToIgnore.DecodeCompareTextToCompareLog(errorString));
            }
        }

        /// <summary>
        /// This allows you to add a log with setting that will be used to decide if a log is ignored.
        /// The Type and State must be set, but any strings set to null with automatically match anything 
        /// and the Attribute has a MatchAnything setting too.
        /// </summary>
        /// <param name="logTypeToIgnore"></param>
        public void AddIgnoreCompareLog(CompareLog logTypeToIgnore)
        {
            if (logTypeToIgnore.State <= CompareState.Ok)
                throw new ArgumentException("You cannot ignore logs with a State of OK (or lower).");
            _logsToIgnore.Add(logTypeToIgnore);
        }
    }
}