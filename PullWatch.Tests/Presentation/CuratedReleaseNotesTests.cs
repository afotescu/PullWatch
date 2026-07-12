namespace PullWatch.Tests;

public sealed class CuratedReleaseNotesTests
{
    [Fact]
    public void ParsesCuratedHeadingsAndBothBulletMarkers()
    {
        const string markdown = """
            # PullWatch release

            ## What's new

            * Added reliable playback.
            * Added persisted audio settings.

            ## Fixes

            - Fixed playback resets.
            - Fixed first-frame flickering.
            """;

        var sections = CuratedReleaseNotesParser.Parse(markdown);

        Assert.Collection(
            sections,
            section =>
            {
                Assert.Equal("What's new", section.Heading);
                Assert.Equal(
                    ["Added reliable playback.", "Added persisted audio settings."],
                    section.Bullets
                );
            },
            section =>
            {
                Assert.Equal("Fixes", section.Heading);
                Assert.Equal(
                    ["Fixed playback resets.", "Fixed first-frame flickering."],
                    section.Bullets
                );
            }
        );
    }

    [Fact]
    public void JoinsIndentedBulletContinuationLines()
    {
        const string markdown = """
            ## Fixes

            - Fixed playback controls after a video reaches the end
              and is started again.
            """;

        var section = Assert.Single(CuratedReleaseNotesParser.Parse(markdown));

        Assert.Equal(
            "Fixed playback controls after a video reaches the end and is started again.",
            Assert.Single(section.Bullets)
        );
    }

    [Fact]
    public void OmitsUnsupportedContentAndSectionsWithoutBullets()
    {
        const string markdown = """
            ## Empty section

            A paragraph is not a supported bullet.

            ### Nested heading

            - This bullet does not belong to a supported section.
            """;

        var sections = CuratedReleaseNotesParser.Parse(markdown);

        Assert.Empty(sections);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyMarkdownHasNoContent(string? markdown)
    {
        Assert.Empty(CuratedReleaseNotesParser.Parse(markdown));
    }

    [Theory]
    [InlineData("0.6.3")]
    [InlineData("v0.6.3")]
    public void CreatesWhatsNewViewModelForCuratedContent(string version)
    {
        var viewModel = WhatsNewViewModel.Create(version, "## Fixes\n\n- Fixed playback.");

        Assert.NotNull(viewModel);
        Assert.Equal("What's new in PullWatch v0.6.3", viewModel.Heading);
    }

    [Fact]
    public void DoesNotCreateWhatsNewViewModelWithoutCuratedContent()
    {
        var viewModel = WhatsNewViewModel.Create("0.6.3", "A plain paragraph.");

        Assert.Null(viewModel);
    }
}
