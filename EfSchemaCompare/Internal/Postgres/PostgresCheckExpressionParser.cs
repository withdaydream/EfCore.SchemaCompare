using System;
using System.Collections.Generic;
using System.Linq;

namespace EfSchemaCompare.Internal.Postgres;

internal static class PostgresCheckExpressionParser
{
    /// <summary>
    /// Parses the PostgreSQL check-expression subset used by SchemaCompare.
    /// This intentionally is not a complete PostgreSQL parser; unsupported syntax fails closed.
    /// </summary>
    public static PostgresCheckExpressionParseResult TryParse(string sql)
    {
        var tokens = PostgresCheckExpressionTokenizer.Tokenize(sql);
        var invalid = tokens.FirstOrDefault(t => t.Kind == PostgresCheckTokenKind.Invalid);
        if (invalid.Kind == PostgresCheckTokenKind.Invalid)
            return PostgresCheckExpressionParseResult.Failed($"{invalid.Text} at position {invalid.Position}");

        var parser = new Parser(tokens);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly IReadOnlyList<PostgresCheckToken> _tokens;
        private int _position;

        public Parser(IReadOnlyList<PostgresCheckToken> tokens)
        {
            _tokens = tokens;
        }

        public PostgresCheckExpressionParseResult Parse()
        {
            try
            {
                var expression = ParseExpression();
                if (Current.Kind != PostgresCheckTokenKind.End)
                    return Fail($"Unexpected token '{Current.Text}'", Current);

                return PostgresCheckExpressionParseResult.Parsed(expression);
            }
            catch (PostgresCheckParseException ex)
            {
                return PostgresCheckExpressionParseResult.Failed(ex.Message);
            }
        }

        private PostgresCheckExpression ParseExpression()
        {
            return ParseOr();
        }

        private PostgresCheckExpression ParseOr()
        {
            var left = ParseAnd();
            while (MatchKeyword("or"))
                left = new PostgresBinaryExpression("or", left, ParseAnd());

            return left;
        }

        private PostgresCheckExpression ParseAnd()
        {
            var left = ParseNot();
            while (MatchKeyword("and"))
                left = new PostgresBinaryExpression("and", left, ParseNot());

            return left;
        }

        private PostgresCheckExpression ParseNot()
        {
            if (MatchKeyword("not"))
                return new PostgresUnaryExpression("not", ParseNot());

            return ParseComparison();
        }

        private PostgresCheckExpression ParseComparison()
        {
            var left = ParsePostfix();

            if (MatchKeyword("is"))
            {
                var negated = MatchKeyword("not");
                if (MatchKeyword("null"))
                    return new PostgresIsNullExpression(left, negated);
                if (MatchKeyword("true"))
                    return new PostgresBinaryExpression(negated ? "<>" : "=", left,
                        new PostgresLiteralExpression(PostgresLiteralKind.Boolean, true.ToString()));
                if (MatchKeyword("false"))
                    return new PostgresBinaryExpression(negated ? "<>" : "=", left,
                        new PostgresLiteralExpression(PostgresLiteralKind.Boolean, false.ToString()));

                throw Error("Expected NULL, TRUE, or FALSE after IS", Current);
            }

            if (MatchKeyword("not"))
            {
                if (!MatchKeyword("in"))
                    throw Error("Expected IN after NOT in comparison expression", Current);

                return new PostgresInExpression(left, ParseExpressionListInParentheses(), true);
            }

            if (MatchKeyword("in"))
                return new PostgresInExpression(left, ParseExpressionListInParentheses(), false);

            if (Current.Kind == PostgresCheckTokenKind.Operator && IsComparisonOperator(Current.Text))
            {
                var op = NormalizeOperator(Advance().Text);
                return new PostgresBinaryExpression(op, left, ParsePostfix());
            }

            return left;
        }

        private PostgresCheckExpression ParsePostfix()
        {
            var expression = ParsePrimary();
            while (Match(PostgresCheckTokenKind.DoubleColon))
            {
                var (typeName, isArray) = ParseTypeName();
                expression = new PostgresCastExpression(expression, typeName, isArray);
            }

            return expression;
        }

        private PostgresCheckExpression ParsePrimary()
        {
            if (Match(PostgresCheckTokenKind.LeftParen))
            {
                var expression = ParseExpression();
                Consume(PostgresCheckTokenKind.RightParen, "Expected ')' after expression");
                return expression;
            }

            if (MatchKeyword("case"))
                return ParseCase();

            if (MatchKeyword("array"))
                return new PostgresArrayExpression(ParseExpressionListInBrackets());

            if (Current.Kind == PostgresCheckTokenKind.String)
                return new PostgresLiteralExpression(PostgresLiteralKind.String, Advance().Text);

            if (Current.Kind == PostgresCheckTokenKind.Number)
                return new PostgresLiteralExpression(PostgresLiteralKind.Number, Advance().Text);

            if (MatchKeyword("true"))
                return new PostgresLiteralExpression(PostgresLiteralKind.Boolean, true.ToString());

            if (MatchKeyword("false"))
                return new PostgresLiteralExpression(PostgresLiteralKind.Boolean, false.ToString());

            if (MatchKeyword("null"))
                return new PostgresLiteralExpression(PostgresLiteralKind.Null, null);

            if (Current.Kind == PostgresCheckTokenKind.Operator && (Current.Text == "+" || Current.Text == "-"))
            {
                var op = Advance().Text;
                return new PostgresUnaryExpression(op, ParsePostfix());
            }

            if (Current.Kind == PostgresCheckTokenKind.Identifier)
            {
                var name = Advance().Text;
                if (Match(PostgresCheckTokenKind.LeftParen))
                    return new PostgresFunctionExpression(name, ParseExpressionListUntil(PostgresCheckTokenKind.RightParen));

                return new PostgresIdentifierExpression(name);
            }

            throw Error("Expected expression", Current);
        }

        private PostgresCheckExpression ParseCase()
        {
            var whens = new List<PostgresCaseWhen>();
            PostgresCheckExpression? elseResult = null;

            while (MatchKeyword("when"))
            {
                var condition = ParseExpression();
                if (!MatchKeyword("then"))
                    throw Error("Expected THEN in CASE expression", Current);

                whens.Add(new PostgresCaseWhen(condition, ParseExpression()));
            }

            if (MatchKeyword("else"))
                elseResult = ParseExpression();

            if (!MatchKeyword("end"))
                throw Error("Expected END in CASE expression", Current);

            if (whens.Count == 0)
                throw Error("CASE expression must contain at least one WHEN", Current);

            return new PostgresCaseExpression(whens, elseResult);
        }

        private IReadOnlyList<PostgresCheckExpression> ParseExpressionListInParentheses()
        {
            Consume(PostgresCheckTokenKind.LeftParen, "Expected '(' before expression list");
            return ParseExpressionListUntil(PostgresCheckTokenKind.RightParen);
        }

        private IReadOnlyList<PostgresCheckExpression> ParseExpressionListInBrackets()
        {
            Consume(PostgresCheckTokenKind.LeftBracket, "Expected '[' after ARRAY");
            return ParseExpressionListUntil(PostgresCheckTokenKind.RightBracket);
        }

        private IReadOnlyList<PostgresCheckExpression> ParseExpressionListUntil(PostgresCheckTokenKind terminator)
        {
            var expressions = new List<PostgresCheckExpression>();
            if (Match(terminator))
                return expressions;

            do
            {
                expressions.Add(ParseExpression());
            }
            while (Match(PostgresCheckTokenKind.Comma));

            Consume(terminator, $"Expected '{TerminatorText(terminator)}' after expression list");
            return expressions;
        }

        private (string TypeName, bool IsArray) ParseTypeName()
        {
            var parts = new List<string>();
            while (Current.Kind == PostgresCheckTokenKind.Identifier)
            {
                if (IsExpressionKeyword(Current.Text))
                    break;

                parts.Add(Advance().Text);
            }

            if (parts.Count == 0)
                throw Error("Expected type name after cast operator", Current);

            var isArray = false;
            while (Match(PostgresCheckTokenKind.LeftBracket))
            {
                Consume(PostgresCheckTokenKind.RightBracket, "Expected ']' in array type cast");
                isArray = true;
            }

            return (string.Join(" ", parts), isArray);
        }

        private PostgresCheckToken Current => _tokens[_position];

        private PostgresCheckToken Advance()
        {
            if (Current.Kind != PostgresCheckTokenKind.End)
                _position++;

            return _tokens[_position - 1];
        }

        private bool Match(PostgresCheckTokenKind kind)
        {
            if (Current.Kind != kind)
                return false;

            Advance();
            return true;
        }

        private bool MatchKeyword(string keyword)
        {
            if (!Current.IsKeyword(keyword))
                return false;

            Advance();
            return true;
        }

        private void Consume(PostgresCheckTokenKind kind, string message)
        {
            if (!Match(kind))
                throw Error(message, Current);
        }

        private static PostgresCheckExpressionParseResult Fail(string message, PostgresCheckToken token)
        {
            return PostgresCheckExpressionParseResult.Failed($"{message} at position {token.Position}");
        }

        private static PostgresCheckParseException Error(string message, PostgresCheckToken token)
        {
            return new PostgresCheckParseException($"{message} at position {token.Position}");
        }

        private static bool IsComparisonOperator(string op)
        {
            return op is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "~";
        }

        private static string NormalizeOperator(string op)
        {
            return op == "!=" ? "<>" : op.ToLowerInvariant();
        }

        private static bool IsExpressionKeyword(string keyword)
        {
            return keyword.Equals("and", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("or", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("not", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("is", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("in", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("when", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("then", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("else", StringComparison.OrdinalIgnoreCase)
                   || keyword.Equals("end", StringComparison.OrdinalIgnoreCase);
        }

        private static string TerminatorText(PostgresCheckTokenKind terminator)
        {
            return terminator switch
            {
                PostgresCheckTokenKind.RightParen => ")",
                PostgresCheckTokenKind.RightBracket => "]",
                _ => terminator.ToString()
            };
        }
    }

    private sealed class PostgresCheckParseException : Exception
    {
        public PostgresCheckParseException(string message)
            : base(message)
        {
        }
    }
}
