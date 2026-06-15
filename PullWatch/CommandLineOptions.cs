namespace PullWatch;

public sealed record CommandLineOptions(
    bool RecordNow,
    string? WowLogsDirectory,
    string? RecordingsDirectory,
    bool? RecordMythicPlus,
    bool? RecordRaidEncounters)
{
    public PullWatchSettings ApplyTo(PullWatchSettings settings)
    {
        return settings with
        {
            WowLogsDirectory = WowLogsDirectory ?? settings.WowLogsDirectory,
            RecordingsDirectory = RecordingsDirectory ?? settings.RecordingsDirectory,
            RecordMythicPlus = RecordMythicPlus ?? settings.RecordMythicPlus,
            RecordRaidEncounters = RecordRaidEncounters ?? settings.RecordRaidEncounters
        };
    }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out CommandLineOptions options,
        out string? error)
    {
        var recordNow = false;
        string? wowLogsDirectory = null;
        string? recordingsDirectory = null;
        bool? recordMythicPlus = null;
        bool? recordRaidEncounters = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            switch (argument)
            {
                case "--record-now":
                    recordNow = true;
                    break;
                case "--wow-logs-directory":
                    if (!TryReadValue(arguments, ref index, argument, out wowLogsDirectory, out error))
                    {
                        options = default!;
                        return false;
                    }

                    break;
                case "--recordings-directory":
                    if (!TryReadValue(arguments, ref index, argument, out recordingsDirectory, out error))
                    {
                        options = default!;
                        return false;
                    }

                    break;
                case "--record-mythic-plus":
                    if (!TryReadBoolean(arguments, ref index, argument, out recordMythicPlus, out error))
                    {
                        options = default!;
                        return false;
                    }

                    break;
                case "--record-raid-encounters":
                    if (!TryReadBoolean(arguments, ref index, argument, out recordRaidEncounters, out error))
                    {
                        options = default!;
                        return false;
                    }

                    break;
                default:
                    options = default!;
                    error = $"Unknown command-line option: {argument}";
                    return false;
            }
        }

        options = new CommandLineOptions(
            recordNow,
            wowLogsDirectory,
            recordingsDirectory,
            recordMythicPlus,
            recordRaidEncounters);
        error = null;
        return true;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (++index >= arguments.Count || arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            error = $"Command-line option {option} requires a value.";
            return false;
        }

        value = arguments[index];
        error = null;
        return true;
    }

    private static bool TryReadBoolean(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out bool? value,
        out string? error)
    {
        if (!TryReadValue(arguments, ref index, option, out var rawValue, out error))
        {
            value = null;
            return false;
        }

        if (!bool.TryParse(rawValue, out var parsed))
        {
            value = null;
            error = $"Command-line option {option} must be true or false.";
            return false;
        }

        value = parsed;
        return true;
    }
}
