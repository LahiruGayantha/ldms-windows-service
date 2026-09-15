# LDMS Outlet Helper

A tiny local Windows service with three independent, per-outlet integrations: fetching
item photos from the outlet's IP camera, printing garment tags on the outlet's serial
tag printer, and (optionally) relaying fingerprint-terminal attendance events to
`ldms-web-api`. Most outlets only need the camera and printer halves.

## Camera snapshots — why it exists

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

## Tag printing — why it exists

The tag printer is a serial (COM port) device, physically attached to the outlet PC.
A browser cannot open a COM port itself, and `ldms-windows-app` (the WinForms client
that previously owned this connection directly) needed the same capability available to
a browser-based order flow too. This service now owns the connection: any caller on the
same PC - the WinForms app or the browser - asks it to print, and it does the actual
serial I/O. Same loopback-only reasoning as the camera: both callers always run on the
outlet PC itself.

```
Caller (WinForms app, or browser via the frontend)
  --> POST http://127.0.0.1:8770/print/tag   { "tag": "SH1234", "tagCount": 2 }
        --> ENQ/ACK handshake + framed write   (this service, serial port)
        <-- { "success": true, "message": "Sent 2 * SH1234 to printer." }
```

Concurrent print requests are serialized with an in-process lock, not a queue - a
physical port can only run one job at a time, and printing is human-triggered (clicking
Save), not high-frequency.

## Attendance relay — why it exists

The Hikvision fingerprint terminal (`DS-K1A802AMF-B`, confirmed against real hardware —
see `aidlc-docs/construction/spike-s0-device-https.md` in the workspace root) can push
access events in real time, but only as **plain HTTP to a bare IPv4 address** — it
cannot do HTTPS and cannot be pointed at a domain name. So it can't reach the public
`ldms-web-api` webhook directly. This service accepts that LAN push, durably queues it
to disk, and relays it over HTTPS with the same per-device secret the webhook already
expects — a transparent forward, not a parser; `ldms-web-api`'s `IDeviceEventAdapter`
still does all the interpretation.

```
Fingerprint terminal (outlet LAN)
  --> POST http://<this-pc-lan-ip>:8771/attendance/events   (plain HTTP, real-time push)
        --> queued to disk (%ProgramData%\LdmsOutletCameraHelper\attendance-queue\)
        --> POST https://<ldms-web-api>/api/attendance/device-events?deviceId=N   (HTTPS, retried until accepted)
              header: X-Device-Secret
```

Unlike the camera port, this one binds **all interfaces**, not loopback — the terminal
is a separate physical device on the LAN and must be able to reach it. That is a real,
deliberate widening of this service's network exposure, scoped to the outlet's own
trusted LAN (the same network the terminal itself lives on). `AllowedDeviceIp` is the
only check available at that boundary, since the terminal has no way to send our
webhook secret on its own push.

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
  "AllowedOrigins": [ "https://<the-outlet-frontend-origin>" ],
  "Attendance": {
    "Enabled": false,
    "ListenPort": 8771,
    "DeviceId": 1,
    "DeviceSecret": "the-webhook-secret-shown-once-when-the-device-was-registered",
    "LdmsApiBaseUrl": "https://laundroplus-dev-api.azurewebsites.net",
    "AllowedDeviceIp": "192.168.1.50"
  },
  "Printer": {
    "Enabled": true,
    "PortName": "COM9"
  }
}
```

- `SnapshotUrl` - the camera's still-image endpoint (Hikvision/Axis/Dahua all expose
  one). Use `https://` + `"AllowSelfSignedCert": true` only if the camera forces TLS
  with its own certificate.
- `AllowedOrigins` - the origin the LDMS frontend is served from. Leave `[]` to allow
  any origin (acceptable because the service only listens on loopback).
- `ListenPort` - must match `cameraHelperURL` in the frontend's `environment.*.ts`.
- `Attendance.Enabled` - leave `false` (default) at outlets with no fingerprint
  terminal; the attendance port isn't even bound in that case.
- `Attendance.DeviceId`/`DeviceSecret` - must match the `AttendanceDevice` row already
  registered in `ldms-web-api` for this terminal (the secret is shown once, at
  registration/rotation, in the HR Devices admin screen).
- `Attendance.LdmsApiBaseUrl` - the environment's API origin, e.g. the Azure Staging or
  Production API URL — never the terminal's own address.
- `Attendance.AllowedDeviceIp` - the terminal's LAN IP. Leave blank to accept a push
  from any address (only do this if the outlet LAN is otherwise fully trusted).
- On the terminal itself: configure `PUT /ISAPI/Event/notification/httpHosts` (or the
  equivalent local web-UI screen) to push to `http://<this-pc's-LAN-IP>:8771/attendance/events`
  over plain HTTP — that's the only protocol/addressing mode this hardware supports.
- `Printer.PortName` - the COM port the tag printer is attached to (matches the WinForms
  app's old `taggingMachinePortName` setting - same hardware, same protocol, just
  reached over HTTP now instead of a direct serial connection from that process).
- `Printer.Enabled` - set `false` at an outlet with no tag printer; `/print/tag` still
  responds (loopback port is shared with the camera), just with a clear failure message
  instead of attempting to open a port.

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | `{ "status": "ok", "cameraConfigured": true, "printerConfigured": true, "attendanceEnabled": false, "attendanceQueueDepth": null }` |
| GET | `/snapshot` | Digest-fetches the camera still, returns `image/jpeg` (HTTP 502 with a JSON `error` on failure) |
| POST | `/print/tag` | Body `{ "tag": "SH1234", "tagCount": 2 }`. Always returns HTTP 200 with `{ "success": bool, "message": string }` - `success: false` covers a disabled/misconfigured printer, a busy/unresponsive machine, or a bad port, all distinguished by `message`. |
| POST | `/attendance/events` | Only mapped when `Attendance.Enabled=true`. Accepts the terminal's raw push, queues it to disk, returns 200 immediately; a background loop relays it to `ldms-web-api` and deletes it once accepted. 403 if `AllowedDeviceIp` is set and the sender doesn't match. |

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
