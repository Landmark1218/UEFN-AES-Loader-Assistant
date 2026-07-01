namespace UEFNMapInstaller;

/// <summary>
/// Lightweight INI editor that replaces only the specified key value
/// while preserving existing lines, comments, and order. Appends to the section if the key is absent,
/// or adds a new section at the end of the file if the section is missing.
/// </summary>
internal static class IniEditor
{
    /// <summary>
    /// Reads the file (or creates it from <paramref name=\"template\"/> if absent),
    /// sets the specified (section, key) to value, and saves the file.
    /// </summary>
    public static void SetValue(string path, string section, string key, string value, string? template = null)
    {
        List<string> lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : (template ?? "").Replace("\r\n", "\n").Split('\n').ToList();

        SetValue(lines, section, key, value);
        File.WriteAllText(path, string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n");
    }

    public static void SetValue(List<string> lines, string section, string key, string value)
    {
        var keyLine = $"{key}={value}";

        int secIdx = FindSectionIndex(lines, section);
        if (secIdx < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(keyLine);
            return;
        }

        int end = NextSectionIndex(lines, secIdx + 1);
        for (int i = secIdx + 1; i < end; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith(';') || t.StartsWith('#')) continue;

            int eq = t.IndexOf('=');
            if (eq <= 0) continue;

            var k = t[..eq].Trim();
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = keyLine;
                return;
            }
        }

        // Key not found in section -> insert before trailing blank lines
        int insertAt = end;
        while (insertAt - 1 > secIdx && lines[insertAt - 1].Trim().Length == 0)
            insertAt--;
        lines.Insert(insertAt, keyLine);
    }

    private static int FindSectionIndex(List<string> lines, string section)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (TryGetSectionName(lines[i], out var name)
                && string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static int NextSectionIndex(List<string> lines, int from)
    {
        for (int i = from; i < lines.Count; i++)
            if (TryGetSectionName(lines[i], out _))
                return i;
        return lines.Count;
    }

    private static bool TryGetSectionName(string line, out string name)
    {
        var t = line.Trim();
        if (t.Length >= 2 && t[0] == '[' && t[^1] == ']')
        {
            name = t[1..^1].Trim();
            return true;
        }
        name = "";
        return false;
    }
}
