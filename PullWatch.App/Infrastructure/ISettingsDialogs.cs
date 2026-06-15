namespace PullWatch;

public interface ISettingsDialogs
{
    string? PickFolder(string title, string? initialDirectory);

    bool SaveBeforeLeavingSettings();
}
