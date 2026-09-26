using System.Globalization;
using System.Text;
namespace MoenotesAssets;

/// <summary>A JSON object that keeps its insertion order (Python's dict), as the exporters build documents.</summary>
public sealed class PyObject : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> items = [];
    private readonly Dictionary<string, int> index = new(StringComparer.Ordinal);
    public int Count => items.Count;
    public IEnumerable<string> Keys => items.Select(p => p.Key);
    public object? this[string key]
    {
        get => index.TryGetValue(key, out var i) ? items[i].Value : throw new KeyNotFoundException(key);
        set
        {
            if (index.TryGetValue(key, out var i)) items[i] = new(key, value);
            else { index[key] = items.Count; items.Add(new(key, value)); }
        }
    }
    public void Add(string key, object? value) => this[key] = value;
    public bool ContainsKey(string key) => index.ContainsKey(key);
    public bool TryGetValue(string key, out object? value)
    {
        if (index.TryGetValue(key, out var i)) { value = items[i].Value; return true; }
        value = null; return false;
    }
    public object? Get(string key, object? fallback = null) => TryGetValue(key, out var v) ? v : fallback;
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// JSON text as nnnotes writes it (Python's json.dumps with ensure_ascii=False): objects in insertion order, floats in
/// Python's repr (shortest round-trip digits, "1.0", "1e-05", "3.4028234663852886e+38"), non-finite floats as the
/// out-of-range literals 1e999 / -1e999, NaN refused. Values: null, bool, string, integers, float/double,
/// <see cref="PyObject"/> and lists. The same document gives the same bytes as nnnotes' minified site files.
/// </summary>
public static class PyJson
{
    public static byte[] Minified(object? value) => Encoding.UTF8.GetBytes(Dumps(value));

    /// <summary>
    /// A JSON document as Python's json.loads reads it: objects as <see cref="PyObject"/>, arrays as lists, a number
    /// with a fraction or exponent as a double (1e999 as infinity), others as long (ulong when larger).
    /// </summary>
    public static object? Parse(ReadOnlySpan<byte> utf8)
    {
        var reader = new System.Text.Json.Utf8JsonReader(utf8, new System.Text.Json.JsonReaderOptions { MaxDepth = 256 });
        Config.Require(reader.Read(), "Empty JSON document");
        var value = Value(ref reader);
        Config.Require(!reader.Read(), "Trailing data after the JSON document");
        return value;
    }
    private static object? Value(ref System.Text.Json.Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case System.Text.Json.JsonTokenType.StartObject:
                var o = new PyObject();
                while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndObject)
                {
                    var key = reader.GetString()!; reader.Read(); o[key] = Value(ref reader);
                }
                return o;
            case System.Text.Json.JsonTokenType.StartArray:
                var list = new List<object?>();
                while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray) list.Add(Value(ref reader));
                return list;
            case System.Text.Json.JsonTokenType.String: return reader.GetString();
            case System.Text.Json.JsonTokenType.True: return true;
            case System.Text.Json.JsonTokenType.False: return false;
            case System.Text.Json.JsonTokenType.Null: return null;
            case System.Text.Json.JsonTokenType.Number:
                var text = Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan);
                if (text.IndexOfAny(['.', 'e', 'E']) >= 0) return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l : ulong.Parse(text, CultureInfo.InvariantCulture);
            default: throw new InvalidDataException($"Unexpected JSON token {reader.TokenType}");
        }
    }
    public static string Dumps(object? value)
    {
        var output = new StringBuilder();
        Write(output, value, "");
        return output.ToString();
    }

    private static void Write(StringBuilder output, object? value, string path)
    {
        switch (value)
        {
            case null: output.Append("null"); break;
            case bool b: output.Append(b ? "true" : "false"); break;
            case string s: String(output, s); break;
            case float f: output.Append(Float(f, path)); break;
            case double d: output.Append(Float(d, path)); break;
            case int or long or uint or ulong or short or ushort or byte or sbyte:
                output.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            case PyObject o:
                output.Append('{'); var first = true;
                foreach (var (key, item) in o)
                {
                    if (!first) output.Append(',');
                    first = false; String(output, key); output.Append(':'); Write(output, item, path + "/" + key);
                }
                output.Append('}'); break;
            case System.Collections.IList list:
                output.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) output.Append(',');
                    Write(output, list[i], $"{path}[{i}]");
                }
                output.Append(']'); break;
            default: throw new InvalidDataException($"No JSON form for {value.GetType().Name} at {(path.Length == 0 ? "/" : path)}");
        }
    }

    private static void String(StringBuilder output, string s)
    {
        output.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                default:
                    if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }

    /// <summary>A float as Python's repr writes it (a float32 value is written as the double it widens to).</summary>
    public static string Float(double d, string path = "")
    {
        if (double.IsNaN(d)) throw new InvalidDataException($"NaN at {(path.Length == 0 ? "/" : path)} (no JSON representation)");
        if (double.IsPositiveInfinity(d)) return "1e999";
        if (double.IsNegativeInfinity(d)) return "-1e999";
        return PyFloat.Repr(d);
    }
}
