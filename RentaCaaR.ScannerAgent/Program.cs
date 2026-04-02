using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentaCaaR.ScannerAgent.Config;
using RentaCaaR.ScannerAgent.Logging;
using RentaCaaR.ScannerAgent.Scanner;
using RentaCaaR.ScannerAgent.Ocr;
using RentaCaaR.ScannerAgent.Update;
using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var defaultLogPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "RentaCaaR",
    "ScannerAgent",
    "logs",
    "agent.log");

var fileLogPath = builder.Configuration["FileLogging:Path"] ?? defaultLogPath;
var fileLogMinLevel = Enum.TryParse<LogLevel>(builder.Configuration["FileLogging:MinLevel"], true, out var parsedLevel)
    ? parsedLevel
    : LogLevel.Information;
var fileLogRetentionDays = int.TryParse(builder.Configuration["FileLogging:RetentionDays"], out var parsedRetention)
    ? parsedRetention
    : 7;

builder.Logging.AddProvider(new FileLoggerProvider(fileLogPath, fileLogMinLevel, fileLogRetentionDays));

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
            // "null" origin = fichero local abierto directamente en el navegador (file://)
            if (origin == "null") return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            // Localhost para desarrollo
            if (uri.Host == "localhost" || uri.Host == "127.0.0.1") return true;
            // Cualquier origen HTTPS es seguro: el agente solo escucha en 127.0.0.1,
            // por lo que solo peticiones del propio equipo pueden llegar aquí.
            return uri.Scheme == "https";
        })
        .AllowAnyMethod()
        .AllowAnyHeader();
    });
});

var app = builder.Build();
app.UseCors();

var appLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentRuntime");

app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    appLogger.LogInformation("HTTP {Method} {Path} started", ctx.Request.Method, ctx.Request.Path);
    try
    {
        await next();
        appLogger.LogInformation(
            "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
            ctx.Request.Method,
            ctx.Request.Path,
            ctx.Response.StatusCode,
            sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "HTTP {Method} {Path} failed after {ElapsedMs}ms", ctx.Request.Method, ctx.Request.Path, sw.ElapsedMilliseconds);
        throw;
    }
});

var config = app.Services.GetRequiredService<AgentConfig>();
var scanner = app.Services.GetRequiredService<WiaScanner>();
var processor = app.Services.GetRequiredService<DocumentProcessor>();

appLogger.LogInformation(
    "Scanner agent starting. Version={Version}, Registered={Registered}, BackendUrl={BackendUrl}",
    AgentConfig.AgentVersion,
    config.IsRegistered,
    string.IsNullOrWhiteSpace(config.BackendUrl) ? "(none)" : config.BackendUrl);

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
        appLogger.LogInformation("/scanners returned {Count} scanner(s)", scanners.Count);
        return Results.Ok(scanners);
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "/scanners failed");
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
        appLogger.LogInformation("/scan request received. RequestedScannerId={RequestedScannerId}, Dpi={Dpi}", opts.ScannerId, opts.Dpi ?? 300);
        var scanners = scanner.GetScanners();
        if (scanners.Count == 0)
        {
            appLogger.LogWarning("/scan failed: no connected scanner found");
            return Results.Problem("No se encontró ningún escáner conectado");
        }

        string scannerId = opts.ScannerId ?? config.DefaultScannerId ?? scanners[0].Id;
        int dpi = opts.Dpi ?? 300;

        appLogger.LogInformation("/scan using scanner {ScannerId} at {Dpi} DPI", scannerId, dpi);

        byte[] imageBytes = scanner.Scan(scannerId, dpi);
        appLogger.LogInformation("/scan image captured: {Bytes} byte(s)", imageBytes.Length);

        var fields = processor.Process(imageBytes);
        appLogger.LogInformation("/scan processing completed. Method={Method}, DocumentType={DocumentType}", fields.Method, fields.DocumentType);

        return Results.Ok(new
        {
            fields,
            imageBase64 = Convert.ToBase64String(imageBytes),
        });
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "/scan failed");
        return Results.Problem(ex.Message);
    }
});

// POST /process  body: { imageBase64: string }
// Extrae campos de un documento a partir de una imagen ya capturada (sin escáner).
// Útil para pruebas o para procesar fotos tomadas con móvil.
app.MapPost("/process", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<ProcessRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (req == null || string.IsNullOrWhiteSpace(req.ImageBase64))
        return Results.BadRequest(new { error = "imageBase64 es requerido" });

    try
    {
        // Admite tanto base64 puro como data URL (data:image/jpeg;base64,...)
        var b64 = req.ImageBase64.Contains(',') ? req.ImageBase64.Split(',')[1] : req.ImageBase64;
        var imageBytes = Convert.FromBase64String(b64);
        appLogger.LogInformation("/process image received: {Bytes} byte(s)", imageBytes.Length);

        var fields = processor.Process(imageBytes);
        appLogger.LogInformation("/process completed. Method={Method}, DocumentType={DocumentType}", fields.Method, fields.DocumentType);

        return Results.Ok(new { fields });
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "/process failed");
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
        appLogger.LogInformation("/register attempt. BackendUrl={BackendUrl}, Name={Name}", req.BackendUrl, req.Name ?? Environment.MachineName);
        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { token = req.Token, name = req.Name ?? Environment.MachineName });
        var response = await http.PostAsync($"{req.BackendUrl.TrimEnd('/')}/api/agent/activate",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            appLogger.LogWarning("/register backend rejected registration. Status={StatusCode}", (int)response.StatusCode);
            return Results.Problem(responseBody);
        }

        var result = JsonSerializer.Deserialize<ActivateResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        config.Save(result.AgentId, result.Secret, req.BackendUrl, result.OrgName, result.OfficeName);
        appLogger.LogInformation("/register success. AgentId={AgentId}, Org={Org}, Office={Office}", result.AgentId, result.OrgName, result.OfficeName);
        return Results.Ok(new { ok = true, orgName = result.OrgName, officeName = result.OfficeName });
    }
    catch (Exception ex)
    {
        appLogger.LogError(ex, "/register failed");
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
    {
        config.SaveDefaultScanner(req.DefaultScannerId);
        appLogger.LogInformation("/settings updated default scanner to {ScannerId}", req.DefaultScannerId);
    }
    else
    {
        appLogger.LogInformation("/settings called without defaultScannerId change");
    }
    return Results.Ok(new { ok = true });
});

await app.RunAsync();

// ─── Request/Response models ────────────────────────────────────────────────
record ScanRequest { public string? ScannerId { get; init; } public int? Dpi { get; init; } }
record ProcessRequest { public string? ImageBase64 { get; init; } }
record RegisterRequest { public string? Token { get; init; } public string? BackendUrl { get; init; } public string? Name { get; init; } }
record ActivateResponse { public string AgentId { get; init; } = ""; public string Secret { get; init; } = ""; public string OrgName { get; init; } = ""; public string OfficeName { get; init; } = ""; }
record SettingsRequest { public string? DefaultScannerId { get; init; } }
