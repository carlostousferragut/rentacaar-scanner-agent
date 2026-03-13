# RentaCaaR Scanner Agent

Windows Service that provides a local HTTP API on port 7823 for scanning documents (Spanish DNI/NIE via PDF417 barcode, passports via MRZ OCR) using WIA-connected flatbed scanners.

## Requirements

- Windows 10/11 x64
- .NET 8 SDK (for development)
- A WIA-compatible flatbed scanner
- Inno Setup 6 (to build the installer)
- Tesseract tessdata files (for MRZ/passport OCR)

## Tessdata Setup

Download the trained data files from the [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) repository and place them in:

```
RentaCaaR.ScannerAgent/tessdata/
  mrz.traineddata    ← specialized MRZ model (preferred)
  spa.traineddata    ← Spanish language (fallback)
  eng.traineddata    ← English language (fallback)
```

The `mrz.traineddata` file can be found in the [tessdata](https://github.com/tesseract-ocr/tessdata) repo (it's a specialized model for machine-readable zones).

## Build

```bash
# Restore packages
dotnet restore RentaCaaR.ScannerAgent/RentaCaaR.ScannerAgent.csproj

# Publish self-contained single-file executable
dotnet publish RentaCaaR.ScannerAgent/RentaCaaR.ScannerAgent.csproj -c Release
```

Output: `RentaCaaR.ScannerAgent/bin/Release/net8.0-windows/win-x64/publish/RentaCaaR.ScannerAgent.exe`

## Build the Installer

1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
2. Run the build first (see above) so the publish output exists
3. Compile the installer script:

```bash
iscc installer/setup.iss
```

Output: `installer/Output/RentaCaaR-Scanner-Setup-1.0.6.exe`

## Automated Release (GitHub Actions)

This repository includes a workflow at `.github/workflows/release.yml` that:

1. Reads `MyAppVersion` from `installer/setup.iss`
2. Validates it matches `<Version>` in `RentaCaaR.ScannerAgent.csproj`
3. Restores and publishes the agent in `Release`
4. Installs Inno Setup on the runner
5. Builds the installer from `installer/setup.iss`
6. Creates/publishes GitHub Release `v{MyAppVersion}` with the installer asset

It runs automatically on `push` to `main` (no manual tag needed).

If release `v{MyAppVersion}` already exists, it skips publishing.

Typical flow:

```bash
# 1) Bump version in both files to the same value (e.g. 1.0.5)
#    - RentaCaaR.ScannerAgent/RentaCaaR.ScannerAgent.csproj
#    - installer/setup.iss

# 2) Commit and push to main
git add .
git commit -m "Release 1.0.5"
git push origin main
```

Important: keep `RentaCaaR.ScannerAgent.csproj` and `installer/setup.iss` versions aligned, otherwise the workflow fails by design.

## Development (run as console app)

```bash
dotnet run --project RentaCaaR.ScannerAgent/RentaCaaR.ScannerAgent.csproj
```

The service will start on `http://127.0.0.1:7823`. When running as a console app (not a Windows Service), it behaves identically — WIA and OCR work the same way.

## API Endpoints

| Method | Path        | Description                                      |
|--------|-------------|--------------------------------------------------|
| GET    | /health     | Service status, version, registration state      |
| GET    | /scanners   | List connected WIA scanners                      |
| POST   | /scan       | Scan a document and return fields + base64 image |
| POST   | /register   | Register agent with the backend using a token    |
| POST   | /settings   | Set default scanner ID                           |

### POST /scan body

```json
{
  "scannerId": "optional-wia-device-id",
  "dpi": 300
}
```

### POST /register body

```json
{
  "token": "one-time-registration-token",
  "backendUrl": "https://your-rentacaar-instance.com",
  "name": "optional-agent-name"
}
```

## Windows Registry

Configuration is stored in:

```
HKEY_LOCAL_MACHINE\SOFTWARE\RentaCaaR\ScannerAgent
  AgentId          (string)
  Secret           (string)
  BackendUrl       (string)
  OrgName          (string)
  OfficeName       (string)
  DefaultScannerId (string, optional)
```

## Auto-Update

The service polls `{backendUrl}/api/agent/updates?version=X&agentId=Y` every hour. If a new version is available, it downloads and silently runs the Inno Setup installer (`/VERYSILENT /NORESTART /SUPPRESSMSGBOXES`), which stops the service, replaces the binary, and restarts it automatically.

## Heartbeat

Every 5 minutes, the service posts to `{backendUrl}/api/agent/heartbeat` with the current version. Authentication uses the `X-Agent-Auth: {agentId}:{secret}` header.
