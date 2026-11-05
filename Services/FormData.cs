public static class FormData
{
    public static IReadOnlyDictionary<string, string> Parse(string value)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        foreach (string pair in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Decode(parts[0]);
            string itemValue = parts.Length == 2 ? Decode(parts[1]) : string.Empty;
            values[key] = itemValue;
        }

        return values;
    }

    public static string? GetQueryValue(Uri uri, string key)
    {
        IReadOnlyDictionary<string, string> values = Parse(uri.Query.TrimStart('?'));
        return values.TryGetValue(key, out string? value) ? value : null;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
