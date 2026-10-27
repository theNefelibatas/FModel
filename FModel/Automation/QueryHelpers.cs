using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Automation;

public static class QueryHelpers
{
    public static string NormalizePath(string path) => (path ?? "").Replace('\\', '/').Trim('/');

    public static bool InDirectory(string path, string directory)
    {
        directory = NormalizePath(directory);
        return directory.Length == 0 || NormalizePath(path).StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static int Integer(JObject args, string key, int fallback, int min, int max)
    {
        var value = args.Value<long?>(key) ?? fallback;
        if (value < min || value > max) throw new ArgumentOutOfRangeException(key, $"{key} must be between {min} and {max}.");
        return (int) value;
    }

    public static object Page<T>(IEnumerable<T> values, JObject args)
    {
        var offset = Integer(args, "offset", 0, 0, int.MaxValue);
        var limit = Integer(args, "limit", 100, 1, 500);
        var items = new List<T>();
        var total = 0;
        foreach (var value in values)
        {
            if (total >= offset && items.Count < limit) items.Add(value);
            total++;
        }
        return new { total, offset, items, nextOffset = offset + items.Count < total ? (int?) (offset + items.Count) : null };
    }

    public static object Text(string text, JObject args)
    {
        if (text == null) throw new NotSupportedException("This file did not produce a text document. Use an asset export or preview operation.");
        var offset = Integer(args, "offset", 0, 0, int.MaxValue);
        var length = Integer(args, "length", 16000, 1, 65536);
        if (offset > text.Length) throw new ArgumentOutOfRangeException("offset", "Offset exceeds document length.");
        var content = text.Substring(offset, Math.Min(length, text.Length - offset));
        return new { totalCharacters = text.Length, offset, content, nextOffset = offset + content.Length < text.Length ? (int?) (offset + content.Length) : null };
    }

    public static object Json(object value, JObject args)
    {
        var text = JsonConvert.SerializeObject(value, Formatting.Indented);
        if (args.Value<string>("selector") is { Length: > 0 } selector)
        {
            var selected = JToken.Parse(text).SelectToken(selector, true);
            text = (selected ?? JValue.CreateNull()).ToString(Formatting.Indented);
        }
        return Text(text, args);
    }
}
