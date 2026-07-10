using FluentMigrator;

namespace PullWatch;

[Migration(202607100001)]
public sealed class RenameChallengeModeTimerLimitSecondsToMythicRatingAfterRun : Migration
{
    public override void Up()
    {
        Rename
            .Column("TimerLimitSeconds")
            .OnTable("RecordingChallengeModes")
            .To("MythicRatingAfterRun");
    }

    public override void Down()
    {
        Rename
            .Column("MythicRatingAfterRun")
            .OnTable("RecordingChallengeModes")
            .To("TimerLimitSeconds");
    }
}
