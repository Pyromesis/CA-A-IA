// CA-A-IA — Catálogo del ecosistema OpenCode (models.dev, bloque "opencode" == OpenCode Zen).
// Fuente pública y documentada (la que usa el propio opencode); con caché local de 24 h.
// Aporta lo que el endpoint Zen no da: gratuidad (coste 0/0), contexto y capacidades.

using System.Text.Json;
using CaAIA.Domain.AI;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Providers.Zen;

/// <summary>
/// Lee el bloque <c>opencode</c> de models.dev (<c>api.json</c>) con caché en DataPath.
/// Sin red o con forma irreconocible: usa caché vieja si existe; si no, lista vacía (nunca excepción).
/// </summary>
public sealed class ModelsDevCatalog
{
    public const string DefaultCatalogUrl = "https://models.dev/api.json";

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _catalogUrl;
    private readonly TimeSpan _ttl;
    private readonly ILogger<ModelsDevCatalog> _log;

    public ModelsDevCatalog(
        HttpClient http, string dataPath, ILogger<ModelsDevCatalog> log,
        string catalogUrl = DefaultCatalogUrl, TimeSpan? ttl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cachePath = Path.Combine(dataPath, "models-dev-api.json");
        _catalogUrl = catalogUrl;
        _log = log;
        _ttl = ttl ?? TimeSpan.FromHours(24);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<IReadOnlyList<AIModel>> GetOpenCodeModelsAsync(
        string providerId, CancellationToken ct)
    {
        var json = await LoadAsync(ct).ConfigureAwait(false);
        if (json is null)
        {
            return Array.Empty<AIModel>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("opencode", out var provider)
                || !provider.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object)
            {
                _log.LogWarning("models.dev: bloque 'opencode' irreconocible.");
                return Array.Empty<AIModel>();
            }

            var result = new List<AIModel>();
            foreach (var entry in models.EnumerateObject())
            {
                var model = ParseModel(providerId, entry.Value);
                if (model is not null)
                {
                    result.Add(model);
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "models.dev: JSON inválido.");
            return Array.Empty<AIModel>();
        }
    }

    internal static AIModel? ParseModel(string providerId, JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Str(m, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = Str(m, "name") ?? id;
        int? context = m.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object
            ? Int(limit, "context") : null;

        var costInput = m.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object
            ? Num(cost, "input") : null;
        var costOutput = costInput.HasValue ? Num(cost, "output") : null;
        var isFree = costInput is 0 && costOutput is 0;

        var caps = ProviderCapabilities.Streaming;
        if (m.TryGetProperty("tool_call", out var tools) && tools.ValueKind == JsonValueKind.True)
        {
            caps |= ProviderCapabilities.Tools;
        }

        if (context is >= 100_000)
        {
            caps |= ProviderCapabilities.LongContext;
        }

        if (m.TryGetProperty("modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Object
            && modalities.TryGetProperty("input", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in inputs.EnumerateArray())
            {
                if (input.ValueKind == JsonValueKind.String &&
                    input.GetString()?.Contains("image", StringComparison.OrdinalIgnoreCase) == true)
                {
                    caps |= ProviderCapabilities.Vision;
                    break;
                }
            }
        }

        return new AIModel(id, name, providerId, context, caps, IsFree: isFree)
        {
            SupportedEfforts = ParseReasoningOptions(m),
        };
    }

    /// <summary>
    /// Niveles de `reasoning_options` con type "effort" (p. ej. ["low","medium","high","xhigh"]).
    /// </summary>
    internal static IReadOnlyList<string> ParseReasoningOptions(JsonElement model)
    {
        if (model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("reasoning_options", out var options)
            && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind == JsonValueKind.Object
                    && option.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "effort"
                    && option.TryGetProperty("values", out var values)
                    && values.ValueKind == JsonValueKind.Array)
                {
                    return values.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!.Trim().ToLowerInvariant())
                        .Where(s => s.Length > 0)
                        .Distinct()
                        .ToList();
                }
            }
        }

        return Array.Empty<string>();
    }

    private async Task<string?> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(_cachePath) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_cachePath) < _ttl)
            {
                return await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "models.dev: caché ilegible.");
        }

        var downloaded = await DownloadAsync(ct).ConfigureAwait(false);
        if (downloaded is not null)
        {
            return downloaded;
        }

        // Sin red: caché vieja antes que nada.
        try
        {
            return File.Exists(_cachePath)
                ? await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> DownloadAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var response = await _http.GetAsync(_catalogUrl, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("models.dev -> HTTP {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                await File.WriteAllTextAsync(_cachePath, json, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "models.dev: no se pudo cachear (se usa en memoria).");
            }

            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug(ex, "models.dev: descarga fallida.");
            return null;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static double? Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
}
