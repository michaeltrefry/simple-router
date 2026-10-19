using System.Globalization;
using SimpleRouter.Routing;
using SimpleRouter.OpenRouter;

namespace SimpleRouter.Dashboard;

/// <summary>Display text and status-pill styles shared by the dashboard panels.</summary>
public static class DashboardFormat
{
    public static string PinUrl(string sessionKey, ModelTier tier, long? beforeId = null) =>
        $"/ui/pins?sessionKey={Uri.EscapeDataString(sessionKey)}&tier={tier.Name()}" +
        (beforeId is { } id ? $"&beforeId={id.ToString(CultureInfo.InvariantCulture)}" : "");

    public static string RequestUrl(long id) => $"/ui/requests/{id.ToString(CultureInfo.InvariantCulture)}";

    public static string? ActualModel(RequestRow row) => row.Provider == ProviderKind.OpenRouter
        && row.ResolvedModelId is { } model && OpenRouterRoutingOptions.IsAuto(model) ? null : row.ResolvedModelId;

    public static string OutcomeLabel(string? outcome) => outcome switch
    {
        "completed" => "Completed",
        "failed" => "Failed",
        "client_cancelled" => "Client cancelled",
        _ => "Unknown",
    };

    public static string Time(DateTimeOffset? at) => at is { } value ? value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : "—";

    public static string Cost(long micros) => (micros / 1_000_000m).ToString("$0.0000", CultureInfo.InvariantCulture);

    /// <summary>Dollars for chart axes and totals: cents, or four places under a cent.</summary>
    public static string Dollars(double usd) => usd.ToString(usd is > 0 and < 0.01 ? "$0.0000" : "$#,0.00", CultureInfo.InvariantCulture);

    public static string Compact(long count) => count switch
    {
        >= 1_000_000 => (count / 1_000_000d).ToString("0.#M", CultureInfo.InvariantCulture),
        >= 1_000 => (count / 1_000d).ToString("0.#K", CultureInfo.InvariantCulture),
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    public static string Latency(long ms) => ms < 1000
        ? $"{ms} ms"
        : (ms / 1000d).ToString("0.0 s", CultureInfo.InvariantCulture);

    public static string Tokens(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Session keys are hashes; a prefix is enough to tell pins apart.</summary>
    public static string ShortKey(string key) => key.Length <= 12 ? key : key[..12];

    public static string Pill(ModelStatus status) => status switch
    {
        ModelStatus.Eligible => "pill ok",
        ModelStatus.Cooling => "pill warn",
        ModelStatus.Disabled or ModelStatus.NotLoggedIn => "pill muted",
        _ => "pill bad",
    };

    public static string Pill(int httpStatus) => httpStatus switch
    {
        < 400 => "pill ok",
        429 => "pill warn",
        _ => "pill bad",
    };

    public static string AccountPill(string state) => state switch
    {
        "ok" => "pill ok",
        "cooling" => "pill warn",
        "auth_rejected" => "pill bad",
        _ => "pill muted",
    };

    public static string AccountLabel(string state) => state switch
    {
        "ok" => "ok",
        "cooling" => "cooling",
        "auth_rejected" => "login needed (auth rejected)",
        "not_logged_in" => "not logged in",
        _ => "no routable models",
    };
}
