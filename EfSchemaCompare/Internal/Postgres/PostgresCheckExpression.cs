using System.Collections.Generic;

namespace EfSchemaCompare.Internal.Postgres;

internal abstract record PostgresCheckExpression;

internal sealed record PostgresIdentifierExpression(string Name) : PostgresCheckExpression;

internal sealed record PostgresLiteralExpression(PostgresLiteralKind Kind, string? Value) : PostgresCheckExpression;

internal sealed record PostgresUnaryExpression(string Operator, PostgresCheckExpression Operand) : PostgresCheckExpression;

internal sealed record PostgresBinaryExpression(string Operator, PostgresCheckExpression Left, PostgresCheckExpression Right) : PostgresCheckExpression;

internal sealed record PostgresIsNullExpression(PostgresCheckExpression Operand, bool Negated) : PostgresCheckExpression;

internal sealed record PostgresInExpression(PostgresCheckExpression Operand, IReadOnlyList<PostgresCheckExpression> Values, bool Negated) : PostgresCheckExpression;

internal sealed record PostgresFunctionExpression(string Name, IReadOnlyList<PostgresCheckExpression> Arguments) : PostgresCheckExpression;

internal sealed record PostgresArrayExpression(IReadOnlyList<PostgresCheckExpression> Items) : PostgresCheckExpression;

internal sealed record PostgresCastExpression(PostgresCheckExpression Operand, string TypeName, bool IsArray) : PostgresCheckExpression;

internal sealed record PostgresCaseExpression(IReadOnlyList<PostgresCaseWhen> Whens, PostgresCheckExpression? ElseResult) : PostgresCheckExpression;

internal sealed record PostgresCaseWhen(PostgresCheckExpression Condition, PostgresCheckExpression Result);

internal enum PostgresLiteralKind
{
    String,
    Number,
    Boolean,
    Null
}

internal readonly record struct PostgresCheckExpressionParseResult(
    bool Success,
    PostgresCheckExpression? Expression,
    string? Error)
{
    public static PostgresCheckExpressionParseResult Parsed(PostgresCheckExpression expression)
    {
        return new PostgresCheckExpressionParseResult(true, expression, null);
    }

    public static PostgresCheckExpressionParseResult Failed(string error)
    {
        return new PostgresCheckExpressionParseResult(false, null, error);
    }
}
