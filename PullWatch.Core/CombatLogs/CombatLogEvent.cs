namespace PullWatch;

public sealed class CombatLogEvent(string name, int argumentsStart, string rawLine)
{
    private IReadOnlyList<string>? _arguments;

    public string Name { get; } = name;

    public string RawLine { get; } = rawLine;

    public IReadOnlyList<string> Arguments => _arguments ??= ParseFields(
        argumentsStart < RawLine.Length
            ? RawLine.AsSpan(argumentsStart)
            : []);

    private static IReadOnlyList<string> ParseFields(ReadOnlySpan<char> value)
    {
        var fields = new List<string>();
        var fieldStart = 0;
        var inQuotes = false;
        var bracketDepth = 0;

        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case '[' when !inQuotes:
                    bracketDepth++;
                    break;
                case ']' when !inQuotes && bracketDepth > 0:
                    bracketDepth--;
                    break;
                case ',' when !inQuotes && bracketDepth == 0:
                    fields.Add(Unquote(value[fieldStart..index]));
                    fieldStart = index + 1;
                    break;
            }
        }

        if (!value.IsEmpty)
        {
            fields.Add(Unquote(value[fieldStart..]));
        }

        return fields;
    }

    private static string Unquote(ReadOnlySpan<char> value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].ToString()
            : value.ToString();
    }
}
