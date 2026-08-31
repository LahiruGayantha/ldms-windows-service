# LDMS Outlet Camera Helper

A tiny local Windows service that lets the LDMS web frontend capture item photos from
the outlet's IP camera.

## Why it exists

The camera lives on the outlet LAN at a per-outlet URL protected by **HTTP Digest**
auth. The frontend runs in a browser served over HTTPS from Azure, so it cannot reach
an `http://` LAN address or perform a Digest handshake from JavaScript. This service
runs on a PC at the outlet, on `http://127.0.0.1:<port>` (loopback only). The browser
is allowed to call `http://localhost` even from an HTTPS page, so it asks this service
for a snapshot; the service does the Digest fetch to the camera and returns the JPEG.
The browser then uploads the bytes to `ldms-web-api`, which archives them in Azure Blob
Storage.

```
Browser (collection-order screen)
  --> GET http://127.0.0.1:8770/snapshot
        --> Digest GET  http://<camera-lan-ip>/<snapshot-path>   (this service)
        <-- image/jpeg
  --> POST /api/collection-order-item-images/stage   (ldms-web-api)
```

## Build

```
dotnet publish LdmsOutletCameraHelper.csproj -c Release -r win-x64 --self-contained -o publish
```

## Install (per outlet PC, as Administrator)

1. Copy the `publish` folder to the outlet PC.
2. Run `install.ps1` from an elevated PowerShell. It creates
   `%ProgramData%\LdmsOutletCameraHelper\appsettings.json` and registers the service.
3. Edit that config file with this outlet's values, then restart the service
   (`Restart-Service LdmsOutletCameraHelper`).

```json
{
  "Camera": {
    "SnapshotUrl": "http://192.168.1.64/ISAPI/Streaming/channels/101/picture",
    "Username": "operator",
    "Password": "the-camera-password",
    "AllowSelfSignedCert": false,
    "TimeoutSeconds": 10
  },
  "ListenPort": 8770,
  "AllowedOrigins": [ "https://<the-outlet-frontend-origin>" ]
}
```

- `SnapshotUrl` - the camera's still-image endpoint (Hikvision/Axis/Dahua all expose
  one). Use `https://` + `"AllowSelfSignedCert": true` only if the camera forces TLS
  with its own certificate.
- `AllowedOrigins` - the origin the LDMS frontend is served from. Leave `[]` to allow
  any origin (acceptable because the service only listens on loopback).
- `ListenPort` - must match `cameraHelperURL` in the frontend's `environment.*.ts`.

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | `{ "status": "ok", "cameraConfigured": true }` |
| GET | `/snapshot` | Digest-fetches the camera still, returns `image/jpeg` (HTTP 502 with a JSON `error` on failure) |

## Troubleshooting

Run the exe directly from a console instead of as a service to see logs:

```
LdmsOutletCameraHelper.exe
```

`curl http://127.0.0.1:8770/health` and `curl -v http://127.0.0.1:8770/snapshot -o test.jpg`
verify the service and the camera path respectively.

The credentials in `appsettings.json` are plaintext (same posture as the tagging
machine settings in the WinForms app). The file sits under `%ProgramData%`; restrict
its ACL if the outlet PC is shared.
