using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BR_Libretro;

internal static class RomTitle
{
    private static readonly HashSet<string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        "World", "USA", "US", "Europe", "EU", "Japan", "JP", "Asia", "Australia", "Canada",
        "Brazil", "China", "France", "Germany", "Hong Kong", "Italy", "Korea", "Netherlands",
        "Russia", "Spain", "Sweden", "Taiwan", "UK", "United Kingdom"
    };

    private static readonly Regex TrailingGroup = new(@"\s*[\(\[]([^\)\]]+)[\)\]]\s*$", RegexOptions.Compiled);
    private static readonly Regex Language = new(@"^[A-Za-z]{2}(?:-[A-Za-z]{2})?$", RegexOptions.Compiled);
    private static readonly Regex ReleaseTag = new(
        @"^(?:Rev(?:ision)?\s*[A-Za-z0-9.]+|v(?:ersion)?\s*[0-9.]+|Beta(?:\s*\d+)?|Proto(?:type)?(?:\s*\d+)?|Demo|Sample|Unl(?:icensed)?|Pirate|Alt(?:ernate)?(?:\s*\d+)?|Disc\s*\d+|Disk\s*\d+|Side\s*[A-Za-z0-9]+|SGB\s*Enhanced|GB\s*Compatible|Rumble\s*Version)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NoIntroArticle = new(
        @"^(?<name>.+),\s*(?<article>The|A|An)(?<subtitle>\s+-\s+.+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string Clean(string title, string fallback = "ROM")
    {
        string value = Regex.Replace((title ?? string.Empty).Replace('_', ' '), @"\s+", " ").Trim(' ', '-', '_');
        while (!string.IsNullOrWhiteSpace(value))
        {
            Match match = TrailingGroup.Match(value);
            if (!match.Success || !IsMetadataGroup(match.Groups[1].Value)) break;
            value = value.Substring(0, match.Index).TrimEnd(' ', '-', '_');
        }
        if (!string.IsNullOrWhiteSpace(value))
        {
            Match article = NoIntroArticle.Match(value);
            if (article.Success)
            {
                string subtitle = article.Groups["subtitle"].Value;
                if (!string.IsNullOrWhiteSpace(subtitle)) subtitle = ": " + subtitle.Substring(subtitle.IndexOf('-') + 1).Trim();
                value = article.Groups["article"].Value + " " + article.Groups["name"].Value.Trim() + subtitle;
            }
        }
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    internal static string Canonical(string title)
    {
        string value = Clean(title, string.Empty).Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    private static bool IsMetadataGroup(string group)
    {
        string value = group.Trim();
        if (ReleaseTag.IsMatch(value)) return true;

        string[] parts = value.Split(',');
        if (parts.Length == 0) return false;
        foreach (string part in parts)
        {
            string token = part.Trim();
            if (!Regions.Contains(token) && !Language.IsMatch(token)) return false;
        }
        return true;
    }
}
