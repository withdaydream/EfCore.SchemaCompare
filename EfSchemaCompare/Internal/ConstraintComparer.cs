using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaCompare.Internal.Postgres;

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
        if (attributes == CompareAttributes.CheckConstraint)
        {
            CompareCheckConstraints(
                dbConstraints.Cast<IConstraintReader.CheckConstraint>().ToList(),
                modelConstraints.Cast<IConstraintReader.CheckConstraint>().ToList(),
                attributes);
            return;
        }

        if (attributes == CompareAttributes.ForeignKey)
        {
            CompareForeignKeyConstraints(
                dbConstraints.Cast<IConstraintReader.ForeignKeyConstraint>().ToList(),
                modelConstraints.Cast<IConstraintReader.ForeignKeyConstraint>().ToList(),
                attributes);
            return;
        }

        var extraInDbList = dbConstraints.Except(modelConstraints).ToList();
        if (extraInDbList.Any())
            foreach (IConstraintReader.IConstraint c in extraInDbList)
                _logger.ExtraInDatabase(c.GetCompareText(), attributes);

        var missingInDbList = modelConstraints.Except(dbConstraints).ToList();
        if (missingInDbList.Any())
            foreach (IConstraintReader.IConstraint c in missingInDbList)
                _logger.NotInDatabase(c.GetCompareText(), attributes);
    }

    private void CompareCheckConstraints(IReadOnlyList<IConstraintReader.CheckConstraint> dbConstraints,
        IReadOnlyList<IConstraintReader.CheckConstraint> modelConstraints, CompareAttributes attributes)
    {
        var dbByKey = dbConstraints.ToDictionary(c => new ConstraintKey(c.TableName, c.ConstraintName));
        var modelByKey = modelConstraints.ToDictionary(c => new ConstraintKey(c.TableName, c.ConstraintName));

        foreach (var dbConstraint in dbConstraints.Where(c => !modelByKey.ContainsKey(new ConstraintKey(c.TableName, c.ConstraintName))))
            _logger.ExtraInDatabase(dbConstraint.GetCompareText(), attributes);

        foreach (var modelConstraint in modelConstraints.Where(c => !dbByKey.ContainsKey(new ConstraintKey(c.TableName, c.ConstraintName))))
            _logger.NotInDatabase(modelConstraint.GetCompareText(), attributes);

        foreach (var modelConstraint in modelConstraints)
        {
            var key = new ConstraintKey(modelConstraint.TableName, modelConstraint.ConstraintName);
            if (!dbByKey.TryGetValue(key, out var dbConstraint))
                continue;

            var modelParse = PostgresCheckExpressionParser.TryParse(modelConstraint.CheckClause);
            var dbParse = PostgresCheckExpressionParser.TryParse(dbConstraint.CheckClause);
            if (modelParse.Success && dbParse.Success)
            {
                if (!PostgresCheckExpressionStructuralComparer.AreEquivalent(modelParse.Expression!, dbParse.Expression!))
                {
                    _logger.CheckDifferent(modelConstraint.GetCompareText(), dbConstraint.GetCompareText(),
                        attributes, StringComparison.Ordinal);
                }

                continue;
            }

            if (!string.Equals(modelConstraint.CheckClause, dbConstraint.CheckClause, StringComparison.Ordinal))
                _logger.CheckDifferent(modelConstraint.GetCompareText(), dbConstraint.GetCompareText(),
                    attributes, StringComparison.Ordinal);
        }
    }

    private void CompareForeignKeyConstraints(IReadOnlyList<IConstraintReader.ForeignKeyConstraint> dbConstraints,
        IReadOnlyList<IConstraintReader.ForeignKeyConstraint> modelConstraints, CompareAttributes attributes)
    {
        var dbByKey = dbConstraints.ToDictionary(c => new ConstraintKey(c.TableName, c.ConstraintName));
        var modelByKey = modelConstraints.ToDictionary(c => new ConstraintKey(c.TableName, c.ConstraintName));

        foreach (var dbConstraint in dbConstraints.Where(c => !modelByKey.ContainsKey(new ConstraintKey(c.TableName, c.ConstraintName))))
            _logger.ExtraInDatabase(dbConstraint.GetCompareText(), attributes);

        foreach (var modelConstraint in modelConstraints.Where(c => !dbByKey.ContainsKey(new ConstraintKey(c.TableName, c.ConstraintName))))
            _logger.NotInDatabase(modelConstraint.GetCompareText(), attributes);

        foreach (var modelConstraint in modelConstraints)
        {
            var key = new ConstraintKey(modelConstraint.TableName, modelConstraint.ConstraintName);
            if (!dbByKey.TryGetValue(key, out var dbConstraint))
                continue;

            _logger.CheckDifferent(modelConstraint.GetCompareText(), dbConstraint.GetCompareText(),
                attributes, StringComparison.Ordinal);
        }
    }

    private readonly record struct ConstraintKey(string TableName, string ConstraintName);
}