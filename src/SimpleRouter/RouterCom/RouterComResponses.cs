using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using SimpleRouter.Api;
using SimpleRouter.Codex;
using SimpleRouter.Local;
using SimpleRouter.Routing;
using SimpleRouter.Usage;

namespace SimpleRouter.RouterCom;

/// <summary>Shares Responses translation with Codex while keeping credentials, reasoning and errors separate.</summary>
public sealed class RouterComResponses(RouterOptions options, TimeProvider time)
{
    public const string SignaturePrefix = "routercom:";

    public async Task<UpstreamResult> SendAsync(UpstreamCall call, HttpResponseMessage response)
    {
        var context = call.Context;
        var ct = context.RequestAborted;
        var streaming = call.Request["stream"] is JsonValue value && value.TryGetValue<bool>(out var requested) && requested;
        var translator = new ResponsesStreamTranslator(call.Model.Id, call.EstimatedInputTokens, SignaturePrefix);
        var builder = new AnthropicMessageBuilder();
        var start = translator.Start();
        builder.Apply(start);
        if (!streaming)
        {
            try
            {
                var raw = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct) as JsonObject;
                var status = ChatRequestTranslator.Str(raw?["status"]);
                if (raw is null || status is not ("completed" or "incomplete") || raw["output"] is not JsonArray)
                {
                    RequestDiagnostics.Fail(context, "malformed_response");
                    return UpstreamResult.NotServed(new ModelFault(FaultKind.Overloaded));
                }
                UpstreamUsageMetadata.Capture(context, raw);
                translator.Feed(new JsonObject { ["type"] = "response." + status, ["response"] = raw }).ForEach(builder.Apply);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or IOException or HttpRequestException)
            {
                RequestDiagnostics.Fail(context, "malformed_response");
                return UpstreamResult.NotServed(new ModelFault(FaultKind.Overloaded));
            }
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(builder.Message, WireJson.Relaxed), ct);
            return UpstreamResult.Served;
        }
        var started = false;
        ModelFault? fault = null;
        await using var output = new StreamKeepAlive(context.Response.Body, options.Limits.KeepAliveInterval, time, ct);
        async Task Emit(List<JsonObject> events)
        {
            if (!started)
            {
                if (events.Count == 0 || fault is not null) return;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                events.Insert(0, start);
                started = true;
            }
            await output.WriteAsync(Encoding.UTF8.GetBytes(ResponsesStreamTranslator.Format(events)), translator.Ended);
        }
        async Task Fail(string kind, FaultKind failure)
        {
            RequestDiagnostics.Fail(context, kind);
            fault = new ModelFault(failure, LimitHeaders.RetryAfter(response.Headers, time.GetUtcNow()));
            await Emit(translator.Abort("Router.com response could not be completed."));
        }
        var grace = new StreamStartGrace(options.Limits.StreamStartGrace, time, async () =>
        {
            if (!started) await Emit([new JsonObject { ["type"] = "ping" }]);
        });
        try
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var data = new StringBuilder();
            while (!translator.Ended)
            {
                string? line;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(options.RouterCom.StreamIdleTimeout);
                    var read = reader.ReadLineAsync(idle.Token).AsTask();
                    line = await grace.AwaitAsync(read, ct);
                }
                if (line is { Length: > 0 })
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line[5..].TrimStart(' '));
                    if (data.Length > 8 * 1024 * 1024) { await Fail("malformed_response", FaultKind.Overloaded); break; }
                    continue;
                }
                if (data.Length > 0)
                {
                    JsonObject? frame;
                    try { frame = JsonNode.Parse(data.ToString()) as JsonObject; }
                    catch (JsonException) { frame = null; }
                    data.Clear();
                    if (frame is null || ChatRequestTranslator.Str(frame["type"]) is not { } type
                        || (type is "response.completed" or "response.incomplete" && frame["response"] is not JsonObject))
                    {
                        await Fail("malformed_response", FaultKind.Overloaded);
                        break;
                    }
                    if (frame["response"] is JsonObject raw) UpstreamUsageMetadata.Capture(context, raw);
                    if (type is "error" or "response.failed")
                    {
                        var error = frame["response"]?["error"] as JsonObject ?? frame["error"] as JsonObject ?? frame;
                        var code = ChatRequestTranslator.Str(error["code"]) ?? ChatRequestTranslator.Str(error["type"]);
                        var failure = code == "context_length_exceeded" || ContextOverflow.IsReported(ChatRequestTranslator.Str(error["message"]))
                            ? FaultKind.ContextOverflow : code is "rate_limit_exceeded" or "rate_limit_error" ? FaultKind.Limited
                            : code is "invalid_api_key" or "api_key_deactivated" or "insufficient_credits" or "budget_exceeded" or "billing_account_locked" ? FaultKind.AuthRejected
                            : code is "model_not_found" or "provider_key_required" or "not_implemented" ? FaultKind.PlanRejected : FaultKind.Overloaded;
                        await Fail("upstream_error", failure);
                        break;
                    }
                    List<JsonObject> events;
                    try { events = translator.Feed(frame); }
                    catch (InvalidOperationException)
                    {
                        await Fail("malformed_response", FaultKind.Overloaded);
                        break;
                    }
                    await Emit(events);
                }
                if (line is null && !translator.Ended) await Fail("truncated_stream", FaultKind.Overloaded);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await Fail("upstream_timeout", FaultKind.Unreachable);
        }
        catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
        {
            await Fail("transport_error", FaultKind.Unreachable);
        }
        if (fault is not null) return started ? UpstreamResult.ServedWith(fault) : UpstreamResult.NotServed(fault);
        return UpstreamResult.Served;
    }
}
