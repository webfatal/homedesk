# RemoteDesk — Vollständiger Projektplan

> Browser-basierte Remote-Desktop-Lösung für Windows  
> C# / .NET 10 · VP8 · WebSocket · Docker · Self-signed SSL  
> Relay-Hosting: QNAP NAS (Docker) · Privates Projekt

---

## Systemübersicht

```
┌─────────────────────────────────────────────────────────────┐
│  Windows Host (C# / ASP.NET Core + WPF)                     │
│  DXGI Capture → VP8 (FFMpegCore) → WebSocket → Relay        │
│  SendInput ← Input-Events ← Relay ← Browser                 │
│  Clipboard Sync · File Transfer · Multi-Monitor              │
└──────────────────────────┬──────────────────────────────────┘
                           │ WebSocket ausgehend (Port 443)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  Relay Server (Docker · ASP.NET Core · Self-signed SSL)      │
│  Session-Code Matching · 1 Host → N Viewer Broadcast         │
│  Clipboard-Kanal · File-Transfer-Kanal · Input-Routing       │
└──────────────────────────┬──────────────────────────────────┘
                           │ WebSocket ausgehend (Port 443)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  Browser Clients (N gleichzeitig, keine Installation)        │
│  Canvas VP8-Decode · View-Only / Remote-Control              │
│  Clipboard API · Drag & Drop Upload · File Download          │
└─────────────────────────────────────────────────────────────┘
```

---

## Technologie-Stack

| Bereich | Technologie |
|---|---|
| Host Language | C# / .NET 10 |
| Host UI | WPF (Tray-Icon + Settings-Fenster) |
| Screen Capture | Windows.Graphics.Capture API / DXGI Desktop Duplication |
| Video Encoding | FFMpegCore (LGPL) → VP8 (libvpx) |
| Host Web Server | Kestrel (ASP.NET Core) |
| Relay Server | ASP.NET Core in Docker |
| SSL | Self-signed Zertifikat (via BouncyCastle oder dotnet tool), konfigurierbar |
| Browser Client | Vanilla JS · HTML5 Canvas · WebCodecs API (VP8) |
| Installer | Inno Setup (kostenlos, Open Source) |
| Input Injection | Windows SendInput() API |
| Clipboard | WinEventHook (Host) · Clipboard API (Browser) |

---

## Protokoll-Kanäle (WebSocket-Nachrichtentypen)

Alle Nachrichten sind binär oder JSON mit Typ-Header:

| Typ | Richtung | Inhalt |
|---|---|---|
| `video_frame` | Host → Viewer | VP8-chunk oder JPEG, Timestamp, Monitor-ID |
| `input_mouse` | Viewer → Host | x, y, button, action (move/click/scroll) |
| `input_key` | Viewer → Host | keyCode, modifiers, action (down/up) |
| `clipboard_sync` | bidirektional | text oder base64-Bild, Quelle |
| `file_offer` | Host → Viewer | Dateiname, Größe, Transfer-ID |
| `file_chunk` | Viewer → Host | Transfer-ID, Chunk-Nummer, Binärdaten |
| `session_event` | Relay → alle | viewer_joined, viewer_left, control_granted |
| `control_request` | Viewer → Host | Anfrage zur Fernsteuerung |
| `control_response` | Host → Viewer | granted / denied |
| `config_sync` | Host → Viewer | FPS, Auflösung, Monitor-Liste |

---

## SSL-Konzept (Self-signed)

Der Host verwaltet das SSL-Zertifikat für den Relay-Server zentral:

```
Host-Einstellungen (WPF UI):
  ┌─────────────────────────────────────────────┐
  │ Relay-Server Adresse: [192.168.1.50]        │
  │ Port:                 [443              ]   │
  │ SSL-Zertifikat:       ○ Let's Encrypt       │
  │                       ● Self-signed (lokal) │
  │                       ○ Eigene PFX-Datei    │
  │                                             │
  │ [Zertifikat generieren]  Gültig bis: --     │
  │ [Zertifikat exportieren → Browser-Trust]    │
  └─────────────────────────────────────────────┘
```

- Zertifikat wird via BouncyCastle in C# generiert (kein externes Tool)
- PFX-Datei wird an den Relay-Docker-Container übertragen (Volume-Mount)
- Für LAN-Nutzung: Fingerprint wird im Browser einmalig bestätigt
- Für Internet: Domain + Let's Encrypt via Caddy im Docker-Compose ist optional

---

---

# Phase 1 — LAN-Kern: Capture, Encode, Anzeige

**Ziel:** Einen funktionierenden Screen-Stream vom Windows-Host zum Browser über das lokale Netzwerk.  
**Dauer:** 2–3 Wochen  
**Ergebnis:** Browser zeigt Live-Screen, keine Fernsteuerung, kein Internet

---

### 1.1 Projektstruktur anlegen

```
RemoteDesk/
├── RemoteDesk.Host/          ← WPF-Anwendung (Hauptprojekt)
│   ├── Capture/              ← Screen-Capture-Modul
│   ├── Encoding/             ← VP8-Encoding-Modul
│   ├── Server/               ← Kestrel WebSocket-Server
│   ├── Session/              ← Session-Verwaltung
│   └── UI/                   ← WPF Tray + Settings
├── RemoteDesk.Relay/         ← ASP.NET Core Relay-Server
│   ├── Hubs/                 ← WebSocket-Session-Hub
│   └── Dockerfile
├── RemoteDesk.Client/        ← Browser-Client (HTML/JS)
│   ├── index.html
│   ├── viewer.js
│   └── codec.js
└── RemoteDesk.Installer/     ← Inno Setup Skript
```

**NuGet-Pakete (Host):**
```xml
<PackageReference Include="FFMpegCore" Version="5.*" />
<PackageReference Include="SharpDX.DXGI" Version="4.2.0" />
<PackageReference Include="Microsoft.AspNetCore.App" />
<PackageReference Include="Hardcodet.NotifyIcon.Wpf" Version="1.1.0" />
```

---

### 1.2 Screen Capture (DXGI Desktop Duplication)

**Datei:** `RemoteDesk.Host/Capture/DesktopCaptureService.cs`

Kernlogik:
- Enumeriert alle angeschlossenen Monitore via DXGI Adapter/Output
- Öffnet Desktop Duplication für den gewählten Monitor
- Läuft in eigenem Thread mit `PeriodicTimer` (FPS-gesteuert)
- Gibt `byte[] rawBitmap` (BGRA32) zurück per `Action<CapturedFrame>` Callback
- Thread-safe Frame-Buffer mit `Interlocked` (dropped frames statt Stau)

```csharp
// Schnittstelle des Capture-Moduls
public interface IDesktopCaptureService
{
    IReadOnlyList<MonitorInfo> GetAvailableMonitors();
    void StartCapture(int monitorIndex, int targetFps, Action<CapturedFrame> onFrame);
    void StopCapture();
    event EventHandler<MonitorInfo> MonitorConfigurationChanged;
}

public record CapturedFrame(byte[] BgraData, int Width, int Height, DateTime Timestamp);
public record MonitorInfo(int Index, string Name, int Width, int Height, bool IsPrimary);
```

**Fehlerbehandlung:**
- Monitor wird abgesteckt → Event auslösen, Fallback auf Primary Monitor
- UAC-Dialog erscheint → schwarzes Frame senden (DXGI liefert automatisch schwarz)
- Desktop-Wechsel (Sperrbildschirm) → schwarzes Frame + `session_event: screen_locked`

---

### 1.3 VP8-Encoding (FFMpegCore)

**Datei:** `RemoteDesk.Host/Encoding/VP8EncoderService.cs`

- Nimmt BGRA32-Frames entgegen
- Encodes via FFMpegCore als VP8 in IVF-Container
- Keyframe-Intervall: alle 2 Sekunden (für neue Viewer die sich verbinden)
- Qualitäts-Preset: `crf 33` (gut für Remote Desktop, konfigurierbar)
- Bitrate-Ziel: ~500 kbit/s bei 1080p/15fps, skaliert mit FPS

```csharp
public interface IVideoEncoderService
{
    void Initialize(int width, int height, int fps, VideoQuality quality);
    void EncodeFrame(byte[] bgraData, Action<byte[]> onEncodedChunk);
    void ForceKeyframe(); // Für neue Viewer-Verbindungen
    void Dispose();
}

public enum VideoQuality { Low, Medium, High, Lossless }
```

**JPEG-Fallback:** Wenn WebCodecs API im Browser nicht verfügbar, sendet der Server
JPEG-Frames als Fallback. Wird automatisch nach Handshake ausgehandelt.

---

### 1.4 Kestrel WebSocket-Server (LAN)

**Datei:** `RemoteDesk.Host/Server/LocalWebSocketServer.cs`

- Startet auf `https://0.0.0.0:8443` (oder konfigurierbarem Port)
- Dient auch den statischen Browser-Client-Dateien (`/index.html`, `/viewer.js`)
- WebSocket-Endpunkt: `wss://[host-ip]:8443/stream`
- Im LAN-Modus: direkter Kanal, kein Relay nötig

**Handshake-Ablauf:**
```
Browser → GET /stream (WebSocket Upgrade)
Server  → 101 Switching Protocols
Browser → { "type": "hello", "capabilities": ["vp8", "jpeg"] }
Server  → { "type": "config", "codec": "vp8", "fps": 15, "monitors": [...] }
Server  → [binary VP8 chunks im Dauerbetrieb]
```

---

### 1.5 Browser-Client Phase 1

**Dateien:** `index.html`, `viewer.js`, `codec.js`

Minimaler Client für Phase 1:
- Eingabefeld für Host-IP + Port
- WebSocket-Verbindung aufbauen
- WebCodecs `VideoDecoder` für VP8 initialisieren
- Decoded Frames auf `<canvas>` zeichnen
- FPS-Counter und Latenz-Anzeige

```javascript
// Kernstruktur viewer.js
class RemoteDeskViewer {
    constructor(canvasEl) { ... }
    async connect(wsUrl) { ... }
    handleVideoFrame(data) { ... }   // VP8 → VideoDecoder → Canvas
    handleSessionEvent(msg) { ... }
    disconnect() { ... }
}
```

---

### 1.6 Deliverables Phase 1

- [ ] Screen wird live im Browser angezeigt (LAN)
- [ ] FPS einstellbar 5–30 (Host-UI Slider)
- [ ] Monitor-Auswahl im Host-UI (Dropdown)
- [ ] VP8-Stream mit JPEG-Fallback
- [ ] FPS- und Latenzanzeige im Browser
- [ ] Kein Port-Forwarding nötig (Browser verbindet direkt per IP im LAN)

---

---

# Phase 2 — Relay-Server & Internet-Verbindung

**Ziel:** Verbindung über das Internet ohne Port-Forwarding, self-signed SSL.  
**Dauer:** 1–2 Wochen  
**Ergebnis:** Verbindung via Session-Code funktioniert von überall

---

### 2.1 Relay-Server Architektur

**Datei:** `RemoteDesk.Relay/Hubs/RelayHub.cs`

Der Relay-Server ist ein reiner Datendurchleiter — er versteht den Inhalt nicht,
proxied nur verschlüsselte WebSocket-Frames.

```
Session-Tabelle im Relay (In-Memory):
{
  "482619073": {
    host: WebSocket connection,
    viewers: [WebSocket1, WebSocket2],
    createdAt: DateTime,
    hostApprovalPending: [WebSocket3]
  }
}
```

**Endpunkte:**
- `wss://relay:443/host` — Host registriert sich hier
- `wss://relay:443/viewer?code=482619073` — Viewer verbindet sich hier
- `GET /health` — Health-Check für Docker

**Session-Lebenszyklus:**
```
1. Host verbindet → generiert 9-stelligen Code → sendet Code an Relay
2. Relay speichert { Code → Host-Connection }
3. Viewer verbindet mit Code → Relay sendet "viewer_join_request" an Host
4. Host akzeptiert → Relay aktiviert Viewer in Session
5. Host trennt → alle Viewer erhalten "host_disconnected" → Session gelöscht
6. Session-Timeout: 24h ohne Aktivität
```

---

### 2.2 Docker-Setup

**Datei:** `RemoteDesk.Relay/Dockerfile`

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app
COPY publish/ .
ENTRYPOINT ["dotnet", "RemoteDesk.Relay.dll"]
```

**Datei:** `RemoteDesk.Relay/docker-compose.yml`

```yaml
services:
  relay:
    build: .
    ports:
      - "443:443"
    volumes:
      - ./certs:/app/certs:ro       # SSL-Zertifikat (self-signed PFX)
      - ./config:/app/config:ro     # relay-config.json
    environment:
      - CERT_PATH=/app/certs/relay.pfx
      - CERT_PASSWORD=${CERT_PASSWORD}
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "wget", "-q", "--spider", "https://localhost/health"]
      interval: 30s
```

---

### 2.3 Self-signed SSL im Host konfigurieren

**Datei:** `RemoteDesk.Host/SSL/CertificateManager.cs`

Der Host generiert und verwaltet das Zertifikat für den Relay-Server:

```csharp
public class CertificateManager
{
    // Generiert self-signed X.509 Zertifikat via BouncyCastle
    public X509Certificate2 GenerateSelfSignedCertificate(
        string subjectName,    // z.B. "CN=RemoteDesk Relay"
        string[] sanEntries,   // IP-Adressen oder Hostnamen
        int validDays = 3650); // 10 Jahre Standard

    // Exportiert als PFX für Docker-Volume
    public void ExportToPfx(X509Certificate2 cert, string path, string password);

    // Exportiert öffentlichen Schlüssel als CER für Browser-Trust
    public void ExportPublicKeyCer(X509Certificate2 cert, string path);

    // Zeigt Zertifikat-Fingerprint (SHA256) für manuelle Browser-Bestätigung
    public string GetFingerprint(X509Certificate2 cert);
}
```

**NuGet:**
```xml
<PackageReference Include="BouncyCastle.Cryptography" Version="2.*" />
```

**Ablauf Self-signed Setup:**
```
Host-UI: [Zertifikat generieren]
    → CertificateManager.GenerateSelfSignedCertificate()
    → PFX wird gespeichert: C:\ProgramData\RemoteDesk\relay.pfx
    → Fingerprint wird angezeigt: "AB:CD:EF:..."
    → [PFX auf Server übertragen] Button → SCP/SFTP oder manuell

Relay Docker startet mit PFX → Kestrel nutzt self-signed cert
Browser → HTTPS-Warnung beim ersten Mal → Benutzer bestätigt manuell
          (oder: CER-Export → Browser-Zertifikat-Import)
```

---

### 2.4 Host verbindet sich mit Relay

**Datei:** `RemoteDesk.Host/Session/RelayConnection.cs`

```csharp
public class RelayConnection
{
    // Verbindet Host ausgehend zum Relay
    public async Task ConnectAsync(string relayUrl, bool trustSelfSigned);

    // Sendet Session-Registrierung
    public async Task<string> RegisterSessionAsync(); // gibt Code zurück

    // Empfängt Viewer-Anfragen, löst Event aus
    public event EventHandler<ViewerJoinRequest> ViewerJoinRequested;

    // Proxied Video-Frames an alle verbundenen Viewer
    public async Task BroadcastFrameAsync(byte[] vpx8Chunk);
}
```

**Self-signed Trust im Client:**
```csharp
// HttpClientHandler konfigurieren für self-signed
handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
{
    if (_settings.TrustSelfSigned && cert != null)
        return cert.Thumbprint == _settings.RelayThumbprint;
    return errors == SslPolicyErrors.None;
};
```

---

### 2.5 Session-Code UI

**Host-Seite (WPF):**
```
┌──────────────────────────────────────┐
│  RemoteDesk                    [_][X]│
├──────────────────────────────────────┤
│  Session-Code:  482 619 073          │
│  Status: ● Verbunden mit Relay       │
│  Monitor: [Display 1 - 1920×1080 ▼] │
│  FPS: [15 ════════════░░░░░░] 30     │
│                                      │
│  Aktive Viewer: 0                    │
│  [ Session beenden ]                 │
└──────────────────────────────────────┘
```

**Browser-Seite:**
```
┌──────────────────────────────────────┐
│  RemoteDesk — Verbinden              │
│                                      │
│  Session-Code:  [___] [___] [___]    │
│                                      │
│  Relay-Server: [relay.example.com  ] │
│                                      │
│              [ Verbinden ]           │
└──────────────────────────────────────┘
```

---

### 2.6 Deliverables Phase 2

- [ ] Relay-Server läuft in Docker
- [ ] Self-signed SSL wird vom Host generiert und verwaltet
- [ ] Host verbindet sich ausgehend mit Relay (kein offener Port am Host)
- [ ] Browser verbindet sich via 9-stelligem Session-Code
- [ ] Host sieht Viewer-Anfrage und kann bestätigen/ablehnen
- [ ] Verbindung funktioniert über das Internet
- [ ] LAN-Direktverbindung bleibt weiterhin möglich

---

---

# Phase 3 — Multi-Monitor & FPS-Steuerung

**Ziel:** Vollständige Multi-Monitor-Unterstützung und performante FPS-Regelung.  
**Dauer:** 1 Woche  
**Ergebnis:** Nutzer kann Monitor wechseln, FPS dynamisch anpassen

---

### 3.1 Monitor-Enumeration und Switching

```csharp
public class MultiMonitorManager
{
    // Gibt alle aktuell angeschlossenen Monitore zurück
    public IReadOnlyList<MonitorInfo> GetMonitors();

    // Wechselt aktiven Monitor (auch während laufender Session)
    public void SwitchToMonitor(int monitorIndex);

    // Reagiert auf Monitor-Konfigurationsänderungen (Plug/Unplug)
    public event EventHandler<MonitorConfigChangedArgs> ConfigurationChanged;
}
```

**Konfigurationsänderung während Session:**
- Host wechselt Monitor → sendet `config_sync` an alle Viewer
- Browser empfängt neue Auflösung → Canvas wird angepasst
- Ein Keyframe wird sofort erzwungen

---

### 3.2 Adaptive FPS-Steuerung

```csharp
public class AdaptiveCaptureTimer
{
    private int _targetFps;

    // Ändert FPS on-the-fly ohne Neustart
    public void SetTargetFps(int fps) // 5–30
    {
        _targetFps = Math.Clamp(fps, 5, 30);
        _timer.Period = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
    }

    // Überspringt Frame wenn Encoding noch läuft (backpressure)
    public bool TryCapture(out CapturedFrame frame);
}
```

**FPS-Anzeige im Browser:**
- Tatsächliche FPS (gemessen über Rolling Average der letzten 30 Frames)
- Ziel-FPS vom Host
- Beide werden in der Statusleiste angezeigt

---

### 3.3 Auflösungs-Skalierung

Optionale Downscaling-Stufen für Bandbreitenersparnis:

| Setting | Skalierung | Typische Bitrate |
|---|---|---|
| Nativ | 100% | ~800 kbit/s |
| HD | 75% | ~400 kbit/s |
| SD | 50% | ~200 kbit/s |

Konfigurierbar im Host-UI und per Browser-Anfrage.

---

### 3.4 Browser — Responsive Canvas

- Canvas passt sich an Browser-Fenstergröße an (CSS `object-fit: contain`)
- Auflösungsänderungen (Monitor-Wechsel) werden nahtlos übernommen
- Mauskoordinaten werden skaliert zurückgerechnet:
  ```javascript
  // Mausposition im Browser → absolute Host-Koordinaten
  const scaleX = hostWidth / canvas.clientWidth;
  const scaleY = hostHeight / canvas.clientHeight;
  const hostX = Math.round(event.offsetX * scaleX);
  const hostY = Math.round(event.offsetY * scaleY);
  ```

---

### 3.5 Deliverables Phase 3

- [ ] Alle Monitore werden in Host-UI aufgelistet (Name, Auflösung)
- [ ] Monitor-Wechsel während laufender Session ohne Reconnect
- [ ] FPS-Slider 5–30 wirkt sofort
- [ ] Tatsächliche vs. Ziel-FPS im Browser sichtbar
- [ ] Canvas-Skalierung korrekt (Mauszeiger landet an der richtigen Stelle)
- [ ] Optionale Auflösungs-Skalierung

---

---

# Phase 4 — Fernsteuerung, Mehrere Viewer & Rollen

**Ziel:** Input-Fernsteuerung, mehrere gleichzeitige Viewer, Kontroll-Verwaltung.  
**Dauer:** 1 Woche  
**Ergebnis:** Vollständige Fernsteuerung, View-Only-Modus, Kontrollanfragen

---

### 4.1 Input Injection (Host)

**Datei:** `RemoteDesk.Host/Input/InputInjector.cs`

```csharp
public class InputInjector
{
    // Mausbewegung (absolute Koordinaten auf virtualem Desktop)
    public void MoveMouse(int x, int y);

    // Mausklick (left/right/middle, down/up)
    public void MouseButton(MouseButton btn, bool down);

    // Mausrad
    public void MouseWheel(int delta, bool horizontal = false);

    // Tastatur (Windows Virtual Key Codes)
    public void KeyPress(ushort vkCode, bool down);

    // Spezialfall: Sende STRG+ALT+ENTF (erfordert Dienst-Kontext)
    public void SendSAS(); // Secure Attention Sequence
}
```

**Koordinaten-Transformation:**
- Browser sendet `(x, y)` relativ zur Canvas-Größe (0.0–1.0 normalisiert)
- Host skaliert auf absolute DXGI-Koordinaten des gewählten Monitors
- Berücksichtigt DPI-Skalierung (High-DPI / 4K Monitore)

---

### 4.2 Multi-Viewer Kontroll-Modell

**Relay-seitige Verwaltung:**

```
Session-State:
{
  host: connection,
  viewers: {
    "viewer-uuid-1": { connection, mode: "view_only" },
    "viewer-uuid-2": { connection, mode: "control" },    ← max. einer
    "viewer-uuid-3": { connection, mode: "view_only" },
  },
  controlGrantedTo: "viewer-uuid-2"  // null wenn niemand
}
```

**Kontroll-Anfrage-Ablauf:**
```
Viewer 2 klickt "Fernsteuerung anfragen"
    → { type: "control_request", viewerId: "viewer-uuid-2" }
Host empfängt → Popup erscheint im WPF-Fenster:
    "Viewer 2 (Chrome, 192.168.1.45) möchte die Fernsteuerung übernehmen"
    [ Erlauben ]  [ Ablehnen ]
Host klickt "Erlauben"
    → { type: "control_response", viewerId: "viewer-uuid-2", granted: true }
Relay aktualisiert controlGrantedTo
    → Viewer 2: Eingaben werden weitergeleitet
    → Alle anderen: Eingaben werden ignoriert
    → Alle Viewer erhalten: { type: "control_changed", controller: "viewer-uuid-2" }
```

**Host kann jederzeit:**
- Kontrolle entziehen (Button "Fernsteuerung beenden")
- Einzelne Viewer trennen
- Session komplett beenden

---

### 4.3 Browser-UI für Rollen

```
Kontroll-Leiste (unten im Browser):

[View-Only-Modus]                    → graue Leiste, Cursor = default
[🖱 Fernsteuerung anfragen]          → Anfrage senden
[⏳ Warte auf Host-Bestätigung...]   → nach Anfrage
[✅ Fernsteuerung aktiv] [Abgeben]   → wenn gewährt, Maus kaptiert Canvas

Viewer-Liste (einblendbar):
  👁 Du (View Only)
  🖱 Max (Fernsteuerung)  ← farblich hervorgehoben
  👁 Anna (View Only)
```

---

### 4.4 Tastatur-Capture im Browser

Wenn Fernsteuerung aktiv:
- Canvas erhält `tabIndex=0` → kann Fokus halten
- `keydown`/`keyup` Events werden abgefangen
- Spezielle Tastenkombinationen (Alt+Tab, Win) werden über `keyboard.lock()` (Keyboard Lock API) oder Hinweis an Nutzer behandelt
- Escape-Taste: immer Fernsteuerung freigeben (Sicherheits-Shortcut)

---

### 4.5 Deliverables Phase 4

- [ ] Mausbewegung, Klick, Scroll funktionieren korrekt
- [ ] Tastatureingaben werden korrekt injiziert (inkl. Sondertasten)
- [ ] Kontroll-Anfrage mit Host-Approval-Popup
- [ ] Maximal ein Viewer mit Kontrolle gleichzeitig
- [ ] Alle anderen Viewer sind View-Only
- [ ] Host kann Kontrolle jederzeit entziehen
- [ ] Viewer-Liste im Browser sichtbar
- [ ] Escape = sofortige Freigabe der Fernsteuerung
- [ ] DPI-korrekte Mauskoordinaten auch auf 4K-Monitoren

---

---

# Phase 5 — Clipboard & Dateiübertragung

**Ziel:** Zwischenablage-Synchronisation und Dateiübertragung in beide Richtungen.  
**Dauer:** 1 Woche  
**Ergebnis:** Copy/Paste und Dateiübertragung zwischen Host und Browser

---

### 5.1 Clipboard-Synchronisation (Host-Seite)

**Datei:** `RemoteDesk.Host/Clipboard/ClipboardMonitor.cs`

```csharp
public class ClipboardMonitor
{
    // WinEventHook auf CLIPBOARDUPDATE-Nachrichten
    public event EventHandler<ClipboardChangedArgs> ClipboardChanged;

    // Liest aktuelle Zwischenablage
    public ClipboardContent GetCurrentContent(); // Text, RTF oder Bild

    // Schreibt empfangene Inhalte in Windows-Zwischenablage
    public void SetContent(ClipboardContent content);
}

public record ClipboardContent(
    ClipboardContentType Type,  // Text, RTF, Image
    string? Text,
    byte[]? ImagePng,
    string? SourceId             // Verhindert Echo-Loop
);
```

**Echo-Loop-Schutz:**
- Jede Clipboard-Änderung bekommt eine Source-ID
- Wenn Host eine Änderung empfängt die er selbst gesendet hat → ignorieren

---

### 5.2 Clipboard im Browser

```javascript
class ClipboardSync {
    // Empfängt Clipboard-Update vom Host → Browser-Zwischenablage
    async onHostClipboard(content) {
        if (content.type === 'text') {
            await navigator.clipboard.writeText(content.text);
        }
    }

    // Überwacht Browser-Clipboard-Änderungen → sendet an Host
    // (nur möglich wenn Tab im Fokus und Nutzer-Interaktion stattfand)
    async onBrowserPaste(event) {
        const text = await navigator.clipboard.readText();
        this.ws.send(JSON.stringify({ type: 'clipboard_sync', text }));
    }
}
```

**Hinweis:** Browser-Clipboard-API erfordert HTTPS und Nutzer-Interaktion.
Self-signed Zertifikat muss daher einmalig im Browser akzeptiert werden.

---

### 5.3 Dateiübertragung Browser → Host

**Mechanismus:** Drag & Drop auf Canvas oder Button-Upload

```javascript
canvas.addEventListener('drop', async (event) => {
    const files = event.dataTransfer.files;
    for (const file of files) {
        await uploadFile(file); // chunked WebSocket Upload
    }
});

async function uploadFile(file) {
    const CHUNK_SIZE = 256 * 1024; // 256 KB pro Chunk
    const transferId = crypto.randomUUID();
    // offer senden
    ws.send(JSON.stringify({
        type: 'file_offer', transferId,
        name: file.name, size: file.size
    }));
    // Chunks senden
    for (let offset = 0; offset < file.size; offset += CHUNK_SIZE) {
        const chunk = file.slice(offset, offset + CHUNK_SIZE);
        ws.send(await chunk.arrayBuffer()); // Binary frame
    }
}
```

**Host empfängt:**
- Speichert Datei in `%USERPROFILE%\Downloads\RemoteDesk\`
- Zeigt Windows-Benachrichtigung: "Datei 'report.pdf' empfangen"

---

### 5.4 Dateiübertragung Host → Browser

**Host-UI:**
- Rechtsklick im Tray-Icon → "Datei senden"
- Datei-Dialog öffnet sich
- Datei wird in Chunks über WebSocket an alle (oder ausgewählte) Viewer gesendet

**Browser:**
- Empfängt `file_offer` → zeigt Benachrichtigung: "Host sendet 'report.pdf' (2.4 MB)"
- Chunks werden gesammelt → `Blob` erstellt → automatischer Download-Dialog
- Fortschrittsanzeige in Prozent

---

### 5.5 Fortschritt & Limits

- Maximale Dateigröße: konfigurierbar (Standard: 500 MB)
- Fortschrittsbalken in beiden Richtungen (Browser-UI + Host-Tray-Tooltip)
- Abbruch jederzeit möglich
- Mehrere gleichzeitige Transfers möglich (via Transfer-ID getrennt)

---

### 5.6 Deliverables Phase 5

- [ ] Text-Clipboard synchronisiert Host ↔ Browser bidirektional
- [ ] Bilder in Zwischenablage (PNG) werden übertragen
- [ ] Echo-Loop wird korrekt verhindert
- [ ] Drag & Drop Upload Browser → Host funktioniert
- [ ] Button-Upload als Alternative zu Drag & Drop
- [ ] Host → Browser Dateiübertragung mit Download-Dialog
- [ ] Fortschrittsanzeige in beide Richtungen
- [ ] Windows-Benachrichtigung bei empfangener Datei
- [ ] Konfigurierbares Größenlimit

---

---

# Phase 6 — Installer, Firewall & Finalisierung

**Ziel:** Professioneller Windows-Installer, automatische Firewall-Konfiguration,
poliertes UI und stabiler Produktivbetrieb.  
**Dauer:** 1 Woche  
**Ergebnis:** Fertige, installierbare Anwendung mit professionellem Setup-Erlebnis

---

### 6.1 Inno Setup Installer

**Datei:** `RemoteDesk.Installer/setup.iss`

Der Installer übernimmt vollautomatisch:

```iss
[Setup]
AppName=RemoteDesk
AppVersion=1.0
DefaultDirName={autopf}\RemoteDesk
DefaultGroupName=RemoteDesk
OutputBaseFilename=RemoteDesk-Setup-1.0

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs
Source: "..\certs\*"; DestDir: "{commonappdata}\RemoteDesk\certs"

[Run]
; Windows Defender Firewall: eingehend (LAN-Direktverbindung)
Filename: "netsh"; \
  Parameters: "advfirewall firewall add rule name=""RemoteDesk Host (eingehend)"" dir=in action=allow program=""{app}\RemoteDesk.Host.exe"" enable=yes profile=private"; \
  Flags: runhidden; StatusMsg: "Firewall wird konfiguriert..."

; Ausgehend (Relay-Verbindung, normalerweise nicht nötig, aber explizit)
Filename: "netsh"; \
  Parameters: "advfirewall firewall add rule name=""RemoteDesk Host (ausgehend)"" dir=out action=allow program=""{app}\RemoteDesk.Host.exe"" enable=yes"; \
  Flags: runhidden

; Autostart-Eintrag (optional, per Checkbox im Installer)
Filename: "schtasks"; \
  Parameters: "/Create /TN ""RemoteDesk\Autostart"" /TR ""{app}\RemoteDesk.Host.exe"" /SC ONLOGON /RL HIGHEST /F"; \
  Flags: runhidden; Tasks: autostart

[UninstallRun]
; Firewall-Regeln beim Deinstallieren entfernen
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""RemoteDesk Host (eingehend)"""; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""RemoteDesk Host (ausgehend)"""; Flags: runhidden

[Tasks]
Name: autostart; Description: "RemoteDesk automatisch mit Windows starten"; Flags: unchecked
```

---

### 6.2 Host-Einstellungs-UI (vollständig)

**WPF Settings-Fenster — Tabs:**

**Tab 1: Verbindung**
```
Relay-Server Adresse:  [relay.meinserver.de    ]
Port:                  [443]
SSL-Modus:             ○ Self-signed (empfohlen für eigenen Server)
                       ○ Vertrauenswürdiges Zertifikat (Let's Encrypt)
                       ○ Eigene PFX-Datei [Durchsuchen...]

[Zertifikat generieren]  → generiert PFX + CER
[CER exportieren]        → für Browser-Trust
Fingerprint: AB:CD:EF:12:34... [Kopieren]

[Verbindung testen]  ● Verbunden  /  ✗ Fehler
```

**Tab 2: Anzeige**
```
Standard-Monitor:   [Display 1 - 1920×1080 (Primär) ▼]
Bildrate (FPS):     [15 ══════════════░░░░░] 30
Qualität:           ○ Niedrig  ● Mittel  ○ Hoch
Skalierung:         [Nativ ▼]  (Nativ / 75% HD / 50% SD)
Codec:              ● VP8  ○ JPEG
```

**Tab 3: Sicherheit**
```
Viewer-Anfragen:    ● Immer bestätigen (empfohlen)
                    ○ Automatisch ablehnen
Dateiübertragung:   ● Erlauben
Clipboard-Sync:     ● Erlauben
Max. Dateigröße:    [500] MB
Session-Timeout:    [24] Stunden
```

**Tab 4: Info / Diagnose**
```
Version: 1.0.0
Verbindungslog (letzte 50 Einträge)
[Log exportieren]   [Fehler melden]
```

---

### 6.3 Tray-Icon (Laufzeitanzeige)

```
[RemoteDesk Icon im System-Tray]

Rechtsklick-Menü:
  ───────────────────────────────
  ● Session aktiv: 482 619 073
  Viewer: 2 verbunden
  ───────────────────────────────
  Session-Code anzeigen
  Fernsteuerung beenden
  Alle Viewer trennen
  ───────────────────────────────
  Datei senden...
  ───────────────────────────────
  Einstellungen
  Beenden
```

---

### 6.4 Browser-Client Finalisierung

Poliertes UI mit allen Features aus allen Phasen:

```
┌───────────────────────────────────────────────────────────┐
│ RemoteDesk                           [Vollbild] [Trennen] │
├───────────────────────────────────────────────────────────┤
│                                                           │
│              Remote Desktop Canvas                        │
│                                                           │
├───────────────────────────────────────────────────────────┤
│ 👁 View Only  [Steuerung anfragen]    FPS: 15/15  42ms   │
│ 📋 Clipboard  📁 Datei senden  👥 2 Viewer               │
└───────────────────────────────────────────────────────────┘
```

**Features:**
- Vollbild-Modus (F11 oder Button)
- Tastatur-Shortcut `Escape` = Fernsteuerung freigeben
- Mobile-kompatibel (Touch-Events → Maus-Events)
- Dark/Light Mode (CSS Media Query)
- Verbindungsqualitäts-Indikator (FPS-Drop, Latenz-Spike)

---

### 6.5 Qualitätssicherung & Testing

**Testszenarien:**

| Szenario | Prüfpunkt |
|---|---|
| LAN-Verbindung | Direkt per IP, kein Relay |
| Internet-Verbindung | Via Relay, NAT-Traversal |
| Self-signed SSL | Verbindung trotz Browser-Warnung |
| Monitor-Wechsel | Nahtlos während Session |
| FPS-Änderung | Sofortige Wirkung, kein Freeze |
| Viewer 2 tritt bei | Keyframe wird sofort gesendet |
| Kontrolle übergeben | Approve-Flow korrekt |
| Große Datei (500 MB) | Fortschritt korrekt, kein Timeout |
| Clipboard Bild (PNG) | Korrekt übertragen |
| Relay-Verbindung bricht | Auto-Reconnect (5 Versuche) |
| Host schliesst App | Alle Viewer erhalten Trennnachricht |

---

### 6.6 Deliverables Phase 6

- [ ] Inno Setup Installer erstellt `.exe`-Setup-Datei
- [ ] Installer konfiguriert Windows Defender Firewall automatisch
- [ ] Uninstaller entfernt alle Firewall-Regeln
- [ ] Optionaler Autostart-Eintrag im Installer
- [ ] Vollständiges Settings-UI mit allen Tabs
- [ ] Self-signed Zertifikat-Generierung und -Export im Host
- [ ] Poliertes Browser-UI mit Vollbild, Statusleiste, Viewer-Liste
- [ ] Auto-Reconnect bei Verbindungsabbruch (Host ↔ Relay)
- [ ] Mobile Touch-Unterstützung im Browser
- [ ] Alle Testszenarien bestanden

---

---

## Gesamtübersicht

| Phase | Inhalt | Dauer |
|---|---|---|
| **1** | LAN-Kern: Capture, VP8, Canvas | 2–3 Wochen |
| **2** | Relay-Server, Docker, SSL, Session-Code | 1–2 Wochen |
| **3** | Multi-Monitor, FPS-Steuerung, Skalierung | 1 Woche |
| **4** | Fernsteuerung, Multi-Viewer, Rollen | 1 Woche |
| **5** | Clipboard, Dateiübertragung | 1 Woche |
| **6** | Installer, Firewall, UI-Polish | 1 Woche |
| **Gesamt** | | **7–10 Wochen** |

---

## Projektentscheidungen (geklärt April 2026)

| Frage | Entscheidung |
|---|---|
| **Relay-Server Hosting** | QNAP NAS als Docker Container (bereits vorhanden) |
| **Domain / Erreichbarkeit** | DNS-Name des QNAP NAS, sowohl im LAN als auch über Internet erreichbar |
| **SSL-Zertifikat** | Self-signed, vom Host generiert und auf QNAP deployt; DNS-Name als SAN im Zertifikat |
| **Veröffentlichung** | Vorerst privat |
| **.NET-Version** | .NET 10 |

### QNAP-spezifische Hinweise

- Relay läuft als Docker Container in Container Station auf dem QNAP
- Port 443 muss im QNAP-Router/Firewall nach außen freigegeben sein (einmalige Einrichtung)
- QNAP Container Station unterstützt `docker-compose` → Setup direkt nutzbar
- DNS-Name des NAS als CN und SAN im self-signed Zertifikat eintragen
- QNAP-interne Ports: kein Konflikt mit Port 443 prüfen (QNAP Management läuft auf 8080/8443)

---

*Erstellt: April 2026 · Zuletzt aktualisiert: April 2026*
