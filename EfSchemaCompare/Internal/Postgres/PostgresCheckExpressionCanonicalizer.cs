using System;
using System.Collections.Generic;
using System.Linq;

namespace EfSchemaCompare.Internal.Postgres;

internal static class PostgresCheckExpressionCanonicalizer
{
    public static PostgresCheckExpression Canonicalize(PostgresCheckExpression expression)
    {
        return expression switch
        {
            PostgresIdentifierExpression identifier => new PostgresIdentifierExpression(NormalizeName(identifier.Name)),
            PostgresLiteralExpression literal => CanonicalizeLiteral(literal),
            PostgresUnaryExpression unary => new PostgresUnaryExpression(
                NormalizeName(unary.Operator),
                Canonicalize(unary.Operand)),
            PostgresBinaryExpression binary => CanonicalizeBinary(binary),
            PostgresIsNullExpression isNull => new PostgresIsNullExpression(Canonicalize(isNull.Operand), isNull.Negated),
            PostgresInExpression inExpression => new PostgresInExpression(
                Canonicalize(inExpression.Operand),
                CanonicalizeList(inExpression.Values),
                inExpression.Negated),
            PostgresFunctionExpression function => new PostgresFunctionExpression(
                NormalizeName(function.Name),
                CanonicalizeList(function.Arguments)),
            PostgresArrayExpression array => new PostgresArrayExpression(CanonicalizeList(array.Items)),
            PostgresCastExpression cast => CanonicalizeCast(cast),
            PostgresCaseExpression caseExpression => new PostgresCaseExpression(
                caseExpression.Whens
                    .Select(w => new PostgresCaseWhen(Canonicalize(w.Condition), Canonicalize(w.Result)))
                    .ToList(),
                caseExpression.ElseResult == null ? null : Canonicalize(caseExpression.ElseResult)),
            _ => expression
        };
    }

    private static PostgresCheckExpression CanonicalizeBinary(PostgresBinaryExpression binary)
    {
        var left = Canonicalize(binary.Left);
        var right = Canonicalize(binary.Right);
        var op = binary.Operator == "!=" ? "<>" : NormalizeName(binary.Operator);

        if (op == "=" && TryGetSingleArrayArgument(right, "any", out var anyItems))
            return new PostgresInExpression(left, anyItems, false);

        if ((op == "<>" || op == "!=") && TryGetSingleArrayArgument(right, "all", out var allItems))
            return new PostgresInExpression(left, allItems, true);

        return new PostgresBinaryExpression(op, left, right);
    }

    private static PostgresCheckExpression CanonicalizeCast(PostgresCastExpression cast)
    {
        var operand = Canonicalize(cast.Operand);
        var typeName = NormalizeTypeName(cast.TypeName);

        if (IsRepresentationCast(typeName) || (cast.IsArray && operand is PostgresArrayExpression))
            return operand;

        return new PostgresCastExpression(operand, typeName, cast.IsArray);
    }

    private static bool TryGetSingleArrayArgument(PostgresCheckExpression expression, string functionName,
        out IReadOnlyList<PostgresCheckExpression> items)
    {
        items = Array.Empty<PostgresCheckExpression>();
        if (expression is not PostgresFunctionExpression function
            || !string.Equals(function.Name, functionName, StringComparison.OrdinalIgnoreCase)
            || function.Arguments.Count != 1)
        {
            return false;
        }

        var argument = StripArrayCast(function.Arguments[0]);
        if (argument is not PostgresArrayExpression array)
            return false;

        items = array.Items;
        return true;
    }

    private static PostgresCheckExpression StripArrayCast(PostgresCheckExpression expression)
    {
        return expression is PostgresCastExpression { IsArray: true } cast
            ? cast.Operand
            : expression;
    }

    private static PostgresLiteralExpression CanonicalizeLiteral(PostgresLiteralExpression literal)
    {
        return literal.Kind switch
        {
            PostgresLiteralKind.Boolean => new PostgresLiteralExpression(
                PostgresLiteralKind.Boolean,
                NormalizeName(literal.Value ?? string.Empty)),
            _ => literal
        };
    }

    private static IReadOnlyList<PostgresCheckExpression> CanonicalizeList(IReadOnlyList<PostgresCheckExpression> expressions)
    {
        return expressions.Select(Canonicalize).ToList();
    }

    private static bool IsRepresentationCast(string typeName)
    {
        return typeName is "text" or "character varying" or "uuid";
    }

    private static string NormalizeName(string value)
    {
        return value.ToLowerInvariant();
    }

    //PostgreSQL stores a type under its full name, e.g. int as integer
    private static string NormalizeTypeName(string typeName)
    {
        return PostgresTypeNameNormalizer.Normalize(typeName);
    }
}

internal static class PostgresCheckExpressionStructuralComparer
{
    /// <summary>
    /// Returns whether EF Core's SQL and the database's are the same expression once the rewrites PostgreSQL makes when it
    /// stores one, e.g. adding brackets, spaces and casts, are undone. Returns null if either can't be parsed.
    /// </summary>
    public static bool? AreEquivalent(string modelSql, string databaseSql)
    {
        var modelParse = PostgresCheckExpressionParser.TryParse(modelSql);
        var databaseParse = PostgresCheckExpressionParser.TryParse(databaseSql);
        if (!modelParse.Success || !databaseParse.Success)
            return null;

        return AreEquivalent(modelParse.Expression!, databaseParse.Expression!);
    }

    /// <summary>
    /// The order matters: the database's expression may have casts EF Core's doesn't, but not the other way round
    /// </summary>
    public static bool AreEquivalent(PostgresCheckExpression model, PostgresCheckExpression database)
    {
        return EqualsCore(
            PostgresCheckExpressionCanonicalizer.Canonicalize(model),
            PostgresCheckExpressionCanonicalizer.Canonicalize(database));
    }

    //Every comparison below passes EF Core's expression first and the database's second
    private static bool EqualsCore(PostgresCheckExpression left, PostgresCheckExpression right)
    {
        if (EqualsSameShape(left, right))
            return true;

        //PostgreSQL adds a cast where it converts a value implicitly, e.g. price * quantity is stored as
        //price * (quantity)::numeric when price is numeric and quantity is an integer
        return right is PostgresCastExpression cast && EqualsCore(left, cast.Operand);
    }

    private static bool EqualsSameShape(PostgresCheckExpression left, PostgresCheckExpression right)
    {
        if (left.GetType() != right.GetType())
            return false;

        return left switch
        {
            PostgresIdentifierExpression l => l.Name == ((PostgresIdentifierExpression)right).Name,
            PostgresLiteralExpression l => LiteralEquals(l, (PostgresLiteralExpression)right),
            PostgresUnaryExpression l => l.Operator == ((PostgresUnaryExpression)right).Operator
                                         && EqualsCore(l.Operand, ((PostgresUnaryExpression)right).Operand),
            PostgresBinaryExpression l => BinaryEquals(l, (PostgresBinaryExpression)right),
            PostgresIsNullExpression l => l.Negated == ((PostgresIsNullExpression)right).Negated
                                          && EqualsCore(l.Operand, ((PostgresIsNullExpression)right).Operand),
            PostgresInExpression l => InEquals(l, (PostgresInExpression)right),
            PostgresFunctionExpression l => FunctionEquals(l, (PostgresFunctionExpression)right),
            PostgresArrayExpression l => SequenceEquals(l.Items, ((PostgresArrayExpression)right).Items),
            PostgresCastExpression l => CastEquals(l, (PostgresCastExpression)right),
            PostgresCaseExpression l => CaseEquals(l, (PostgresCaseExpression)right),
            _ => false
        };
    }

    private static bool LiteralEquals(PostgresLiteralExpression left, PostgresLiteralExpression right)
    {
        return left.Kind == right.Kind && string.Equals(left.Value, right.Value, StringComparison.Ordinal);
    }

    private static bool BinaryEquals(PostgresBinaryExpression left, PostgresBinaryExpression right)
    {
        return left.Operator == right.Operator
               && EqualsCore(left.Left, right.Left)
               && EqualsCore(left.Right, right.Right);
    }

    private static bool InEquals(PostgresInExpression left, PostgresInExpression right)
    {
        return left.Negated == right.Negated
               && EqualsCore(left.Operand, right.Operand)
               && SequenceEquals(left.Values, right.Values);
    }

    private static bool FunctionEquals(PostgresFunctionExpression left, PostgresFunctionExpression right)
    {
        return left.Name == right.Name && SequenceEquals(left.Arguments, right.Arguments);
    }

    private static bool CastEquals(PostgresCastExpression left, PostgresCastExpression right)
    {
        return left.TypeName == right.TypeName
               && left.IsArray == right.IsArray
               && EqualsCore(left.Operand, right.Operand);
    }

    private static bool CaseEquals(PostgresCaseExpression left, PostgresCaseExpression right)
    {
        return SequenceEquals(left.Whens, right.Whens, CaseWhenEquals)
               && ((left.ElseResult == null && right.ElseResult == null)
                   || (left.ElseResult != null && right.ElseResult != null && EqualsCore(left.ElseResult, right.ElseResult)));
    }

    private static bool CaseWhenEquals(PostgresCaseWhen left, PostgresCaseWhen right)
    {
        return EqualsCore(left.Condition, right.Condition) && EqualsCore(left.Result, right.Result);
    }

    private static bool SequenceEquals(IReadOnlyList<PostgresCheckExpression> left, IReadOnlyList<PostgresCheckExpression> right)
    {
        return SequenceEquals(left, right, EqualsCore);
    }

    private static bool SequenceEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, T, bool> comparer)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!comparer(left[i], right[i]))
                return false;
        }

        return true;
    }
}
