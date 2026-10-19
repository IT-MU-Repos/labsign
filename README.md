# LabSign

Signs LIMS report PDFs with the SafeNet USB token, driven from the web LIMS **Sign reports** page.
It replaces **Reporting > Sign Reports** in the LIMS_SOURCE desktop app (`SignReports.vb`,
`Signing.vb`) and keeps its file flow: `…\Reports\<Type>\Pending\X.pdf` is signed into
`…\Reports\<Type>\Signed\X.pdf`.

```
LIMS page ── GET pending ──────────────> Main-API (otherfiles / lr_*)
    │
    ├── POST 127.0.0.1:5578/sign-batch ──> LabSign (this app)
    │        own PIN dialog → token signs each PDF → NDJSON progress back
    ├── POST mark-signed ─────────────────> Main-API (otherfiles.signed*, lr_*.track_signed)
    └── POST 127.0.0.1:5578/finalize ────> LabSign deletes the Pending originals
```

The PIN never passes through the browser. Pending originals are only removed after Main-API has
recorded the batch, so an interruption anywhere leaves a report where the page expects it.

## Install (lab PC)

1. SafeNet Authentication Client must be installed (it provides `C:\Windows\System32\eTPKCS11.dll`).
2. Download `LabSign.exe` from the latest release and put it in `%LOCALAPPDATA%\Programs\LabSign\`.
3. Run it once. It shows a shield icon in the notification area, registers itself to start with
   Windows, and registers the `labsign://` link the LIMS page uses to start it.

No admin rights are needed; everything is per user (HKCU).

## Settings

`%APPDATA%\LabSign\config.json`, written with defaults on first run (the log is beside it):

| Key | Default | |
| --- | --- | --- |
| `port` | `5578` | 5577 is the ReLIMS print service |
| `allowed_origins` | labserve.net / it-mu.co.za hosts, `localhost:5010` | pages allowed to call LabSign |
| `pkcs11_library_path` | `C:\Windows\System32\eTPKCS11.dll` | SafeNet PKCS#11 module |
| `certificate_fallback_path` | `C:\LIMS-Data\Security\signature.cer` | only if the token has no matching cert |
| `stamp_image_path` | `C:\LIMS-Data\Security\signature.png` | handwritten signature under the stamp text |
| `tsa_url` | empty | RFC 3161 timestamp server; empty = none (as legacy) |
| `default_reason` | `Report approved` | PDF signature reason |

## API (loopback only)

- `GET /health`: `{service: "labsign", version, busy, token: {driver_present, present, label, subject, not_after, error}}`
- `POST /sign-batch`: `{signer_name, reason?, files: [{filename, pending_path, signed_path}]}`, streamed
  `application/x-ndjson` events: `start`, `pin_required`, `file` (status `signed | copied | skipped |
  already_signed | failed`, `full_path`, `sha256`, `error`), then `done` or `error`.
- `POST /finalize`: `{files: [...]}` deletes each Pending original whose Signed twin exists.

Requests carrying an `Origin` not in `allowed_origins` are refused before anything happens, as are
requests whose `Host` is not a loopback name (DNS rebinding). Paths must be a
`Reports\<Type>\Pending\<name>` / `Signed\<name>` pair.

## Develop

No SDK needed locally; everything runs in the .NET 8 SDK image:

```bash
bash build/test.sh        # unit + API tests, and PKCS#11 tests against SoftHSM2
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet publish src/LabSign/LabSign.csproj -c Release -o out/win-x64
```

- `src/LabSign.Core`: cross-platform. The API, guards, PKCS#11 token, iText signing.
- `src/LabSign`: the Windows tray app. PIN dialog, stamp image, registry integration.

Releasing: bump `<Version>` in `Directory.Build.props`, then tag `v<Version>`. CI tests, publishes
`LabSign.exe` and attaches it to the GitHub release, which the LIMS page checks to nudge updates.
