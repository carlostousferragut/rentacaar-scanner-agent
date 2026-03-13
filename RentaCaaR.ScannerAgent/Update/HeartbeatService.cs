using RentaCaaR.ScannerAgent.Config;
using System.Text;
using System.Text.Json;

namespace RentaCaaR.ScannerAgent.Update;

public class HeartbeatService : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly HttpClient _http = new();
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public HeartbeatService(AgentConfig config, ILogger<HeartbeatService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendHeartbeatAsync();
            await Task.Delay(Interval, ct);
        }
    }

    private async Task SendHeartbeatAsync()
    {
        if (!_config.IsRegistered || string.IsNullOrEmpty(_config.BackendUrl)) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { version = AgentConfig.AgentVersion });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("X-Agent-Auth", $"{_config.AgentId}:{_config.Secret}");
            var response = await _http.PostAsync(
                $"{_config.BackendUrl.TrimEnd('/')}/api/agent/heartbeat", content);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Heartbeat rejected (401), clearing local credentials");
                _config.ClearCredentials();
            }
        }
        catch { /* heartbeat is best-effort */ }
    }
}
