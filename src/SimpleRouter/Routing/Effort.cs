using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SimpleRouter.Routing;

/// <summary>The normalized reasoning-effort scale every target's levels are mapped onto, lowest first.</summary>
[JsonConverter(typeof(EffortLevelConverter))]
public enum EffortLevel
{
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>How a local model is told the effort: not at all, as <c>reasoning_effort</c>, or as per-level <c>chat_template_kwargs</c>.</summary>
[JsonConverter(typeof(EffortModeConverter))]
public enum EffortMode
{
    None,
    ReasoningEffort,
    ChatTemplateKwargs,
}

/// <summary>
/// A model's supported effort levels and, for a local model, how they are sent. <see cref="Kwargs"/> maps a level name to
/// the <c>chat_template_kwargs</c> object sent for it.
/// </summary>
public sealed record EffortMapping(
    EffortMode Mode = EffortMode.None,
    EffortLevel[]? Levels = null,
    Dictionary<string, JsonObject>? Kwargs = null);

public static class Effort
{
    /// <summary>The seed file's shape (camelCase), used wherever an <see cref="EffortMapping"/> is stored.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Codex models accept these unless the catalog lists their own; <c>max</c> only where a model lists it.</summary>
    public static readonly EffortLevel[] CodexDefaultLevels =
        [EffortLevel.Minimal, EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh];

    /// <summary>
    /// The client's effort: <c>output_config.effort</c> when set, else derived from an enabled thinking budget (below
    /// 4,096 tokens low, below 16,384 medium, below 32,768 high, below 65,536 xhigh, else max). Null when neither says
    /// anything (no effort, thinking absent, disabled or adaptive), so the target keeps its own default.
    /// </summary>
    public static EffortLevel? Read(JsonObject request)
    {
        if (request["output_config"]?["effort"] is JsonValue e && e.TryGetValue<string>(out var name) && TryParse(name, out var level))
        {
            return level;
        }
        if (request["thinking"] is not JsonObject thinking
            || thinking["type"] is not JsonValue t || !t.TryGetValue<string>(out var type) || type != "enabled"
            || thinking["budget_tokens"] is not JsonValue b || !b.TryGetValue<long>(out var budget))
        {
            return null;
        }
        return budget switch
        {
            < 4096 => EffortLevel.Low,
            < 16384 => EffortLevel.Medium,
            < 32768 => EffortLevel.High,
            < 65536 => EffortLevel.XHigh,
            _ => EffortLevel.Max,
        };
    }

    /// <summary>The supported level closest to <paramref name="requested"/>; on a tie the lower one. Null when nothing is supported.</summary>
    public static EffortLevel? Nearest(EffortLevel requested, IReadOnlyCollection<EffortLevel> supported) =>
        supported.Count == 0
            ? null
            : supported.OrderBy(s => Math.Abs((int)s - (int)requested)).ThenBy(s => s).First();

    public static string Name(EffortLevel level) => level.ToString().ToLowerInvariant();

    public static bool TryParse(string? name, out EffortLevel level) =>
        Enum.TryParse(name, ignoreCase: true, out level) && Enum.IsDefined(level) && !int.TryParse(name, out _);
}

/// <summary>Reads and writes <see cref="EffortLevel"/> by its lowercase name (<c>xhigh</c>, not <c>x_high</c>).</summary>
public sealed class EffortLevelConverter : JsonConverter<EffortLevel>
{
    public override EffortLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Effort.TryParse(reader.GetString(), out var level) ? level : throw new JsonException($"Unknown effort level '{reader.GetString()}'.");

    public override void Write(Utf8JsonWriter writer, EffortLevel value, JsonSerializerOptions options) => writer.WriteStringValue(Effort.Name(value));
}

/// <summary>Reads and writes <see cref="EffortMode"/> as <c>none</c>, <c>reasoning_effort</c> or <c>chat_template_kwargs</c>.</summary>
public sealed class EffortModeConverter() : JsonStringEnumConverter<EffortMode>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);
