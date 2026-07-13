using FluentMigrator;

namespace PullWatch;

[Migration(202607130001)]
public sealed class AddRecordingIsFavorite : Migration
{
    public override void Up()
    {
        Alter
            .Table("Recordings")
            .AddColumn("IsFavorite")
            .AsBoolean()
            .NotNullable()
            .WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("IsFavorite").FromTable("Recordings");
    }
}
