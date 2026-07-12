using System.IO;

namespace PullWatch;

internal sealed record CuratedReleaseNotesSection(string Heading, IReadOnlyList<string> Bullets);

internal static class CuratedReleaseNotesParser
{
    public static IReadOnlyList<CuratedReleaseNotesSection> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var sections = new List<CuratedReleaseNotesSection>();
        string? currentHeading = null;
        List<string>? currentBullets = null;

        using var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.Length == 0)
            {
                continue;
            }

            if (TryParseHeading(trimmedLine, out var heading))
            {
                AddCurrentSection();
                currentHeading = heading;
                currentBullets = [];
                continue;
            }

            if (trimmedLine.StartsWith('#'))
            {
                AddCurrentSection();
                currentHeading = null;
                currentBullets = null;
                continue;
            }

            if (currentBullets is not null && TryParseBullet(trimmedLine, out var bullet))
            {
                currentBullets.Add(bullet);
                continue;
            }

            if (currentBullets is { Count: > 0 } && char.IsWhiteSpace(line[0]))
            {
                currentBullets[^1] = $"{currentBullets[^1]} {trimmedLine}";
            }
        }

        AddCurrentSection();
        return [.. sections];

        void AddCurrentSection()
        {
            if (currentHeading is null || currentBullets is not { Count: > 0 })
            {
                return;
            }

            sections.Add(new CuratedReleaseNotesSection(currentHeading, [.. currentBullets]));
        }
    }

    private static bool TryParseHeading(string line, out string heading)
    {
        const string prefix = "## ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            heading = string.Empty;
            return false;
        }

        heading = line[prefix.Length..].Trim();
        return heading.Length > 0;
    }

    private static bool TryParseBullet(string line, out string bullet)
    {
        if (line.Length < 3 || line[0] is not ('*' or '-') || !char.IsWhiteSpace(line[1]))
        {
            bullet = string.Empty;
            return false;
        }

        bullet = line[2..].Trim();
        return bullet.Length > 0;
    }
}
