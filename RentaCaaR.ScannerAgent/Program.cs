using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RentaCaaR.ScannerAgent.Config;
using RentaCaaR.ScannerAgent.Scanner;
using RentaCaaR.ScannerAgent.Ocr;
using RentaCaaR.ScannerAgent.Update;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "RentaCaaRScanner";
});

builder.WebHost.UseUrls("http://127.0.0.1:7823");

builder.Services.AddSingleton<AgentConfig>();
builder.Services.AddSingleton<WiaScanner>();
builder.Services.AddSingleton<DocumentProcessor>();
builder.Services.AddHostedService<UpdateChecker>();
builder.Services.AddHostedService<HeartbeatService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            var uri = new Uri(origin);
            return uri.Host == "localhost" || uri.Host == "127.0.0.1";
        })
        .AllowAnyMethod()
        .AllowAnyHeader();
    });
});

var app = builder.Build();
app.UseCors();

var config = app.Services.GetRequiredService<AgentConfig>();
var scanner = app.Services.GetRequiredService<WiaScanner>();
var processor = app.Services.GetRequiredService<DocumentProcessor>();

// GET /health
app.MapGet("/health", () => Results.Ok(new
{
    version = AgentConfig.AgentVersion,
    registered = config.IsRegistered,
    agentId = config.AgentId,
    orgName = config.OrgName,
    officeName = config.OfficeName,
}));

// GET /scanners
app.MapGet("/scanners", () =>
{
    try
    {
        var scanners = scanner.GetScanners();
        return Results.Ok(scanners);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// POST /scan  body: { scannerId?: string, dpi?: int }
app.MapPost("/scan", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var opts = JsonSerializer.Deserialize<ScanRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? new ScanRequest();

    try
    {
        var scanners = scanner.GetScanners();
        if (scanners.Count == 0)
            return Results.Problem("No se encontró ningún escáner conectado");

        string scannerId = opts.ScannerId ?? config.DefaultScannerId ?? scanners[0].Id;
        int dpi = opts.Dpi ?? 300;

        byte[] imageBytes = scanner.Scan(scannerId, dpi);
        var fields = processor.Process(imageBytes);

        return Results.Ok(new
        {
            fields,
            imageBase64 = Convert.ToBase64String(imageBytes),
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// POST /register  body: { token: string, backendUrl: string }
app.MapPost("/register", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<RegisterRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (req == null || string.IsNullOrEmpty(req.Token) || string.IsNullOrEmpty(req.BackendUrl))
        return Results.BadRequest(new { error = "token y backendUrl son requeridos" });

    try
    {
        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { token = req.Token, name = req.Name ?? Environment.MachineName });
        var response = await http.PostAsync($"{req.BackendUrl.TrimEnd('/')}/api/agent/activate",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return Results.Problem(responseBody);

        var result = JsonSerializer.Deserialize<ActivateResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        config.Save(result.AgentId, result.Secret, req.BackendUrl, result.OrgName, result.OfficeName);
        return Results.Ok(new { ok = true, orgName = result.OrgName, officeName = result.OfficeName });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// POST /settings  body: { defaultScannerId?: string }
app.MapPost("/settings", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<SettingsRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (req?.DefaultScannerId != null)
        config.SaveDefaultScanner(req.DefaultScannerId);
    return Results.Ok(new { ok = true });
});

await app.RunAsync();

// ─── Request/Response models ────────────────────────────────────────────────
record ScanRequest { public string? ScannerId { get; init; } public int? Dpi { get; init; } }
record RegisterRequest { public string? Token { get; init; } public string? BackendUrl { get; init; } public string? Name { get; init; } }
record ActivateResponse { public string AgentId { get; init; } = ""; public string Secret { get; init; } = ""; public string OrgName { get; init; } = ""; public string OfficeName { get; init; } = ""; }
record SettingsRequest { public string? DefaultScannerId { get; init; } }
