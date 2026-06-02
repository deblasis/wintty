namespace Ghostty.Core.Config;

/// <summary>
/// Pure-logic operations on ghostty config file lines.
/// Works on string arrays (the caller handles file I/O).
/// Ghostty uses last-wins semantics for duplicate keys.
/// </summary>
public static class ConfigFileParser
{
    /// <summary>
    /// Find the index of the last uncommented line matching the key.
    /// Returns -1 if not found.
    /// </summary>
    public static int FindLastUncommented(string[] lines, string key)
    {
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var parsed = ConfigLine.Parse(lines[i]);
            if (!parsed.IsComment && parsed.Key == key)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Set a value for a key. Replaces the last uncommented occurrence
    /// or appends at the end if the key doesn't exist.
    /// </summary>
    public static string[] SetValue(string[] lines, string key, string value)
    {
        var index = FindLastUncommented(lines, key);
        var result = new string[index >= 0 ? lines.Length : lines.Length + 1];
        Array.Copy(lines, result, lines.Length);

        if (index >= 0)
        {
            result[index] = $"{key} = {value}";
        }
        else
        {
            result[lines.Length] = $"{key} = {value}";
        }
        return result;
    }

    /// <summary>
    /// Comment out all uncommented occurrences of a key.
    /// </summary>
    public static string[] RemoveValue(string[] lines, string key)
    {
        var result = new string[lines.Length];
        Array.Copy(lines, result, lines.Length);

        for (int i = 0; i < result.Length; i++)
        {
            var parsed = ConfigLine.Parse(result[i]);
            if (!parsed.IsComment && parsed.Key == key)
                result[i] = "# " + result[i];
        }
        return result;
    }

    /// <summary>
    /// Append a keybind line. Keybindings accumulate (don't override).
    /// </summary>
    public static string[] SetKeybind(string[] lines, string bindingValue)
    {
        var result = new string[lines.Length + 1];
        Array.Copy(lines, result, lines.Length);
        result[lines.Length] = $"keybind = {bindingValue}";
        return result;
    }

    /// <summary>
    /// Returns the value (RHS of the first '=') of every uncommented line for
    /// the given key, in file order. Mirrors <see cref="SetRepeatableValues"/>'s
    /// view of which lines count for a key (uses <see cref="ConfigLine.Parse"/>).
    /// </summary>
    public static string[] GetRepeatableValues(string[] lines, string key)
    {
        var values = new List<string>();
        foreach (var line in lines)
        {
            var parsed = ConfigLine.Parse(line);
            if (parsed.IsComment || parsed.IsEmpty || parsed.Key != key)
                continue;
            values.Add((parsed.Value ?? string.Empty).TrimEnd());
        }

        return values.ToArray();
    }

    /// <summary>
    /// Replace all occurrences of a repeatable key with new values.
    /// Comments out existing lines and appends new ones.
    /// </summary>
    public static string[] SetRepeatableValues(string[] lines, string key, string[] values)
    {
        var result = RemoveValue(lines, key);
        var newResult = new string[result.Length + values.Length];
        Array.Copy(result, newResult, result.Length);
        for (int i = 0; i < values.Length; i++)
            newResult[result.Length + i] = $"{key} = {values[i]}";
        return newResult;
    }
}
