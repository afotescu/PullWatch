using FluentMigrator;

namespace PullWatch;

[Migration(202606230001)]
public sealed class AddRaidEncounterPullNumber : Migration
{
    public override void Up()
    {
        Alter.Table("RecordingRaidEncounters").AddColumn("PullNumber").AsInt32().Nullable();
    }

    public override void Down()
    {
        Delete.Column("PullNumber").FromTable("RecordingRaidEncounters");
    }
}
