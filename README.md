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

## Install

Download from the LIMS or Mule **LabSign** page (permission-locked; the files are streamed by the
API because this repo is private) or from the GitHub release.

### Windows

1. Install SafeNet Authentication Client (it provides `C:\Windows\System32\eTPKCS11.dll`).
2. Put `LabSign.exe` in `%LOCALAPPDATA%\Programs\LabSign\` and run it. If Windows warns the app is
   unrecognised, choose *More info* > *Run anyway*.
3. A shield icon appears in the notification area. LabSign registers itself to start at login and
   registers the `labsign://` link the web pages use to start it. No admin rights needed (HKCU).

### Linux (x64)

1. Install SafeNet Authentication Client for Linux (it provides `libeToken.so`).
2. Unpack and run the installer (per user, no root):

   ```bash
   tar -xzf labsign-linux-x64.tar.gz
   ./labsign/labsign-linux-install.sh          # --uninstall to remove
   ```

   It installs to `~/.local/share/labsign/` and starts LabSign. LabSign itself registers the
   `labsign://` handler (`~/.local/share/applications/labsign.desktop`) and start at login
   (`~/.config/autostart/labsign.desktop`). The tray icon needs a StatusNotifier host (KDE, XFCE, or
   GNOME with the AppIndicator extension); LabSign works without one, just without the icon.

## Settings

`config.json` in `%APPDATA%\LabSign\` (Windows) or `~/.config/labsign/` (Linux), written with defaults
on first run (the log is beside it):

| Key | Default | |
| --- | --- | --- |
| `port` | `5578` | 5577 is the ReLIMS print service |
| `allowed_origins` | labserve.net / it-mu.co.za hosts, `localhost:5010` | pages allowed to call LabSign |
| `pkcs11_library_path` | `eTPKCS11.dll` (Windows), `libeToken.so` probed in `/usr/lib…` (Linux) | SafeNet PKCS#11 module |
| `certificate_fallback_path` | `C:\LIMS-Data\Security\signature.cer` / `~/.config/labsign/signature.cer` | only if the token has no matching cert |
| `stamp_image_path` | `C:\LIMS-Data\Security\signature.png` / `~/.config/labsign/signature.png` | handwritten signature under the stamp text |
| `tsa_url` | empty | RFC 3161 timestamp server; empty = none (as legacy) |
| `default_reason` | `Report approved` | PDF signature reason |

## API (loopback only)

- `GET /health`: `{service: "labsign", version, busy, token: {driver_present, present, label, subject, not_after, error}}`
- `POST /sign-batch`: `{signer_name, reason?, files: [{filename, pending_path, signed_path}]}`, streamed
  `application/x-ndjson` events: `start`, `pin_required`, `file` (status `signed | copied | skipped |
  already_signed | failed`, `full_path`, `sha256`, `error`), then `done` or `error`.
- `POST /finalize`: `{files: [...]}` deletes each Pending original whose Signed twin exists.
- `POST /sessions`: `{signer_name, document_count, reason?}` asks for the PIN once and returns
  `{session_id, token_label, certificate_subject}` (Mule: byte signing, nothing on disk).
- `POST /sessions/{id}/sign?filename=`: PDF bytes in, signed PDF bytes out (422 with `{error}` if, e.g.,
  `SignHere!` is missing). `DELETE /sessions/{id}` logs out; an idle session ends after 5 minutes.

Requests carrying an `Origin` not in `allowed_origins` are refused before anything happens, as are
requests whose `Host` is not a loopback name (DNS rebinding). Paths must be a
`Reports\<Type>\Pending\<name>` / `Signed\<name>` pair.

## Trying it without a SafeNet stick (Linux)

`build/test-token-linux.sh` creates a SoftHSM2 software token (`labsign-test`, PIN `1234`) with a test
certificate (`CN=LabSign Test Signatory`) and points LabSign at it. Signatures are real, from a
certificate nobody trusts. `--unplug` / `--plug` simulate pulling the stick out and putting it back;
`--remove` undoes it all. `build/make-test-report.py` writes a report PDF with the `SignHere!` marker.

```bash
sudo apt install softhsm2 opensc openssl
./build/test-token-linux.sh
python3 build/make-test-report.py test-report.pdf
```

## Develop

No SDK needed locally; everything runs in the .NET 8 SDK image:

```bash
bash build/test.sh        # unit + API tests, and PKCS#11 tests against SoftHSM2
bash build/package.sh     # out/release: LabSign.exe, labsign-linux-x64.tar.gz (+ .sha256)
```

- `src/LabSign.Core`: cross-platform. The API, guards, PKCS#11 token, iText signing.
- `src/LabSign.Desktop`: the Avalonia tray app for Windows and Linux. PIN window, SkiaSharp stamp,
  `labsign://` + start-at-login integration (HKCU on Windows, XDG desktop entries on Linux).

Releasing: bump `<Version>` in `Directory.Build.props`, then tag `v<Version>`. CI tests, publishes both
builds and attaches them to the GitHub release, which the LabSign pages list and the update nudge reads.
