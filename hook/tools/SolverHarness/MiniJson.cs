// MiniJson.cs -- enough JSON to read what the trace recorder writes, and no more.
//
// WHY NOT `System.Text.Json`. The trace is written by hand as JSON lines by the hook, and the
// hook may not take a dependency it does not already have. Reading it back with a full parser
// would work -- but this project's rule is that a fixture is only evidence if it can be checked
// by eye, and a parser whose behaviour depends on a package version is one more thing between
// the operator's run and the number in the report.
//
// WHY NOT REGULAR EXPRESSIONS. Because the shape is recursive (an array of objects with arrays
// inside) and a regex that "works" on the sample stops working on the frame where a bullet's
// path is one sample long. This is a small recursive-descent parser: about two hundred lines,
// one behaviour, no surprises. It has no error recovery and throws on bad input, which is right:
// a malformed trace should stop the analysis, not be skipped.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Drelay.SolverHarness
{
    /// <summary>A parsed JSON value, or a fluent accessor for one.</summary>
    public sealed class Json
    {
        private readonly object _value;

        private Json(object value)
        {
            _value = value;
        }

        public static readonly Json Null = new Json(null);

        public bool IsNull => _value == null;
        public bool IsObject => _value is Dictionary<string, Json>;
        public bool IsArray => _value is List<Json>;
        public bool IsNumber => _value is double;

        public double AsDouble(double fallback = 0.0)
        {
            if (_value is double d)
            {
                return d;
            }
            if (_value is string s && double.TryParse(s, NumberStyles.Float,
                                                      CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }
            return fallback;
        }

        public float AsFloat(float fallback = 0.0f) => (float)AsDouble(fallback);

        public int AsInt(int fallback = 0)
        {
            double d = AsDouble(fallback);
            return (int)d;
        }

        public long AsLong(long fallback = 0)
        {
            double d = AsDouble(fallback);
            return (long)d;
        }

        public bool AsBool(bool fallback = false)
        {
            if (_value is bool b)
            {
                return b;
            }
            if (_value is double d)
            {
                return d != 0.0;
            }
            return fallback;
        }

        public string AsString(string fallback = "")
        {
            if (_value is string s)
            {
                return s;
            }
            return fallback;
        }

        /// <summary>The array's elements, or an empty list. Never null, so callers need no branch.</summary>
        public List<Json> Items => _value as List<Json> ?? new List<Json>();

        public int Count => Items.Count;

        /// <summary>A member of an object, or Null. Chained lookups therefore never throw.</summary>
        public Json this[string name]
        {
            get
            {
                if (_value is Dictionary<string, Json> map && map.TryGetValue(name, out Json found))
                {
                    return found;
                }
                return Null;
            }
        }

        public bool Has(string name) => _value is Dictionary<string, Json> map && map.ContainsKey(name);

        /// <summary>Parses one line. The trace is JSON Lines, so this is called once per line.</summary>
        public static Json Parse(string text)
        {
            int index = 0;
            Json value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length)
            {
                throw new FormatException("trailing content at offset " + index);
            }
            return value;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
            {
                i++;
            }
        }

        private static Json ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
            {
                throw new FormatException("unexpected end of input");
            }
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return new Json(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return new Json(true);
                case 'f': Expect(s, ref i, "false"); return new Json(false);
                case 'n': Expect(s, ref i, "null"); return Null;
                default: return new Json(ParseNumber(s, ref i));
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            {
                throw new FormatException("expected '" + word + "' at offset " + i);
            }
            i += word.Length;
        }

        private static Json ParseObject(string s, ref int i)
        {
            var map = new Dictionary<string, Json>(StringComparer.Ordinal);
            i++;   // {
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return new Json(map);
            }
            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (s[i] != ':')
                {
                    throw new FormatException("expected ':' at offset " + i);
                }
                i++;
                map[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return new Json(map);
                }
                throw new FormatException("expected ',' or '}' at offset " + i);
            }
        }

        private static Json ParseArray(string s, ref int i)
        {
            var list = new List<Json>();
            i++;   // [
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return new Json(list);
            }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return new Json(list);
                }
                throw new FormatException("expected ',' or ']' at offset " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"')
            {
                throw new FormatException("expected a string at offset " + i);
            }
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length)
                {
                    break;
                }
                char esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length
                            && int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                            CultureInfo.InvariantCulture, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(esc); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.'
                    || c == 'e' || c == 'E')
                {
                    i++;
                    continue;
                }
                break;
            }
            if (i == start)
            {
                throw new FormatException("expected a number at offset " + start);
            }
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float,
                                CultureInfo.InvariantCulture);
        }
    }
}
