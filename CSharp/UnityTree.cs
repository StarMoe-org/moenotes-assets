using AssetsTools.NET;
namespace MoenotesAssets;

/// <summary>
/// Typetree values as UnityPy's read_typetree gives them (nnnotes reads every object that way): a class is an ordered
/// object of its fields, a vector a list (a vector of bytes a list of integers), a pair a two-element list,
/// TypelessData bytes, integers by their type, float32 fields as the double they widen to, bool as bool.
/// </summary>
public static class UnityTree
{
    static readonly HashSet<string> Integers = new(StringComparer.Ordinal)
    {
        "SInt8", "UInt8", "char", "short", "SInt16", "unsigned short", "UInt16", "int", "SInt32", "unsigned int", "UInt32",
        "Type*", "long long", "SInt64", "unsigned long long", "UInt64", "FileSize",
    };

    public static object? Read(AssetTypeValueField field)
    {
        var template = field.TemplateField; var type = template.Type;
        if (type == "bool") return field.AsBool;
        if (Integers.Contains(type)) return Integer(field);
        if (type == "float") return (double)field.AsFloat;
        if (type == "double") return field.AsDouble;
        if (type == "string") return field.AsString;
        if (type == "TypelessData") return field.AsByteArray;
        if (type == "pair") return new List<object?> { Read(field.Children[0]), Read(field.Children[1]) };
        if (template.Children.Count > 0 && template.Children[0].IsArray)
        {
            var array = field.Children[0];
            if (array.Value?.ValueType == AssetValueType.ByteArray) return array.AsByteArray.Select(b => (object?)(long)b).ToList();
            var list = new List<object?>(array.Children.Count);
            foreach (var item in array.Children) list.Add(Read(item));
            return list;
        }
        if (type == "ManagedReferencesRegistry") throw new NotSupportedException($"Field {field.FieldName}: SerializeReference data is not supported");
        var result = new PyObject();
        foreach (var child in field.Children) result[child.FieldName] = Read(child);
        return result;
    }

    static object Integer(AssetTypeValueField field) => field.Value?.ValueType switch
    {
        AssetValueType.Int64 => field.AsLong,
        AssetValueType.UInt64 => field.AsULong,
        AssetValueType.UInt8 or AssetValueType.UInt16 or AssetValueType.UInt32 => (long)field.AsUInt,
        AssetValueType.Bool => field.AsBool ? 1L : 0L,
        _ => (long)field.AsInt,
    };

    /// <summary>Every field path of a typetree ("m_Color", "m_Color.r", ...); lists are leaves (nnnotes flat_names).</summary>
    public static List<string> FlatNames(object? tree, string prefix = "")
    {
        var output = new List<string>();
        if (tree is PyObject o)
            foreach (var (key, value) in o)
            {
                var path = prefix.Length > 0 ? $"{prefix}.{key}" : key;
                output.Add(path);
                output.AddRange(FlatNames(value, path));
            }
        return output;
    }

    public static PyObject Obj(object? value) => value as PyObject ?? throw new InvalidDataException($"Expected an object, got {value?.GetType().Name ?? "null"}");
    public static List<object?> List(object? value) => value as List<object?> ?? throw new InvalidDataException($"Expected a list, got {value?.GetType().Name ?? "null"}");
    public static long Long(object? value) => value switch
    {
        long l => l, ulong u => checked((long)u), int i => i, bool b => b ? 1 : 0,
        _ => throw new InvalidDataException($"Expected an integer, got {value?.GetType().Name ?? "null"}"),
    };
    public static string Str(object? value) => value as string ?? throw new InvalidDataException($"Expected a string, got {value?.GetType().Name ?? "null"}");
    public static bool Truthy(object? value) => value switch
    {
        null => false, bool b => b, long l => l != 0, ulong u => u != 0, double d => d != 0, string s => s.Length > 0,
        PyObject o => o.Count > 0, System.Collections.ICollection c => c.Count > 0, _ => true,
    };
    public static bool IsPPtr(object? value) => value is PyObject o && o.Count == 2 && o.ContainsKey("m_FileID") && o.ContainsKey("m_PathID");
}
