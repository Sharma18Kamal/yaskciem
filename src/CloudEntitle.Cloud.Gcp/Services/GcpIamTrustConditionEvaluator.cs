using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudEntitle.Cloud.Gcp.Services;

public static class GcpIamTrustConditionEvaluator
{
    private static readonly Regex TokenPattern = new(
        """\G\s*(\&\&|\|\||==|!=|<=|>=|[\(\)!,<>\[\]]|in\b|[A-Za-z_][A-Za-z0-9_.]*|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool? Evaluate(string? expression, string resourceName, DateTime evaluatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096
            || string.IsNullOrWhiteSpace(resourceName))
            return null;
        try
        {
            var tokens = Tokenize(expression.Trim());
            if (tokens.Count > 512) return null;
            return new Parser(tokens, resourceName, evaluatedAtUtc.ToUniversalTime()).Parse();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Tokenize(string expression)
    {
        var tokens = new List<string>();
        var offset = 0;
        while (offset < expression.Length)
        {
            var match = TokenPattern.Match(expression, offset);
            if (!match.Success) throw new FormatException("Unsupported GCP IAM condition syntax.");
            tokens.Add(match.Groups[1].Value);
            offset = match.Index + match.Length;
        }
        return tokens;
    }

    private sealed class Parser(IReadOnlyList<string> tokens, string resourceName, DateTime evaluatedAtUtc)
    {
        private int _position;
        private int _depth;

        public bool Parse()
        {
            var result = ParseOr();
            if (_position != tokens.Count) throw new FormatException("Unexpected token in condition.");
            return result;
        }

        private bool ParseOr()
        {
            var result = ParseAnd();
            while (Take("||"))
            {
                var right = ParseAnd();
                result |= right;
            }
            return result;
        }

        private bool ParseAnd()
        {
            var result = ParseUnary();
            while (Take("&&"))
            {
                var right = ParseUnary();
                result &= right;
            }
            return result;
        }

        private bool ParseUnary()
        {
            if (Take("!")) return AtNestedDepth(() => !ParseUnary());
            if (Take("("))
            {
                var value = AtNestedDepth(ParseOr);
                Expect(")");
                return value;
            }
            if (_position >= tokens.Count) throw new FormatException("Missing condition operand.");
            if (Take("true")) return true;
            if (Take("false")) return false;

            var left = ReadValue();
            var operation = Next();
            if (operation == "in")
            {
                Expect("[");
                var matched = false;
                if (!Take("]"))
                {
                    do
                    {
                        matched |= ValuesEqual(left, ReadValue());
                    } while (Take(","));
                    Expect("]");
                }
                return matched;
            }

            var right = ReadValue();
            var comparison = Compare(left, right);
            return operation switch
            {
                "==" => comparison == 0,
                "!=" => comparison != 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                _ => throw new FormatException("Unsupported comparison operator.")
            };
        }

        private object ReadValue()
        {
            var token = Next();
            if (IsQuoted(token)) return Unquote(token);
            if (token.Equals("request.time", StringComparison.Ordinal)) return evaluatedAtUtc;
            if (token.Equals("resource.name", StringComparison.Ordinal)) return resourceName;
            if (token.Equals("resource.type", StringComparison.Ordinal)) return "iam.googleapis.com/ServiceAccount";
            if (token.Equals("timestamp", StringComparison.Ordinal))
            {
                Expect("(");
                var value = ReadValue();
                Expect(")");
                if (value is not string text || !DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                    throw new FormatException("Invalid timestamp literal.");
                return timestamp;
            }
            if (bool.TryParse(token, out var boolean)) return boolean;
            throw new FormatException($"Unsupported condition value '{token}'.");
        }

        private static bool IsQuoted(string token)
            => token.Length >= 2 && (token[0] == '"' && token[^1] == '"' || token[0] == '\'' && token[^1] == '\'');

        private static string Unquote(string token)
        {
            var content = token[1..^1];
            return content.Replace("\\'", "'", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        private static bool ValuesEqual(object left, object right)
            => left.GetType() == right.GetType() && Equals(left, right);

        private static int Compare(object left, object right)
        {
            if (left is string leftString && right is string rightString)
                return string.CompareOrdinal(leftString, rightString);
            if (left is DateTime leftDate && right is DateTime rightDate)
                return DateTime.Compare(leftDate, rightDate);
            if (left is bool leftBool && right is bool rightBool)
                return leftBool.CompareTo(rightBool);
            throw new FormatException("Condition operands have incompatible types.");
        }

        private string Next()
        {
            if (_position >= tokens.Count) throw new FormatException("Missing condition token.");
            return tokens[_position++];
        }

        private bool Take(string expected)
        {
            if (_position >= tokens.Count || !tokens[_position].Equals(expected, StringComparison.Ordinal)) return false;
            _position++;
            return true;
        }

        private void Expect(string expected)
        {
            if (!Take(expected)) throw new FormatException($"Expected '{expected}'.");
        }

        private bool AtNestedDepth(Func<bool> evaluate)
        {
            if (++_depth > 64) throw new FormatException("Condition nesting limit exceeded.");
            try { return evaluate(); }
            finally { _depth--; }
        }
    }
}
