using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamUnify;

internal sealed record LookupResult(string? Name, string? Source, string? Description);

internal sealed class SuggestionLookup
{
    private static readonly ConcurrentDictionary<string,HttpClient> Clients = new();
    internal const int MaxRequests = 4;
    private static readonly SemaphoreSlim Slots = new(MaxRequests, MaxRequests);
    private static readonly SemaphoreSlim MachinePace = new(1, 1);
    private static long nextMachineStart;
    private readonly Func<Uri,string?,CancellationToken,Task<JsonElement?>> readJson;

    internal SuggestionLookup() : this(ReadJsonAsync) { }

    internal SuggestionLookup(Func<Uri,string?,CancellationToken,Task<JsonElement?>> readJson) =>
        this.readJson = readJson;

    private static HttpClient ClientFor(string? proxy)
    {
        string key = proxy?.Trim() ?? "";
        return Clients.GetOrAdd(key, value =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = value.Length != 0,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            if (value.Length != 0)
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https" or "socks5" or "socks4" or "socks4a"))
                    throw new ArgumentException("请填写完整的代理地址，例如 http://127.0.0.1:7890。");
                handler.Proxy = new WebProxy(uri);
            }
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamUnify/2.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        });
    }

    private static async Task<JsonElement?> ReadJsonAsync(Uri uri, string? proxy, CancellationToken ct)
    {
        try
        {
            using var response = await ClientFor(proxy).GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var field)
            || field.ValueKind != JsonValueKind.String) return null;
        string clean = WebUtility.HtmlDecode(field.GetString()!).Replace("\u200b", "").Trim();
        return clean.Length == 0 ? null : clean;
    }

    private async Task<(string? Name,string? Description)> StoreAsync(string host, int id, string? proxy, CancellationToken ct)
    {
        var uri = new Uri($"https://{host}/api/appdetails?appids={id}&cc=cn&l=schinese");
        var json = await RequestAsync(uri, proxy, false, ct).ConfigureAwait(false);
        if (json is not { ValueKind: JsonValueKind.Object } doc ||
            !doc.TryGetProperty(id.ToString(), out var item) || item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !item.TryGetProperty("data", out var data)) return default;
        string? description = Text(data, "short_description");
        if (description != null)
            description = Regex.Replace(Regex.Replace(description, "<[^>]+>", " "), @"\s+", " ").Trim();
        return (Text(data, "name"), string.IsNullOrEmpty(description) ? null : description);
    }

    private async Task<JsonElement?> RequestAsync(Uri uri,string? proxy,bool machine,CancellationToken ct)
    {
        if (machine)
        {
            // A machine query waiting for its turn must leave the HTTP slots available to Steam.
            await MachinePace.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long remaining = nextMachineStart - Environment.TickCount64;
                if (remaining > 0) await Task.Delay((int)remaining, ct).ConfigureAwait(false);
                await Slots.WaitAsync(ct).ConfigureAwait(false);
                nextMachineStart = Environment.TickCount64 + 600;
            }
            finally { MachinePace.Release(); }
        }
        else await Slots.WaitAsync(ct).ConfigureAwait(false);
        try { return await readJson(uri,proxy,ct).ConfigureAwait(false); }
        finally { Slots.Release(); }
    }

    internal async Task<LookupResult> ResolveStoreAsync(int id,string original,string? proxy,CancellationToken ct)
    {
        string? description = null;
        foreach (string host in new[] { "store.steampowered.com", "store.steamchina.com" })
        {
            ct.ThrowIfCancellationRequested();
            var result = await StoreAsync(host, id, proxy, ct).ConfigureAwait(false);
            description ??= result.Description;
            if (Suggestions.ContainsHan(result.Name) && !string.Equals(result.Name, original, StringComparison.OrdinalIgnoreCase))
                return new(result.Name, "Steam 商店", description);
        }
        return new(null,null,description);
    }

    internal async Task<LookupResult> ResolveMachineAsync(string original,string? proxy,CancellationToken ct)
    {
        // MyMemory limits an individual query to 500 UTF-8 bytes.
        if (string.IsNullOrWhiteSpace(original) || System.Text.Encoding.UTF8.GetByteCount(original) > 500)
            return new(null, null, null);
        var uri = new Uri("https://api.mymemory.translated.net/get?langpair=en%7Czh-CN&q=" + Uri.EscapeDataString(original));
        var json = await RequestAsync(uri, proxy, true, ct).ConfigureAwait(false);
        if (json is not { ValueKind: JsonValueKind.Object } data ||
            (data.TryGetProperty("responseStatus", out var status) && status.ToString() != "200") ||
            (data.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True) ||
            !data.TryGetProperty("responseData", out var detail)) return new(null, null, null);
        string? machine = Text(detail, "translatedText");
        if (!Suggestions.ContainsHan(machine) || machine!.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
            || string.Equals(machine, original, StringComparison.OrdinalIgnoreCase))
            return new(null, null, null);
        return new(machine, "机器翻译", null);
    }

    internal async Task<string?> DescriptionAsync(int id, string? proxy, CancellationToken ct)
    {
        var result = await StoreAsync("store.steampowered.com", id, proxy, ct).ConfigureAwait(false);
        return result.Description;
    }
}
