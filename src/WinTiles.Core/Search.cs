namespace WinTiles.Core;

/// <summary>An installed app. Id is its Start-menu ID (shell:AppsFolder parsing name) or a file path.</summary>
public sealed record AppInfo(string Id, string Name);

/// <summary>"Start typing" search, ranked like the Windows 8 search charm.</summary>
public static class AppSearch
{
    public static IReadOnlyList<AppInfo> Rank(string query, IEnumerable<AppInfo> apps)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return Array.Empty<AppInfo>();
        var scored = new List<(int Score, AppInfo App)>();
        foreach (var app in apps)
        {
            var n = app.Name.ToLowerInvariant();
            int? score =
                n == q ? 0 :
                n.StartsWith(q, StringComparison.Ordinal) ? 1 :
                n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(q, StringComparison.Ordinal)) ? 2 :
                n.Contains(q, StringComparison.Ordinal) ? 3 :
                IsSubsequence(q, n) ? 4 :     // "vsc" → Visual Studio Code
                null;
            if (score is { } s) scored.Add((s, app));
        }
        return scored
            .OrderBy(x => x.Score)
            .ThenBy(x => x.App.Name.Length)
            .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.App)
            .ToList();
    }

    private static bool IsSubsequence(string needle, string hay)
    {
        var i = 0;
        foreach (var ch in hay)
            if (i < needle.Length && ch == needle[i]) i++;
        return i == needle.Length;
    }

    /// <summary>A–Z heading for the All apps view.</summary>
    public static string Letter(string name)
    {
        var folded = RemoveDiacritics(name);
        return folded.Length > 0 && char.IsLetter(folded[0]) ? char.ToUpperInvariant(folded[0]).ToString() : "#";
    }

    private static string RemoveDiacritics(string s)
    {
        var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray());
    }
}

/// <summary>
/// Windows 8 search: with Start focused, just type. There's no search box; text input goes
/// straight into the query and the results replace the tiles.
/// </summary>
public static class TypeToSearch
{
    public enum Kind { Append, DeleteBackward, DeleteWord, Clear, Submit, Ignore }

    public readonly record struct Action(Kind Kind, string Text = "");

    public enum Key { Backspace, Enter, Other }

    /// <summary>Typed characters (from WPF's TextInput, so dead keys and IMEs already work).</summary>
    public static Action ForText(string? text, string query)
    {
        if (string.IsNullOrEmpty(text)) return new(Kind.Ignore);
        var typed = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (typed.Length == 0) return new(Kind.Ignore);
        // A leading space would just look empty; ignore it until there's something to search.
        if (query.Length == 0 && string.IsNullOrWhiteSpace(typed)) return new(Kind.Ignore);
        return new(Kind.Append, typed);
    }

    /// <summary>Editing keys. Backspace erases a letter, Ctrl+Backspace a word, Ctrl+Shift+Backspace everything.</summary>
    public static Action ForKey(Key key, bool ctrl, bool shift, bool alt, string query) => key switch
    {
        Key.Backspace when query.Length == 0 => new(Kind.Ignore),
        Key.Backspace when ctrl && shift => new(Kind.Clear),
        Key.Backspace when ctrl || alt => new(Kind.DeleteWord),
        Key.Backspace => new(Kind.DeleteBackward),
        Key.Enter when query.Length == 0 || ctrl || alt => new(Kind.Ignore),
        Key.Enter => new(Kind.Submit),
        _ => new(Kind.Ignore),
    };

    public static string Apply(Action action, string query)
    {
        switch (action.Kind)
        {
            case Kind.Append:
                return query + action.Text;
            case Kind.DeleteBackward:
                if (query.Length == 0) return query;
                // Don't split a surrogate pair (emoji etc.).
                var cut = query.Length >= 2 && char.IsLowSurrogate(query[^1]) && char.IsHighSurrogate(query[^2]) ? 2 : 1;
                return query[..^cut];
            case Kind.DeleteWord:
                var q = query.TrimEnd(' ');
                var space = q.LastIndexOf(' ');
                return space < 0 ? "" : q[..(space + 1)];
            case Kind.Clear:
                return "";
            default:
                return query;
        }
    }
}
