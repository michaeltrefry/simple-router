using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleRouter.Routing;

namespace SimpleRouter.Dashboard;

/// <summary>The local model form's effort fields (mode, comma-separated levels, kwargs JSON, default level) to and from an <see cref="EffortMapping"/>.</summary>
public static class EffortInput
{
    public static readonly (EffortMode Mode, string Name)[] Modes =
        [(EffortMode.None, "none"), (EffortMode.ReasoningEffort, "reasoning_effort"), (EffortMode.ChatTemplateKwargs, "chat_template_kwargs")];

    public static (string Mode, string Levels, string Kwargs, string Default) Format(EffortMapping? effort) => (
        Modes.First(m => m.Mode == (effort?.Mode ?? EffortMode.None)).Name,
        string.Join(", ", (effort?.Levels ?? []).Select(Effort.Name)),
        effort?.Kwargs is { Count: > 0 } kwargs ? JsonSerializer.Serialize(kwargs, Effort.Json) : "",
        effort?.Default is { } level ? Effort.Name(level) : "");

    /// <summary>Null when nothing is set (the provider's defaults).</summary>
    public static EffortMapping? Parse(string mode, string levels, string kwargs, string defaultLevel = "")
    {
        var parsedMode = Modes.FirstOrDefault(m => m.Name == mode) is { Name: not null } match
            ? match.Mode
            : throw new SettingsException($"Unknown effort mode '{mode}'.");
        var parsedLevels = levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => Effort.TryParse(l, out var level) ? level : throw new SettingsException($"Unknown effort level '{l}'."))
            .Distinct()
            .Order()
            .ToArray();
        Dictionary<string, JsonObject>? parsedKwargs = null;
        if (!string.IsNullOrWhiteSpace(kwargs))
        {
            try
            {
                parsedKwargs = JsonSerializer.Deserialize<Dictionary<string, JsonObject>>(kwargs, Effort.Json);
            }
            catch (JsonException)
            {
                throw new SettingsException("Kwargs must be a JSON object mapping each level to an object, e.g. {\"high\": {\"enable_thinking\": true}}.");
            }
        }
        EffortLevel? parsedDefault = string.IsNullOrWhiteSpace(defaultLevel) ? null
            : Effort.TryParse(defaultLevel, out var level) ? level
            : throw new SettingsException($"Unknown default effort level '{defaultLevel.Trim()}'.");
        return parsedMode == EffortMode.None && parsedLevels.Length == 0 && parsedKwargs is null && parsedDefault is null
            ? null
            : new EffortMapping(parsedMode, parsedLevels.Length == 0 ? null : parsedLevels, parsedKwargs, parsedDefault);
    }
}
