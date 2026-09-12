// CA-A-IA — Internet para el agente: buscar (DuckDuckGo, sin keys) y leer
// páginas como texto. Guardas: solo https, sin IPs privadas/localhost (ni por
// DNS), redirecciones acotadas, descargas acotadas, timeouts siempre.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Tools;

namespace CaAIA.Infrastructure.Tools;

/// <summary>Busca en la web (DuckDuckGo, sin API key). { "query", "maxResults"? }.</summary>
public sealed class WebSearchTool : ITool
{
    public const string ToolId = "WebSearch";
    private const int DefaultMax = 8;

    public ToolDefinition Definition { get; } = new(
        ToolId, "WebSearch", "Searches the web (no key needed). Use when you lack current/external knowledge.",
        ToolKind.Search, ToolPermission.Network,
        new[]
        {
            new ToolParameter("query", "Search terms (never paste secrets/keys/tokens).", "string", IsRequired: true),
            new ToolParameter("maxResults", "1-10 (default 8).", "integer", IsRequired: false, DefaultJson: "8"),
        },
        TimeSpan.FromSeconds(30));

    private readonly HttpClient _http;
    public WebSearchTool(HttpClient http) => _http = http;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var query = WriteFileTool.Required(doc, "query");
            if (string.IsNullOrWhiteSpace(query))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'query'.", sw);
            }

            var max = doc.RootElement.TryGetProperty("maxResults", out var m) && m.TryGetInt32(out var n)
                ? Math.Clamp(n, 1, 10) : DefaultMax;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(invocation.TimeoutOverride ?? TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query));
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CA-A-IA/1.0");
            using var response = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await WebFetchTool.ReadCappedAsync(response.Content,
                1_000_000, timeout.Token).ConfigureAwait(false);
            var results = ParseDuckResults(html).Take(max).ToList();
            if (results.Count == 0)
            {
                return WriteFileTool.Ok(invocation, $"No results for '{query}'. Try other terms.", sw);
            }

            var lines = new List<string>();
            var i = 1;
            foreach (var (title, url, snippet) in results)
            {
                lines.Add($"{i}. {title}\n   {url}" +
                    (string.IsNullOrWhiteSpace(snippet) ? string.Empty : $"\n   {snippet}"));
                i++;
            }

            return WriteFileTool.Ok(invocation, string.Join("\n", lines), sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "WebSearch timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, $"Search failed: {ex.Message}", sw);
        }
    }

    /// <summary>Parsea resultados DDG-html (defensivo: si cambian el HTML, lista vacía).</summary>
    internal static IReadOnlyList<(string Title, string Url, string Snippet)> ParseDuckResults(string html)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        if (string.IsNullOrEmpty(html))
        {
            return results;
        }

        // <a ... class="result__a" href="...">title</a> ... [result__snippet]
        foreach (Match link in Regex.Matches(html,
            @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (results.Count >= 10)
            {
                break;
            }

            var url = UnwrapDuckUrl(WebUtility.HtmlDecode(link.Groups[1].Value.Trim()));
            var title = WebFetchTool.HtmlToText(link.Groups[2].Value).Trim();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            results.Add((title, url, string.Empty));
        }

        // Snippets en orden de aparición, emparejados por posición.
        var snippets = Regex.Matches(html,
                @"<a[^>]*class=""result__snippet""[^>]*>(.*?)</a>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(m => WebFetchTool.HtmlToText(m.Groups[1].Value).Trim())
            .ToList();
        for (var i = 0; i < results.Count && i < snippets.Count; i++)
        {
            results[i] = (results[i].Title, results[i].Url, snippets[i]);
        }

        return results;
    }

    /// <summary>Desenvuelve redirects //duckduckgo.com/l/?uddg=...; resto tal cual si http(s).</summary>
    internal static string UnwrapDuckUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return string.Empty;
        }

        var full = href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        if (uri.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var kv in query)
            {
                var cut = kv.IndexOf('=');
                if (cut > 0 && kv[..cut] == "uddg")
                {
                    var target = Uri.UnescapeDataString(kv[(cut + 1)..].Replace("+", " "));
                    return target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? target : string.Empty;
                }
            }

            return string.Empty;
        }

        return uri.Scheme is "http" or "https" ? uri.ToString() : string.Empty;
    }
}

/// <summary>Lee una página como texto (título + cuerpo, truncado). { "url", "maxChars"? }.</summary>
public sealed class WebFetchTool : ITool
{
    public const string ToolId = "WebFetch";
    private const int DefaultMaxChars = 20_000;
    private const long MaxDownloadBytes = 2_000_000;

    public ToolDefinition Definition { get; } = new(
        ToolId, "WebFetch", "Reads a web page as text (title + body, truncated). Use after WebSearch.",
        ToolKind.Documentation, ToolPermission.Network,
        new[]
        {
            new ToolParameter("url", "Absolute https URL (no localhost/private).", "string", IsRequired: true),
            new ToolParameter("maxChars", "Max chars 1000-40000 (default 20000).", "integer", IsRequired: false, DefaultJson: "20000"),
        },
        TimeSpan.FromSeconds(40));

    private readonly HttpClient _http;
    public WebFetchTool(HttpClient http) => _http = http;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var url = WriteFileTool.Required(doc, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                return WriteFileTool.Fail(invocation, "Missing required argument 'url'.", sw);
            }

            var maxChars = doc.RootElement.TryGetProperty("maxChars", out var m) && m.TryGetInt32(out var n)
                ? Math.Clamp(n, 1000, 40_000) : DefaultMaxChars;
            var checkedUrl = await WebSecurity.ValidateUrlAsync(url, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(invocation.TimeoutOverride ?? TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Get, checkedUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CA-A-IA/1.0");
            using var response = await _http.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // La URL pedida ya se validó; aquí solo se revalida el destino FINAL
            // tras redirects (el stub de tests no fija RequestMessage: se omite).
            if (response.RequestMessage?.RequestUri is Uri final
                && !WebSecurity.IsAllowedResponseUrl(final))
            {
                return WriteFileTool.Fail(invocation, "Redirected to a blocked host.", sw);
            }

            response.EnsureSuccessStatusCode();
            var html = await ReadCappedAsync(response.Content, MaxDownloadBytes, timeout.Token)
                .ConfigureAwait(false);
            var title = ExtractTitle(html);
            var text = HtmlToText(html);
            if (text.Length > maxChars)
            {
                text = text[..maxChars] + "\n…[truncated]";
            }

            var header = string.IsNullOrWhiteSpace(title) ? checkedUrl : $"# {title}\n{checkedUrl}";
            return WriteFileTool.Ok(invocation, $"{header}\n\n{text}", sw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WriteFileTool.Fail(invocation, "WebFetch timed out.", sw);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            return WriteFileTool.Fail(invocation, $"Fetch failed: {ex.Message}", sw);
        }
    }

    internal static string ExtractTitle(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? HtmlToText(match.Groups[1].Value).Trim() : string.Empty;
    }

    /// <summary>HTML → texto: fuera scripts/estilos/navegación, entidades, espacios.</summary>
    internal static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = Regex.Replace(html,
            @"<(script|style|noscript|svg|nav|footer|header|aside|form)[^>]*>.*?</\1>",
            " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t\xA0]+", " ");
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return string.Join("\n", lines).Trim();
    }

    internal static async Task<string> ReadCappedAsync(
        HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                ms.Write(buffer, 0, (int)(maxBytes - ms.Length));
                break;
            }

            ms.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Guardas de red: https + sin localhost/privadas (literal o por DNS).</summary>
public static class WebSecurity
{
    private static readonly string[] BlockedHostnames =
        { "localhost", "metadata.google.internal" };

    /// <summary>Valida y devuelve la URL canónica (solo https). Lanza InvalidOperationException si no.</summary>
    public static async Task<string> ValidateUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("URL must be absolute https.");
        }

        if (BlockedHostnames.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Blocked host: {uri.Host}.");
        }

        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            if (IsBlockedIp(literal))
            {
                throw new InvalidOperationException($"Blocked IP literal: {uri.Host}.");
            }

            return uri.ToString();
        }

        // Nombre DNS: si resuelve a privada/loopback, fuera (nota: TOCTOU residual
        // documentado; el redirect final se revalida en IsAllowedResponseUrl).
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            if (addresses.Length > 0 && addresses.All(IsBlockedIp))
            {
                throw new InvalidOperationException($"Host resolves to blocked addresses: {uri.Host}.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Cannot resolve host: {uri.Host}.", ex);
        }

        return uri.ToString();
    }

    public static bool IsAllowedResponseUrl(Uri? uri) =>
        uri is not null && uri.Scheme == Uri.UriSchemeHttps
        && !BlockedHostnames.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        && !(IPAddress.TryParse(uri.Host, out var ip) && IsBlockedIp(ip));

    internal static bool IsBlockedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 10/8, 172.16/12, 192.168/16, 169.254/16, 0/8, multicast, broadcast.
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0 || bytes[0] >= 224;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // ::1 (ya cubierto), :: (unspecified), fc00::/7, fe80::/10, ff00::/8.
            if (ip.Equals(IPAddress.IPv6None) || ip.IsIPv6Multicast)
            {
                return true;
            }

            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }

        return true;
    }
}
