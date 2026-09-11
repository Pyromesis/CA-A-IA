// CA-A-IA — Actualización automática desde GitHub Releases: check, descarga
// verificada (SHA-256 del API + tamaño) e instalación silenciosa en modo /UPDATE.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CaAIA.Domain.Update;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Update;

/// <summary>
/// Canal fijo del proyecto (Pyromesis/CA-A-IA). Solo HTTPS y solo hosts de
/// GitHub: el binario que se ejecuta sale del release oficial o no sale.
/// </summary>
public sealed class GitHubAppUpdater : IAppUpdateService
{
    internal const string Owner = "Pyromesis";
    internal const string Repo = "CA-A-IA";
    internal const string AssetPrefix = "CA-A-IA-Setup-";

    private static readonly string[] AllowedHosts =
        { "api.github.com", "github.com", "objects.githubusercontent.com" };

    private readonly HttpClient _http;
    private readonly ILogger<GitHubAppUpdater> _log;

    public Version CurrentVersion { get; }

    public GitHubAppUpdater(HttpClient http, ILogger<GitHubAppUpdater> log, Version? currentVersion = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        var running = currentVersion
            ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
            ?? new Version(0, 1, 0);
        // Normalizar a 3 componentes: la assembly lleva 0.1.0.0 y el tag 0.1.0;
        // sin esto, 0.1.0.0 > 0.1.0 y ninguna release parecería nueva.
        CurrentVersion = new Version(Math.Max(0, running.Major), Math.Max(0, running.Minor), Math.Max(0, running.Build));
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("CA-A-IA-updater", CurrentVersion.ToString()));
        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(CurrentVersion, null); // aún sin releases
        }

        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false),
            cancellationToken: linked.Token).ConfigureAwait(false);
        var info = ParseRelease(doc.RootElement);
        if (info is null || info.Version <= CurrentVersion)
        {
            return new UpdateCheckResult(CurrentVersion, null);
        }

        _log.LogInformation("Update available: {Current} -> {New} ({Tag})",
            CurrentVersion, info.Version, info.TagName);
        return new UpdateCheckResult(CurrentVersion, info);
    }

    internal UpdateInfo? ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if ((root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True))
        {
            return null; // /latest no los trae, pero no fiarse
        }

        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty : string.Empty;
        var version = ParseVersion(tag);
        if (version is null)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty : string.Empty;
            if (!name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() ?? string.Empty : string.Empty;
            if (!IsAllowedUrl(url))
            {
                _log.LogWarning("Release asset URL not allowed: {Url}", url);
                continue;
            }

            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                && s.TryGetInt64(out var bytes) ? bytes : 0;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var digest = d.GetString() ?? string.Empty;
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha = digest["sha256:".Length..];
                }
            }

            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? string.Empty : string.Empty;
            return new UpdateInfo(version, tag, notes.Trim(), url, size, sha);
        }

        return null;
    }

    public async Task<string> DownloadAsync(
        UpdateInfo update, IProgress<double> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);
        if (!IsAllowedUrl(update.DownloadUrl))
        {
            throw new InvalidOperationException($"Download URL not allowed: {update.DownloadUrl}");
        }

        var dir = Path.Combine(Path.GetTempPath(), "CA-A-IA-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"CA-A-IA-Setup-{update.Version}.exe");
        using var response = await _http.GetAsync(update.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        using var sha = update.Sha256 is not null ? SHA256.Create() : null;
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha?.TransformBlock(buffer, 0, read, null, 0);
            received += read;
            if (total > 0)
            {
                progress.Report(Math.Min(1.0, (double)received / total));
            }
        }

        await file.FlushAsync(ct).ConfigureAwait(false);
        progress.Report(1.0);

        if (update.SizeBytes > 0 && received != update.SizeBytes)
        {
            throw new InvalidOperationException(
                $"Download size mismatch: got {received}, expected {update.SizeBytes}.");
        }

        if (sha is not null)
        {
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var actual = Convert.ToHexString(sha.Hash!);
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Download hash mismatch: the file is not the official one.");
            }
        }

        _log.LogInformation("Update {Version} downloaded and verified ({Bytes} bytes).",
            update.Version, received);
        return path;
    }

    public bool LaunchInstaller(string installerPath, string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            throw new ArgumentException("Installer not found.", nameof(installerPath));
        }

        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("Install directory is required.", nameof(installDirectory));
        }

        // Silencioso + cierra la app en ejecución + modo /UPDATE (el instalador
        // reabre la app al terminar). Por usuario: sin UAC.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = $"/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /UPDATE=1 /DIR=\"{installDirectory}\"",
            UseShellExecute = true,
        };
        try
        {
            using var _ = System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not launch update installer.");
            return false;
        }
    }

    internal static Version? ParseVersion(string tag)
    {
        var t = (tag ?? string.Empty).Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v)
            ? new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build))
            : null;
    }

    internal static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        foreach (var host in AllowedHosts)
        {
            if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
