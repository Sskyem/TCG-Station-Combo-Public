using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Updates existing top-level values in a commented JSON file without re-serializing
/// the whole document. This preserves comments, property order, and hand-authored fields.
/// </summary>
public static class JsoncConfigWriter
{
    private readonly struct ValueSpan
    {
        public readonly int Start;
        public readonly int End;

        public ValueSpan(int start, int end)
        {
            Start = start;
            End = end;
        }
    }

    private readonly struct Replacement
    {
        public readonly int Start;
        public readonly int End;
        public readonly string Text;

        public Replacement(int start, int end, string text)
        {
            Start = start;
            End = end;
            Text = text;
        }
    }

    public static bool TryUpdateFile(
        string path,
        IEnumerable<KeyValuePair<string, JToken>> updates,
        out string error)
    {
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                error = $"Config file does not exist: {path}";
                return false;
            }

            string source = File.ReadAllText(path);
            if (!TryFindTopLevelValues(source, out Dictionary<string, ValueSpan> spans, out error))
                return false;

            var replacements = new List<Replacement>();
            foreach (KeyValuePair<string, JToken> update in updates)
            {
                if (!spans.TryGetValue(update.Key, out ValueSpan span))
                {
                    error = $"Top-level property '{update.Key}' was not found in {path}.";
                    return false;
                }

                string formatted = FormatValue(update.Value ?? JValue.CreateNull(), source, span.Start);
                replacements.Add(new Replacement(span.Start, span.End, formatted));
            }

            replacements.Sort((a, b) => b.Start.CompareTo(a.Start));
            var result = new StringBuilder(source);
            foreach (Replacement replacement in replacements)
            {
                result.Remove(replacement.Start, replacement.End - replacement.Start);
                result.Insert(replacement.Start, replacement.Text);
            }

            File.WriteAllText(path, result.ToString());
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static string FormatValue(JToken value, string source, int valueStart)
    {
        string formatted = value.ToString(Formatting.Indented);
        if (formatted.IndexOf('\n') < 0)
            return formatted;

        int lineStart = source.LastIndexOf('\n', Math.Max(0, valueStart - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        int indentEnd = lineStart;
        while (indentEnd < source.Length && (source[indentEnd] == ' ' || source[indentEnd] == '\t'))
            indentEnd++;

        string propertyIndent = source.Substring(lineStart, indentEnd - lineStart);
        return formatted.Replace("\n", "\n" + propertyIndent);
    }

    private static bool TryFindTopLevelValues(
        string source,
        out Dictionary<string, ValueSpan> spans,
        out string error)
    {
        spans = new Dictionary<string, ValueSpan>(StringComparer.Ordinal);
        error = null;
        int position = 0;

        SkipTrivia(source, ref position);
        if (position >= source.Length || source[position] != '{')
        {
            error = "The config root must be a JSON object.";
            return false;
        }
        position++;

        while (true)
        {
            SkipTrivia(source, ref position);
            if (position >= source.Length)
            {
                error = "Unexpected end of file while reading the config object.";
                return false;
            }

            if (source[position] == '}')
                return true;

            if (source[position] != '"')
            {
                error = $"Expected a property name at character {position}.";
                return false;
            }

            int keyStart = position;
            if (!TrySkipString(source, ref position, out error))
                return false;

            string key;
            try
            {
                key = JsonConvert.DeserializeObject<string>(
                    source.Substring(keyStart, position - keyStart));
            }
            catch (Exception e)
            {
                error = $"Invalid property name at character {keyStart}: {e.Message}";
                return false;
            }

            SkipTrivia(source, ref position);
            if (position >= source.Length || source[position] != ':')
            {
                error = $"Expected ':' after property '{key}'.";
                return false;
            }
            position++;

            SkipTrivia(source, ref position);
            int valueStart = position;
            if (!TrySkipValue(source, ref position, out error))
                return false;

            if (spans.ContainsKey(key))
            {
                error = $"Duplicate top-level property '{key}' is not supported.";
                return false;
            }
            spans[key] = new ValueSpan(valueStart, position);

            SkipTrivia(source, ref position);
            if (position >= source.Length)
            {
                error = $"Unexpected end of file after property '{key}'.";
                return false;
            }

            if (source[position] == ',')
            {
                position++;
                continue;
            }

            if (source[position] == '}')
                return true;

            error = $"Expected ',' or '}}' after property '{key}'.";
            return false;
        }
    }

    private static bool TrySkipValue(string source, ref int position, out string error)
    {
        error = null;
        if (position >= source.Length)
        {
            error = "Expected a JSON value.";
            return false;
        }

        if (source[position] == '"')
            return TrySkipString(source, ref position, out error);

        if (source[position] == '{' || source[position] == '[')
            return TrySkipContainer(source, ref position, out error);

        int start = position;
        while (position < source.Length)
        {
            char c = source[position];
            if (c == ',' || c == '}' || char.IsWhiteSpace(c) || c == '/')
                break;
            position++;
        }

        if (position == start)
        {
            error = $"Expected a JSON value at character {position}.";
            return false;
        }
        return true;
    }

    private static bool TrySkipContainer(string source, ref int position, out string error)
    {
        error = null;
        var closing = new Stack<char>();
        closing.Push(source[position] == '{' ? '}' : ']');
        position++;

        while (position < source.Length && closing.Count > 0)
        {
            char c = source[position];
            if (c == '"')
            {
                if (!TrySkipString(source, ref position, out error))
                    return false;
                continue;
            }

            if (c == '/' && position + 1 < source.Length)
            {
                if (source[position + 1] == '/')
                {
                    position += 2;
                    while (position < source.Length && source[position] != '\n')
                        position++;
                    continue;
                }

                if (source[position + 1] == '*')
                {
                    int end = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        error = "Unterminated block comment.";
                        return false;
                    }
                    position = end + 2;
                    continue;
                }
            }

            if (c == '{')
                closing.Push('}');
            else if (c == '[')
                closing.Push(']');
            else if (c == '}' || c == ']')
            {
                if (closing.Peek() != c)
                {
                    error = $"Mismatched closing delimiter '{c}' at character {position}.";
                    return false;
                }
                closing.Pop();
            }
            position++;
        }

        if (closing.Count > 0)
        {
            error = "Unterminated object or array value.";
            return false;
        }
        return true;
    }

    private static bool TrySkipString(string source, ref int position, out string error)
    {
        error = null;
        position++;

        while (position < source.Length)
        {
            char c = source[position++];
            if (c == '"')
                return true;

            if (c == '\\')
            {
                if (position >= source.Length)
                {
                    error = "Unterminated escape sequence in a JSON string.";
                    return false;
                }
                position++;
            }
        }

        error = "Unterminated JSON string.";
        return false;
    }

    private static void SkipTrivia(string source, ref int position)
    {
        while (position < source.Length)
        {
            if (char.IsWhiteSpace(source[position]))
            {
                position++;
                continue;
            }

            if (source[position] != '/' || position + 1 >= source.Length)
                return;

            if (source[position + 1] == '/')
            {
                position += 2;
                while (position < source.Length && source[position] != '\n')
                    position++;
                continue;
            }

            if (source[position + 1] == '*')
            {
                int end = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                position = end < 0 ? source.Length : end + 2;
                continue;
            }

            return;
        }
    }
}
