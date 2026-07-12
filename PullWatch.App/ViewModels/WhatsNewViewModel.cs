namespace PullWatch;

internal sealed class WhatsNewViewModel
{
    private WhatsNewViewModel(string heading, IReadOnlyList<CuratedReleaseNotesSection> sections)
    {
        Heading = heading;
        Sections = sections;
    }

    public string Heading { get; }

    public IReadOnlyList<CuratedReleaseNotesSection> Sections { get; }

    public static WhatsNewViewModel? Create(string version, string? markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var sections = CuratedReleaseNotesParser.Parse(markdown);
        if (sections.Count == 0)
        {
            return null;
        }

        var trimmedVersion = version.Trim();
        var versionLabel = trimmedVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmedVersion
            : $"v{trimmedVersion}";

        return new WhatsNewViewModel($"What's new in PullWatch {versionLabel}", sections);
    }
}
