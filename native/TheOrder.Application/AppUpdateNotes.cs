using System.Text.RegularExpressions;

namespace TheOrder.Application;

// Summarize the maintainer's published text locally. No LLM/API key, HTML or remote code.
public static partial class AppUpdateNotes
{
    public static string Highlights(string notes)
    {
        var lines = new List<string>(); bool fenced = false;
        foreach (var raw in notes.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (!fenced) lines.Add(line);
        }
        var section = new List<string>(); int headingLevel = 0; bool found = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim(); var heading = Heading().Match(line);
            if (heading.Success)
            {
                var title = heading.Groups[2].Value;
                if (!found && new[] { "更新内容", "主要更新", "主要改动", "新增与修复", "What's new", "Changelog", "Changes" }.Any(x => title.Contains(x,StringComparison.OrdinalIgnoreCase)))
                { found = true; headingLevel = heading.Groups[1].Length; continue; }
                if (found && heading.Groups[1].Length <= headingLevel) break;
            }
            if (found) section.Add(line);
        }
        var candidates = found ? section : lines;
        var bullets = candidates.Select(x => Bullet().Match(x.Trim())).Where(x => x.Success).Select(x => Clean(x.Groups[1].Value))
            .Where(x => x.Length > 0).Distinct().Take(6).ToArray();
        if (bullets.Length > 0) return string.Join("\n", bullets.Select(x => "• " + x));
        var paragraphs = candidates.Where(x => !Heading().IsMatch(x.Trim()) && !x.TrimStart().StartsWith('<'))
            .Select(Clean).Where(x => x.Length > 0).Take(2);
        var fallback = string.Join("\n",paragraphs);
        return fallback.Length > 0 ? fallback : "这个版本没有提供主要改动说明。";
    }
    private static string Clean(string text)
    {
        text = Link().Replace(text,"$1").Replace("**","").Replace("__","").Replace("`","").Trim();
        text = new string(text.Where(c => !char.IsControl(c)).Take(240).ToArray());
        return text;
    }
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^(?:[-*+]\s+|\d+[.)]\s+)(.+)$")] private static partial Regex Bullet();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex Link();
}
