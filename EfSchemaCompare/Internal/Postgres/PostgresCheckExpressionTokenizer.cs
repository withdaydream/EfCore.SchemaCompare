using System;
using System.Collections.Generic;
using System.Globalization;

namespace EfSchemaCompare.Internal.Postgres;

internal static class PostgresCheckExpressionTokenizer
{
    public static IReadOnlyList<PostgresCheckToken> Tokenize(string sql)
    {
        var tokenizer = new Tokenizer(sql);
        return tokenizer.Tokenize();
    }

    private sealed class Tokenizer
    {
        private readonly string _sql;
        private readonly List<PostgresCheckToken> _tokens = new();
        private int _position;

        public Tokenizer(string sql)
        {
            _sql = sql ?? string.Empty;
        }

        public IReadOnlyList<PostgresCheckToken> Tokenize()
        {
            while (!IsAtEnd)
            {
                var c = Current;
                if (char.IsWhiteSpace(c))
                {
                    _position++;
                    continue;
                }

                if (c == '"')
                {
                    ReadQuotedIdentifier();
                    continue;
                }

                if (c == '\'')
                {
                    ReadString();
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    ReadIdentifier();
                    continue;
                }

                if (char.IsDigit(c))
                {
                    ReadNumber();
                    continue;
                }

                ReadSymbol();
            }

            _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.End, string.Empty, _position));
            return _tokens;
        }

        private bool IsAtEnd => _position >= _sql.Length;

        private char Current => _sql[_position];

        private char Peek(int offset)
        {
            var index = _position + offset;
            return index >= _sql.Length ? '\0' : _sql[index];
        }

        private void ReadQuotedIdentifier()
        {
            var start = _position;
            _position++;
            var value = string.Empty;

            while (!IsAtEnd)
            {
                if (Current == '"')
                {
                    if (Peek(1) == '"')
                    {
                        value += '"';
                        _position += 2;
                        continue;
                    }

                    _position++;
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Identifier, value, start));
                    return;
                }

                value += Current;
                _position++;
            }

            _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Invalid, "Unterminated quoted identifier", start));
        }

        private void ReadString()
        {
            var start = _position;
            _position++;
            var value = string.Empty;

            while (!IsAtEnd)
            {
                if (Current == '\'')
                {
                    if (Peek(1) == '\'')
                    {
                        value += '\'';
                        _position += 2;
                        continue;
                    }

                    _position++;
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.String, value, start));
                    return;
                }

                value += Current;
                _position++;
            }

            _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Invalid, "Unterminated string literal", start));
        }

        private void ReadIdentifier()
        {
            var start = _position;
            _position++;
            while (!IsAtEnd && IsIdentifierPart(Current))
                _position++;

            var value = _sql[start.._position];
            _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Identifier, value, start));
        }

        private void ReadNumber()
        {
            var start = _position;
            _position++;
            while (!IsAtEnd && char.IsDigit(Current))
                _position++;

            if (!IsAtEnd && Current == '.' && char.IsDigit(Peek(1)))
            {
                _position++;
                while (!IsAtEnd && char.IsDigit(Current))
                    _position++;
            }

            _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Number, _sql[start.._position], start));
        }

        private void ReadSymbol()
        {
            var start = _position;
            var two = _position + 1 < _sql.Length ? _sql.Substring(_position, 2) : string.Empty;
            switch (two)
            {
                case "::":
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.DoubleColon, two, start));
                    _position += 2;
                    return;
                case "<>":
                case "!=":
                case "<=":
                case ">=":
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Operator, two, start));
                    _position += 2;
                    return;
            }

            switch (Current)
            {
                case '(':
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.LeftParen, "(", start));
                    break;
                case ')':
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.RightParen, ")", start));
                    break;
                case '[':
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.LeftBracket, "[", start));
                    break;
                case ']':
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.RightBracket, "]", start));
                    break;
                case ',':
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Comma, ",", start));
                    break;
                case '=':
                case '<':
                case '>':
                case '~':
                case '+':
                case '-':
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Operator, Current.ToString(CultureInfo.InvariantCulture), start));
                    break;
                default:
                    _tokens.Add(new PostgresCheckToken(PostgresCheckTokenKind.Invalid, $"Unexpected character '{Current}'", start));
                    break;
            }

            _position++;
        }

        private static bool IsIdentifierStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static bool IsIdentifierPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$';
        }
    }
}

internal readonly record struct PostgresCheckToken(PostgresCheckTokenKind Kind, string Text, int Position)
{
    public bool IsKeyword(string keyword)
    {
        return Kind == PostgresCheckTokenKind.Identifier
               && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
    }
}

internal enum PostgresCheckTokenKind
{
    Identifier,
    String,
    Number,
    Operator,
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket,
    Comma,
    DoubleColon,
    End,
    Invalid
}
