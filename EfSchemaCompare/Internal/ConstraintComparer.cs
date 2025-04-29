using System;
using System.Collections.Generic;
using System.Linq;

namespace EfSchemaCompare.Internal;

internal class ConstraintComparer
{
    private readonly CompareLogger2 _logger;

    internal ConstraintComparer(CompareLogger2 logger)
    {
        _logger = logger;
    }

    internal void Compare(IReadOnlyList<IConstraintReader.IConstraint> dbConstraints, IReadOnlyList<IConstraintReader.IConstraint> modelConstraints, CompareAttributes attributes)
    {
        var keyList = dbConstraints.Select(c => c.ConstraintName)
            .Intersect(modelConstraints.Select(c => c.ConstraintName))
            .ToList();
        var dbList = dbConstraints.ToDictionary(k => k.ConstraintName);
        var modelList = modelConstraints.ToDictionary(k => k.ConstraintName);

        foreach (var key in keyList)
            _logger.CheckDifferent(dbList[key].GetCompareText(), modelList[key].GetCompareText(), attributes, StringComparison.InvariantCulture, key);

        var extraInDbList = dbConstraints.Except(modelConstraints).Where(c => !keyList.Contains(c.ConstraintName))
            .ToList();
        if (extraInDbList.Any())
            foreach (IConstraintReader.IConstraint c in extraInDbList)
                _logger.ExtraInDatabase(c.GetCompareText(), attributes);

        var missingInDbList = modelConstraints.Except(dbConstraints).Where(c => !keyList.Contains(c.ConstraintName)).ToList();
        if (missingInDbList.Any())
            foreach (IConstraintReader.IConstraint c in missingInDbList)
                _logger.NotInDatabase(c.GetCompareText(), attributes);
    }
}