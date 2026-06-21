using FluentMigrator;

namespace PullWatch;

[Migration(202606210002)]
public sealed class CreateRecordingChallengeModes : Migration
{
    private const string UtcNowSql = "(strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))";

    public override void Up()
    {
        Execute.Sql(
            $"""
            CREATE TABLE RecordingChallengeModes (
                RecordingId TEXT NOT NULL PRIMARY KEY,
                CreatedAtUtc TEXT NOT NULL DEFAULT {UtcNowSql},
                UpdatedAtUtc TEXT NOT NULL DEFAULT {UtcNowSql},
                DungeonName TEXT NOT NULL,
                MapId INTEGER NOT NULL,
                ChallengeModeId INTEGER NOT NULL,
                KeystoneLevel INTEGER NOT NULL,
                AffixIdsJson TEXT NOT NULL,
                ChallengeStartedAtUtc TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                ChallengeEndedAtUtc TEXT NULL,
                TotalTimeMilliseconds INTEGER NULL,
                OnTimeSeconds REAL NULL,
                TimerLimitSeconds INTEGER NULL,
                CONSTRAINT FK_RecordingChallengeModes_Recordings_RecordingId
                    FOREIGN KEY (RecordingId)
                    REFERENCES Recordings (Id)
                    ON DELETE CASCADE
            );
            """
        );

        Create
            .Index("IX_RecordingChallengeModes_ChallengeModeId")
            .OnTable("RecordingChallengeModes")
            .OnColumn("ChallengeModeId")
            .Ascending();

        Execute.Sql(
            $"""
            CREATE TRIGGER TR_RecordingChallengeModes_SetUpdatedAtUtc
            AFTER UPDATE ON RecordingChallengeModes
            FOR EACH ROW
            WHEN NEW.UpdatedAtUtc = OLD.UpdatedAtUtc
            BEGIN
                UPDATE RecordingChallengeModes
                SET UpdatedAtUtc = {UtcNowSql}
                WHERE RecordingId = OLD.RecordingId;
            END;
            """
        );
    }

    public override void Down()
    {
        Execute.Sql("DROP TRIGGER IF EXISTS TR_RecordingChallengeModes_SetUpdatedAtUtc;");
        Delete.Table("RecordingChallengeModes");
    }
}
