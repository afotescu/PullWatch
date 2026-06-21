using FluentMigrator;

namespace PullWatch;

[Migration(202606210001)]
public sealed class CreateRecordingRaidEncounters : Migration
{
    private const string UtcNowSql = "(strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))";

    public override void Up()
    {
        Execute.Sql(
            $"""
            CREATE TABLE RecordingRaidEncounters (
                RecordingId TEXT NOT NULL PRIMARY KEY,
                CreatedAtUtc TEXT NOT NULL DEFAULT {UtcNowSql},
                UpdatedAtUtc TEXT NOT NULL DEFAULT {UtcNowSql},
                EncounterId INTEGER NOT NULL,
                EncounterName TEXT NOT NULL,
                DifficultyId INTEGER NOT NULL,
                GroupSize INTEGER NULL,
                InstanceId INTEGER NULL,
                EncounterStartedAtUtc TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                EncounterEndedAtUtc TEXT NULL,
                DurationMilliseconds INTEGER NULL,
                CONSTRAINT FK_RecordingRaidEncounters_Recordings_RecordingId
                    FOREIGN KEY (RecordingId)
                    REFERENCES Recordings (Id)
                    ON DELETE CASCADE
            );
            """
        );

        Create
            .Index("IX_RecordingRaidEncounters_EncounterId")
            .OnTable("RecordingRaidEncounters")
            .OnColumn("EncounterId")
            .Ascending();

        Execute.Sql(
            $"""
            CREATE TRIGGER TR_RecordingRaidEncounters_SetUpdatedAtUtc
            AFTER UPDATE ON RecordingRaidEncounters
            FOR EACH ROW
            WHEN NEW.UpdatedAtUtc = OLD.UpdatedAtUtc
            BEGIN
                UPDATE RecordingRaidEncounters
                SET UpdatedAtUtc = {UtcNowSql}
                WHERE RecordingId = OLD.RecordingId;
            END;
            """
        );
    }

    public override void Down()
    {
        Execute.Sql("DROP TRIGGER IF EXISTS TR_RecordingRaidEncounters_SetUpdatedAtUtc;");
        Delete.Table("RecordingRaidEncounters");
    }
}
