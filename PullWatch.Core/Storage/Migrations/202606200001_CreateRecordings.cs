using FluentMigrator;

namespace PullWatch;

[Migration(202606200001)]
public sealed class CreateRecordings : Migration
{
    private const string UtcNowSql = "(strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))";
    private static readonly RawSql UtcNowDefault = RawSql.Insert(UtcNowSql);

    public override void Up()
    {
        // csharpier-ignore
        Create
            .Table("Recordings")
            .WithColumn("Id").AsString().NotNullable().PrimaryKey()
            .WithColumn("CreatedAtUtc").AsString().NotNullable().WithDefaultValue(UtcNowDefault)
            .WithColumn("UpdatedAtUtc").AsString().NotNullable().WithDefaultValue(UtcNowDefault)
            .WithColumn("FilePath").AsString().NotNullable()
            .WithColumn("Status").AsString().NotNullable()
            .WithColumn("Kind").AsString().NotNullable()
            .WithColumn("StartedAtUtc").AsString().Nullable()
            .WithColumn("EndedAtUtc").AsString().Nullable()
            .WithColumn("FileSizeBytes").AsInt64().Nullable()
            .WithColumn("FileModifiedAtUtc").AsString().Nullable();

        Create
            .Index("IX_Recordings_FilePath")
            .OnTable("Recordings")
            .OnColumn("FilePath")
            .Ascending();
        Create
            .Index("IX_Recordings_CreatedAtUtc")
            .OnTable("Recordings")
            .OnColumn("CreatedAtUtc")
            .Ascending();

        Execute.Sql(
            $"""
            CREATE TRIGGER TR_Recordings_SetUpdatedAtUtc
            AFTER UPDATE ON Recordings
            FOR EACH ROW
            WHEN NEW.UpdatedAtUtc = OLD.UpdatedAtUtc
            BEGIN
                UPDATE Recordings
                SET UpdatedAtUtc = {UtcNowSql}
                WHERE Id = OLD.Id;
            END;
            """
        );
    }

    public override void Down()
    {
        Execute.Sql("DROP TRIGGER IF EXISTS TR_Recordings_SetUpdatedAtUtc;");
        Delete.Table("Recordings");
    }
}
