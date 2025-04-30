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
        var extraInDbList = dbConstraints.Except(modelConstraints).ToList();
        if (extraInDbList.Any())
            foreach (IConstraintReader.IConstraint c in extraInDbList)
                _logger.ExtraInDatabase(c.GetCompareText(), attributes);

        var missingInDbList = modelConstraints.Except(dbConstraints).ToList();
        if (missingInDbList.Any())
            foreach (IConstraintReader.IConstraint c in missingInDbList)
                _logger.NotInDatabase(c.GetCompareText(), attributes);
    }
}