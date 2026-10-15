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
/// the <c>chat_template_kwargs</c> object sent for it. <see cref="Default"/> is what a local model is sent when the client
/// says nothing, for servers whose own default (e.g. thinking) is unwanted.
/// </summary>
public sealed record EffortMapping(
    EffortMode Mode = EffortMode.None,
    EffortLevel[]? Levels = null,
    Dictionary<string, JsonObject>? Kwargs = null,
    EffortLevel? Default = null);

public static class Effort
{
    /// <summary>The seed file's shape (camelCase), used wherever an <see cref="EffortMapping"/> is stored.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Codex models accept these unless the catalog lists their own; <c>none</c>, <c>minimal</c> and <c>max</c> only where a model lists them.</summary>
    public static readonly EffortLevel[] CodexDefaultLevels =
        [EffortLevel.Low, EffortLevel.Medium, EffortLevel.High, EffortLevel.XHigh];

    private static readonly Dictionary<string, EffortLevel> ByName =
        Enum.GetValues<EffortLevel>().ToDictionary(Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The client's effort: <c>output_config.effort</c> when set, else derived from an enabled thinking budget (below
    /// 4,096 tokens low, below 16,384 medium, below 32,768 high, below 65,536 xhigh, else max). Null when neither says
    /// anything (no effort, thinking absent or adaptive), so the target keeps its own default. Disabled thinking is
    /// <c>none</c>, so each target gets its lowest level.
    /// </summary>
    public static EffortLevel? Read(JsonObject request)
    {
        if (request["output_config"]?["effort"] is JsonValue e && e.TryGetValue<string>(out var name) && TryParse(name, out var level))
        {
            return level;
        }
        if (request["thinking"] is not JsonObject thinking || thinking["type"] is not JsonValue t || !t.TryGetValue<string>(out var type))
        {
            return null;
        }
        if (type == "disabled")
        {
            return EffortLevel.None;
        }
        if (type != "enabled"
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

    /// <summary>
    /// The level <paramref name="model"/> is sent for <paramref name="request"/>: the client's own for Claude (the request
    /// passes through), the nearest listed level for Codex and for a local model whose mapping sends one (a local model
    /// without the client's level gets its mapping's default), else null.
    /// </summary>
    public static EffortLevel? Sent(JsonObject request, Data.CatalogModel model)
    {
        var requested = Read(request);
        return model.Provider switch
        {
            ProviderKind.Claude => requested,
            ProviderKind.Codex => requested is { } level ? Nearest(level, model.Effort?.Levels is { Length: > 0 } levels ? levels : CodexDefaultLevels) : null,
            _ => model.Effort is { Mode: not EffortMode.None } mapping && (requested ?? mapping.Default) is { } local ? Nearest(local, mapping.Levels ?? []) : null,
        };
    }

    /// <summary>The supported level closest to <paramref name="requested"/>; on a tie the lower one. Null when nothing is supported.</summary>
    public static EffortLevel? Nearest(EffortLevel requested, IReadOnlyCollection<EffortLevel> supported) =>
        supported.Count == 0
            ? null
            : supported.OrderBy(s => Math.Abs((int)s - (int)requested)).ThenBy(s => s).First();

    public static string Name(EffortLevel level) => level.ToString().ToLowerInvariant();

    public static bool TryParse(string? name, out EffortLevel level)
    {
        level = default;
        return name is not null && ByName.TryGetValue(name.Trim(), out level);
    }

    /// <summary>
    /// Throws when a <c>chat_template_kwargs</c> mapping lists a level without kwargs, which would otherwise send nothing.
    /// </summary>
    public static void Validate(EffortMapping? mapping, string modelId)
    {
        if (mapping is not { Mode: EffortMode.ChatTemplateKwargs })
        {
            return;
        }
        var missing = (mapping.Levels ?? []).Where(l => mapping.Kwargs?.ContainsKey(Name(l)) != true).Select(Name).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Model '{modelId}' effort mapping lists {string.Join(", ", missing)} without kwargs.");
        }
    }

    /// <summary>A stored mapping, or null when it cannot be read, so one bad row never fails every catalog query.</summary>
    public static EffortMapping? Deserialize(string stored)
    {
        try
        {
            return JsonSerializer.Deserialize<EffortMapping>(stored, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
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
