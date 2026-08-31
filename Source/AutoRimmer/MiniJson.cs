using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoRimmer
{
    // Tiny JSON support, generalized from AnalyzerBridge's MiniJson: the same
    // recursive-descent parser (string / number / bool / null / object / array),
    // plus a tree writer so verbs can return arbitrary payloads as plain object
    // trees (Dictionary<string,object> / List<object> / string / double / bool /
    // null) instead of hand-assembling JSON per result shape. No external
    // packages, no Unity JsonUtility (avoids an extra module reference).
    public static class MiniJson
    {
        public static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            AppendString(sb, s);
            return sb.ToString();
        }

        public static string N(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // Serializes an object tree. Unknown types fall back to their ToString()
        // as a JSON string — the writer must never throw, because every consumed
        // command owes exactly one result file.
        //
        // "Must never throw" was a comment, not a property, until 1.5: the
        // default arm called value.ToString() unguarded and AppendString NREd
        // on a null string, and both are reachable the moment a wave-2/3 verb
        // returns a Verse object (git-bug 4b65a28, defect 4). MaxDepth is the
        // third guard: a self-referential tree would recurse to a
        // StackOverflowException, which no try/catch in .NET can catch.
        private const int MaxDepth = 64;

        public static void Write(StringBuilder sb, object value) => Write(sb, value, 0);

        private static void Write(StringBuilder sb, object value, int depth)
        {
            if (depth > MaxDepth)
            {
                AppendString(sb, "<autorimmer: nesting past " + MaxDepth + " levels>");
                return;
            }
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: AppendString(sb, s); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(N(f)); break;
                case double d: sb.Append(N(d)); break;
                case Dictionary<string, object> obj:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        AppendString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    break;
                }
                case List<object> list:
                {
                    sb.Append('[');
                    for (int idx = 0; idx < list.Count; idx++)
                    {
                        if (idx > 0) sb.Append(',');
                        Write(sb, list[idx], depth + 1);
                    }
                    sb.Append(']');
                    break;
                }
                case IEnumerable<string> strings:
                {
                    // Per element through Write, not AppendString: a null
                    // element is JSON null, not "".
                    sb.Append('[');
                    bool first = true;
                    foreach (var s in strings)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, s, depth + 1);
                    }
                    sb.Append(']');
                    break;
                }
                default:
                {
                    // A Verse object's ToString() is arbitrary game code and
                    // can throw or return null. Losing the whole result — and,
                    // before 1.5, the rest of the poller cycle with it — over
                    // one field is not a trade worth making.
                    string s;
                    try { s = value.ToString(); }
                    catch (Exception e) { s = "<autorimmer: " + value.GetType().Name + ".ToString() threw " + e.GetType().Name + ">"; }
                    AppendString(sb, s);
                    break;
                }
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            // Belt and braces: Write routes nulls to the JSON `null` literal
            // before they reach here, and Dictionary<string,object> cannot hold
            // a null key — but this used to NRE and it is one branch to close.
            if (s == null) { sb.Append("\"\""); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Parses one JSON object. Returns null on malformed input.
        public static Dictionary<string, object> Parse(string text)
        {
            try
            {
                int i = 0;
                SkipWs(text, ref i);
                var obj = ParseValue(text, ref i) as Dictionary<string, object>;
                return obj;
            }
            catch
            {
                return null;
            }
        }

        public static string GetString(Dictionary<string, object> obj, string key, string fallback = null)
            => obj != null && obj.TryGetValue(key, out var v) && v is string s ? s : fallback;

        private static object ParseValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            char c = t[i];
            if (c == '{') return ParseObject(t, ref i);
            if (c == '[') return ParseArray(t, ref i);
            if (c == '"') return ParseString(t, ref i);
            if (c == 't') { Expect(t, ref i, "true"); return true; }
            if (c == 'f') { Expect(t, ref i, "false"); return false; }
            if (c == 'n') { Expect(t, ref i, "null"); return null; }
            return ParseNumber(t, ref i);
        }

        private static Dictionary<string, object> ParseObject(string t, ref int i)
        {
            var obj = new Dictionary<string, object>();
            i++; // {
            SkipWs(t, ref i);
            if (t[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipWs(t, ref i);
                string key = ParseString(t, ref i);
                SkipWs(t, ref i);
                if (t[i] != ':') throw new FormatException("expected ':'");
                i++;
                obj[key] = ParseValue(t, ref i);
                SkipWs(t, ref i);
                if (t[i] == ',') { i++; continue; }
                if (t[i] == '}') { i++; return obj; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        private static List<object> ParseArray(string t, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(t, ref i);
            if (t[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(t, ref i));
                SkipWs(t, ref i);
                if (t[i] == ',') { i++; continue; }
                if (t[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        private static string ParseString(string t, ref int i)
        {
            if (t[i] != '"') throw new FormatException("expected '\"'");
            i++;
            var sb = new StringBuilder();
            while (t[i] != '"')
            {
                if (t[i] == '\\')
                {
                    i++;
                    switch (t[i])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            sb.Append((char)int.Parse(t.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException("bad escape");
                    }
                }
                else
                {
                    sb.Append(t[i]);
                }
                i++;
            }
            i++; // closing quote
            return sb.ToString();
        }

        private static double ParseNumber(string t, ref int i)
        {
            int start = i;
            while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '-' || t[i] == '+' || t[i] == '.' || t[i] == 'e' || t[i] == 'E'))
                i++;
            return double.Parse(t.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static void Expect(string t, ref int i, string word)
        {
            if (string.CompareOrdinal(t, i, word, 0, word.Length) != 0) throw new FormatException("expected " + word);
            i += word.Length;
        }

        private static void SkipWs(string t, ref int i)
        {
            while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\n' || t[i] == '\r')) i++;
        }
    }
}
