using RentaCaaR.ScannerAgent.Config;
using System.Diagnostics;
using System.Text.Json;

namespace RentaCaaR.ScannerAgent.Update;

public class UpdateChecker : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly HttpClient _http = new();
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    public UpdateChecker(AgentConfig config, ILogger<UpdateChecker> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait 2 minutes after startup before first check
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            await CheckForUpdateAsync();
            await Task.Delay(CheckInterval, ct);
        }
    }

    private async Task CheckForUpdateAsync()
    {
        if (!_config.IsRegistered || string.IsNullOrEmpty(_config.BackendUrl))
        {
            _logger.LogInformation("Skipping update check: agent is not registered or backend URL is missing.");
            return;
        }

        try
        {
            var url = $"{_config.BackendUrl.TrimEnd('/')}/api/agent/updates" +
                      $"?version={Uri.EscapeDataString(AgentConfig.AgentVersion)}" +
                      $"&agentId={Uri.EscapeDataString(_config.AgentId ?? "")}";

            _http.DefaultRequestHeaders.Clear();
            if (!string.IsNullOrEmpty(_config.AgentId) && !string.IsNullOrEmpty(_config.Secret))
                _http.DefaultRequestHeaders.Add("X-Agent-Auth", $"{_config.AgentId}:{_config.Secret}");

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Update check returned HTTP {StatusCode}. Body: {Body}",
                    (int)response.StatusCode, errorBody);
                return;
            }

            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<UpdateResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.HasUpdate != true || string.IsNullOrEmpty(result.DownloadUrl))
            {
                _logger.LogInformation("No update available. Current version: {Current}", AgentConfig.AgentVersion);
                return;
            }

            _logger.LogInformation("Update available: {Current} → {New}. Downloading...",
                AgentConfig.AgentVersion, result.Version);

            await ApplyUpdateAsync(result.DownloadUrl, result.Version ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Update check failed: {Message}", ex.Message);
        }
    }

    private async Task ApplyUpdateAsync(string downloadUrl, string newVersion)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"rentacaar-scanner-update-{newVersion}.exe");

        try
        {
            _logger.LogInformation("Downloading update from {Url}", downloadUrl);
            var data = await _http.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(tempPath, data);
            _logger.LogInformation("Update installer downloaded to {Path} ({Bytes} bytes)", tempPath, data.Length);

            _logger.LogInformation("Launching installer silently...");
            Process.Start(new ProcessStartInfo
            {
                FileName  = tempPath,
                Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
                UseShellExecute = true,
            });
            // Inno Setup will stop & restart the service — no need to exit here
        }
        catch (Exception ex)
        {
            _logger.LogError("Update failed: {Message}", ex.Message);
            try { File.Delete(tempPath); } catch { }
        }
    }

    private record UpdateResponse
    {
        public bool HasUpdate { get; init; }
        public string? Version { get; init; }
        public string? DownloadUrl { get; init; }
    }
}
