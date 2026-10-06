using System;
using System.Collections.Generic;
using System.Linq;

namespace EfSchemaCompare.Internal;

/// <summary>
/// Compares the functions and triggers declared in the DbContexts (HasDbFunction, HasTrigger) with the ones in the database.
/// EF Core only holds their names and signatures, not their SQL, so this checks they exist and that nothing else does.
/// </summary>
internal class FunctionTriggerComparer
{
    private readonly IReadOnlyList<CompareLog> _ignoreList;
    private readonly string _defaultSchema;
    private readonly Action _setErrorHasHappened;

    internal FunctionTriggerComparer(IReadOnlyList<CompareLog> ignoreList, string defaultSchema, Action setErrorHasHappened)
    {
        _ignoreList = ignoreList;
        _defaultSchema = defaultSchema;
        _setErrorHasHappened = setErrorHasHappened;
    }

    internal record ContextDeclarations(
        CompareLog DbContextLog,
        IReadOnlyList<IConstraintReader.FunctionDefinition> Functions,
        IReadOnlyList<IConstraintReader.TriggerDefinition> Triggers);

    /// <summary>
    /// Anything a context declares that isn't in the database is logged under that context.
    /// Anything in the database that no context declares is logged under the first context.
    /// </summary>
    internal void Compare(IReadOnlyList<ContextDeclarations> contexts,
        IReadOnlyList<IConstraintReader.FunctionDefinition> dbFunctions,
        IReadOnlyList<IConstraintReader.TriggerDefinition> dbTriggers)
    {
        CompareObjects(CompareType.Function,
            contexts.Select(c => (c.DbContextLog, c.Functions.Select(f => (f.GetIdentity(_defaultSchema), f.Description)))).ToList(),
            dbFunctions.Select(f => (f.GetIdentity(_defaultSchema), f.Description)).ToList());

        CompareObjects(CompareType.Trigger,
            contexts.Select(c => (c.DbContextLog, c.Triggers.Select(t => (t.GetIdentity(_defaultSchema), t.Description)))).ToList(),
            dbTriggers.Select(t => (t.GetIdentity(_defaultSchema), t.Description)).ToList());
    }

    private void CompareObjects(CompareType type,
        IReadOnlyList<(CompareLog DbContextLog, IEnumerable<(string Identity, string Description)> Declared)> contexts,
        IReadOnlyList<(string Identity, string Description)> inDatabase)
    {
        var dbIdentities = inDatabase.Select(x => x.Identity).ToHashSet(StringComparer.Ordinal);
        var declaredIdentities = contexts.SelectMany(c => c.Declared).Select(x => x.Identity).ToHashSet(StringComparer.Ordinal);

        var extraLogger = CreateLogger(type, contexts[0].DbContextLog);
        foreach (var dbObject in inDatabase.Where(x => !declaredIdentities.Contains(x.Identity))
                     .DistinctBy(x => x.Identity).OrderBy(x => x.Identity, StringComparer.Ordinal))
            extraLogger.ExtraInDatabase(dbObject.Description, CompareAttributes.NotSet, dbObject.Identity);

        foreach (var context in contexts)
        {
            var logger = CreateLogger(type, context.DbContextLog);
            foreach (var declared in context.Declared.Where(x => !dbIdentities.Contains(x.Identity))
                         .DistinctBy(x => x.Identity).OrderBy(x => x.Identity, StringComparer.Ordinal))
                logger.NotInDatabase(declared.Description, CompareAttributes.NotSet, declared.Identity);
        }
    }

    private CompareLogger2 CreateLogger(CompareType type, CompareLog dbContextLog)
    {
        return new CompareLogger2(type, null, dbContextLog.SubLogs, _ignoreList, _setErrorHasHappened);
    }
}
