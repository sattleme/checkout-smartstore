using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// A JSON value, read the same way everywhere (SPEC.md §2), as JavaScript reads it: of members with
/// one name the last counts; a number is its value as an IEEE 754 double whatever its spelling
/// (<c>500</c>, <c>500.0</c> and <c>5e2</c> are 500); a boolean is never a number; a string keeps a
/// lone surrogate a <c>\ud800</c> escape makes, so that the checks of a text can refuse it. A C#
/// null stands for an absent value (JavaScript's undefined), <see cref="JsonNull"/> for JSON's null.
/// </summary>
internal abstract class Json
{
    public static readonly JsonNull Null = new();
    public static readonly JsonBool True = new(true);
    public static readonly JsonBool False = new(false);

    public static JsonString Of(string value) => new(value);

    public static JsonNumber Of(long value) => new(value);

    public static JsonBool Of(bool value) => value ? True : False;

    /// <summary><paramref name="value"/> as a JSON string, or JSON's null.</summary>
    public static Json OfNullable(string? value) => value is null ? Null : new JsonString(value);

    /// <summary><paramref name="value"/> as a JSON number, or JSON's null.</summary>
    public static Json OfNullable(long? value) => value is null ? Null : new JsonNumber(value.Value);

    /// <summary>How deep arrays and objects, counted together, may nest in a text that is JSON (SPEC.md §2).</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// The value of JSON <paramref name="text"/> (RFC 8259, as JavaScript's JSON.parse reads it), or
    /// null when it is not JSON: a text nested more than <see cref="MaxDepth"/> deep is not, as a
    /// pre-scan tells before any parsing (the reader itself takes any depth).
    /// </summary>
    public static Json? Parse(string text)
    {
        if (NestsTooDeep(text))
        {
            return null;
        }
        try
        {
            return new JsonReader(text).ReadDocument();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pre-scan of SPEC.md §2: whether <paramref name="text"/> opens arrays and objects, counted
    /// together, more than <see cref="MaxDepth"/> deep. Each <c>[</c> or <c>{</c> outside a string
    /// adds a level, each <c>]</c> or <c>}</c> outside a string ends one; a string runs from a
    /// <c>"</c> to the next <c>"</c> not escaped (a backslash escapes the character after it).
    /// </summary>
    public static bool NestsTooDeep(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is '[' or '{')
            {
                if (++depth > MaxDepth)
                {
                    return true;
                }
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
        }
        return false;
    }

    /// <summary>The value of UTF-8 JSON <paramref name="bytes"/> (strict UTF-8), or null.</summary>
    public static Json? Parse(ReadOnlySpan<byte> bytes)
    {
        var text = Text.DecodeUtf8(bytes);
        return text is null ? null : Parse(text);
    }

    /// <summary>This value as JavaScript's JSON.stringify writes it: no whitespace.</summary>
    public string Write()
    {
        var builder = new StringBuilder();
        JsonWriter.Write(this, builder);
        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => Write();
}

internal sealed class JsonNull : Json
{
    internal JsonNull()
    {
    }
}

internal sealed class JsonBool(bool value) : Json
{
    public bool Value { get; } = value;
}

internal sealed class JsonNumber : Json
{
    public JsonNumber(double value, string? spelling = null)
    {
        Value = value;
        Spelling = spelling;
    }

    /// <summary>The number, as an IEEE 754 double.</summary>
    public double Value { get; }

    /// <summary>How it was written in what was read (tests serve vectors' spellings as they are).</summary>
    public string? Spelling { get; }
}

internal sealed class JsonString(string value) : Json
{
    public string Value { get; } = value;
}

internal sealed class JsonArray : Json
{
    private readonly List<Json> _items;

    public JsonArray() => _items = [];

    public JsonArray(IEnumerable<Json> items) => _items = [.. items];

    public IReadOnlyList<Json> Items => _items;

    public int Count => _items.Count;

    public Json this[int index] => _items[index];

    public JsonArray Add(Json item)
    {
        _items.Add(item);
        return this;
    }
}

/// <summary>A JSON object: its members in the order they first came, the last value of a name counting.</summary>
internal sealed class JsonObject : Json
{
    private readonly Dictionary<string, Json> _members = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];

    public IReadOnlyList<string> Names => _names;

    public int Count => _names.Count;

    /// <summary>The member <paramref name="name"/>, or null when there is none (undefined).</summary>
    public Json? this[string name] => _members.GetValueOrDefault(name);

    public bool Has(string name) => _members.ContainsKey(name);

    /// <summary>Sets <paramref name="name"/>: a name already there keeps its place and takes the value.</summary>
    public JsonObject Set(string name, Json value)
    {
        if (!_members.ContainsKey(name))
        {
            _names.Add(name);
        }
        _members[name] = value;
        return this;
    }

    public JsonObject Set(string name, string value) => Set(name, Of(value));

    public JsonObject Set(string name, long value) => Set(name, Of(value));

    public JsonObject Set(string name, bool value) => Set(name, Of(value));

    /// <summary>The members in their order.</summary>
    public IEnumerable<KeyValuePair<string, Json>> Members
    {
        get
        {
            foreach (var name in _names)
            {
                yield return new KeyValuePair<string, Json>(name, _members[name]);
            }
        }
    }
}

/// <summary>
/// RFC 8259 as JavaScript's JSON.parse reads it, without recursion (a relay's message may nest
/// deeply): whitespace is space, tab, line feed and carriage return; a string holds no raw control
/// character; escapes are <c>\" \\ \/ \b \f \n \r \t \uXXXX</c>.
/// </summary>
internal sealed class JsonReader(string text)
{
    private readonly string _text = text;
    private int _position;

    private sealed class Frame
    {
        public JsonArray? Array;
        public JsonObject? Object;
        public string? Name;
    }

    public Json ReadDocument()
    {
        var stack = new Stack<Frame>();
        SkipWhitespace();
        while (true)
        {
            Json value;
            var c = Peek();
            if (c == '{')
            {
                _position++;
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _position++;
                    value = new JsonObject();
                }
                else
                {
                    var frame = new Frame { Object = new JsonObject() };
                    frame.Name = ReadName();
                    stack.Push(frame);
                    continue;
                }
            }
            else if (c == '[')
            {
                _position++;
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _position++;
                    value = new JsonArray();
                }
                else
                {
                    stack.Push(new Frame { Array = new JsonArray() });
                    continue;
                }
            }
            else
            {
                value = ReadScalar();
            }

            // A value is complete: it goes into the container above it, which may end too.
            while (true)
            {
                if (stack.Count == 0)
                {
                    SkipWhitespace();
                    if (_position != _text.Length)
                    {
                        throw new FormatException("Text after the JSON value");
                    }
                    return value;
                }
                var frame = stack.Peek();
                if (frame.Object is not null)
                {
                    frame.Object.Set(frame.Name!, value);
                }
                else
                {
                    frame.Array!.Add(value);
                }
                SkipWhitespace();
                var next = Peek();
                if (next == ',')
                {
                    _position++;
                    SkipWhitespace();
                    if (frame.Object is not null)
                    {
                        frame.Name = ReadName();
                    }
                    break;
                }
                if (next == '}' && frame.Object is not null)
                {
                    _position++;
                    stack.Pop();
                    value = frame.Object;
                    continue;
                }
                if (next == ']' && frame.Array is not null)
                {
                    _position++;
                    stack.Pop();
                    value = frame.Array;
                    continue;
                }
                throw new FormatException("Expected , or the end of a container");
            }
        }
    }

    private char Peek()
    {
        if (_position >= _text.Length)
        {
            throw new FormatException("Unexpected end of JSON");
        }
        return _text[_position];
    }

    private void SkipWhitespace()
    {
        while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\n' or '\r')
        {
            _position++;
        }
    }

    /// <summary>A member's name, then its colon: the value follows.</summary>
    private string ReadName()
    {
        if (Peek() != '"')
        {
            throw new FormatException("Expected a member name");
        }
        var name = ReadString();
        SkipWhitespace();
        if (Peek() != ':')
        {
            throw new FormatException("Expected :");
        }
        _position++;
        SkipWhitespace();
        return name;
    }

    private Json ReadScalar()
    {
        var c = Peek();
        switch (c)
        {
            case '"':
                return new JsonString(ReadString());
            case 't':
                Expect("true");
                return Json.True;
            case 'f':
                Expect("false");
                return Json.False;
            case 'n':
                Expect("null");
                return Json.Null;
            default:
                if (c == '-' || c is >= '0' and <= '9')
                {
                    return ReadNumber();
                }
                throw new FormatException("Unexpected character");
        }
    }

    private void Expect(string literal)
    {
        if (string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
        {
            throw new FormatException("Unexpected literal");
        }
        _position += literal.Length;
    }

    private JsonNumber ReadNumber()
    {
        var start = _position;
        if (_text[_position] == '-')
        {
            _position++;
        }
        if (Peek() == '0')
        {
            _position++;
        }
        else if (Peek() is >= '1' and <= '9')
        {
            SkipDigits();
        }
        else
        {
            throw new FormatException("Expected a digit");
        }
        if (_position < _text.Length && _text[_position] == '.')
        {
            _position++;
            if (Peek() is < '0' or > '9')
            {
                throw new FormatException("Expected a digit");
            }
            SkipDigits();
        }
        if (_position < _text.Length && _text[_position] is 'e' or 'E')
        {
            _position++;
            if (Peek() is '+' or '-')
            {
                _position++;
            }
            if (Peek() is < '0' or > '9')
            {
                throw new FormatException("Expected a digit");
            }
            SkipDigits();
        }
        var spelling = _text[start.._position];
        // Correctly rounded since .NET Core 3.0, whatever the length; beyond a double, ±Infinity.
        var value = double.Parse(spelling, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new JsonNumber(value, spelling);
    }

    private void SkipDigits()
    {
        while (_position < _text.Length && _text[_position] is >= '0' and <= '9')
        {
            _position++;
        }
    }

    private string ReadString()
    {
        _position++; // the opening quote
        StringBuilder? builder = null;
        var runStart = _position;
        while (true)
        {
            var c = Peek();
            if (c == '"')
            {
                var tail = _text[runStart.._position];
                _position++;
                return builder is null ? tail : builder.Append(tail).ToString();
            }
            if (c < 0x20)
            {
                throw new FormatException("A control character in a string");
            }
            if (c != '\\')
            {
                _position++;
                continue;
            }
            builder ??= new StringBuilder();
            builder.Append(_text, runStart, _position - runStart);
            _position++;
            var escape = Peek();
            _position++;
            switch (escape)
            {
                case 'u':
                    if (_position + 4 > _text.Length)
                    {
                        throw new FormatException("A \\u escape cut short");
                    }
                    var code = 0;
                    for (var i = 0; i < 4; i++)
                    {
                        var h = _text[_position + i];
                        var digit = h switch
                        {
                            >= '0' and <= '9' => h - '0',
                            >= 'a' and <= 'f' => h - 'a' + 10,
                            >= 'A' and <= 'F' => h - 'A' + 10,
                            _ => throw new FormatException("Not a hex digit"),
                        };
                        code = (code << 4) | digit;
                    }
                    _position += 4;
                    // A lone surrogate stays one: the checks of a text refuse it (SPEC.md §6).
                    builder.Append((char)code);
                    break;
                default:
                    builder.Append(escape switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw new FormatException("Unknown escape"),
                    });
                    break;
            }
            runStart = _position;
        }
    }
}

/// <summary>JSON as JavaScript's JSON.stringify writes it: no whitespace, its escapes, its numbers.</summary>
internal static class JsonWriter
{
    public static void Write(Json value, StringBuilder output)
    {
        // Iterative too: whatever was read can be written.
        var stack = new Stack<(Json Value, int Index)>();
        stack.Push((value, -1));
        while (stack.Count > 0)
        {
            var (current, index) = stack.Pop();
            switch (current)
            {
                case JsonNull:
                    output.Append("null");
                    break;
                case JsonBool b:
                    output.Append(b.Value ? "true" : "false");
                    break;
                case JsonNumber n:
                    output.Append(Numbers.JavaScript(n.Value) ?? "null");
                    break;
                case JsonString s:
                    WriteString(s.Value, output);
                    break;
                case JsonArray array:
                    if (index == -1)
                    {
                        output.Append('[');
                        index = 0;
                    }
                    if (index >= array.Count)
                    {
                        output.Append(']');
                        break;
                    }
                    if (index > 0)
                    {
                        output.Append(',');
                    }
                    stack.Push((array, index + 1));
                    stack.Push((array[index], -1));
                    break;
                case JsonObject obj:
                    if (index == -1)
                    {
                        output.Append('{');
                        index = 0;
                    }
                    if (index >= obj.Count)
                    {
                        output.Append('}');
                        break;
                    }
                    if (index > 0)
                    {
                        output.Append(',');
                    }
                    var name = obj.Names[index];
                    WriteString(name, output);
                    output.Append(':');
                    stack.Push((obj, index + 1));
                    stack.Push((obj[name]!, -1));
                    break;
            }
        }
    }

    /// <summary>A string as ECMAScript's QuoteJSONString writes it (lone surrogates escaped).</summary>
    public static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var escape = c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => null,
            };
            if (escape is not null)
            {
                output.Append(escape);
            }
            else if (c < 0x20)
            {
                output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                output.Append(c).Append(value[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                output.Append(c);
            }
        }
        output.Append('"');
    }
}
