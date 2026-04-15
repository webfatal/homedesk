# RemoteDesk · Phase 1 — LAN-Kern: Capture, Encode, Anzeige

---

## Projektzusammenfassung

**RemoteDesk** ist eine selbst entwickelte, browser-basierte Remote-Desktop-Lösung für Windows. Sie ermöglicht das Teilen und Fernsteuern eines Windows-Desktops über einen normalen Webbrowser — ohne Installation auf der Viewer-Seite und ohne Eingriff in die Netzwerkkonfiguration des Hosts.

| Eigenschaft | Wert |
|---|---|
| **Sprache / Framework** | C# / .NET 10 |
| **Host-UI** | WPF (Tray-Icon + Settings-Fenster) |
| **Codec** | VP8 (libvpx, LGPL) mit JPEG-Fallback |
| **Relay-Hosting** | QNAP NAS · Docker Container Station |
| **DNS / Erreichbarkeit** | DNS-Name des QNAP NAS (LAN + Internet) |
| **SSL** | Self-signed, vom Host generiert |
| **Browser-Client** | Vanilla JS · HTML5 Canvas · WebCodecs API |
| **Sichtbarkeit** | Privates Projekt |

**Phasenübersicht:**

| Phase | Inhalt | Dauer |
|---|---|---|
| **1 ← Sie sind hier** | LAN-Kern: Capture, VP8, Canvas | 2–3 Wochen |
| 2 | Relay-Server, Docker (QNAP), SSL, Session-Code | 1–2 Wochen |
| 3 | Multi-Monitor, FPS-Steuerung, Skalierung | 1 Woche |
| 4 | Fernsteuerung, Multi-Viewer, Rollen | 1 Woche |
| 5 | Clipboard, Dateiübertragung | 1 Woche |
| 6 | Installer, Firewall-Konfiguration, UI-Polish | 1 Woche |

---

## Phase 1 — Ziel & Abgrenzung

**Ziel:** Einen funktionierenden Screen-Stream vom Windows-Host zum Browser über das lokale Netzwerk aufbauen. Am Ende dieser Phase sieht ein Browser-Tab den Live-Inhalt des Windows-Desktops in Echtzeit.

**In dieser Phase enthalten:**
- Projektstruktur und Solution anlegen
- Screen Capture via DXGI Desktop Duplication API
- VP8-Encoding via FFMpegCore
- Eingebetteter Kestrel WebSocket-Server (LAN-Direktverbindung)
- Browser-Client mit VP8-Decode und Canvas-Anzeige
- FPS-Steuerung und Qualitäts-Einstellung
- Grundlegendes WPF-Tray-Icon

**Noch nicht in dieser Phase:**
- Internet-Verbindung / Relay-Server (→ Phase 2)
- Multi-Monitor-Auswahl (→ Phase 3)
- Fernsteuerung / Input-Injection (→ Phase 4)
- Clipboard / Dateiübertragung (→ Phase 5)
- Installer (→ Phase 6)

**Voraussetzungen:**
- Visual Studio 2022 oder JetBrains Rider mit .NET 10 SDK
- Windows 10/11 (für DXGI Desktop Duplication)
- FFMpegCore NuGet-Paket (bringt FFmpeg-Binary mit)
- Moderner Browser (Chrome 94+, Edge 94+, Firefox 130+) für WebCodecs API

---

## 1.1 Projektstruktur anlegen

**Solution-Struktur:**

```
RemoteDesk.sln
├── RemoteDesk.Host/               ← WPF-Anwendung (Hauptprojekt)
│   ├── Capture/
│   │   ├── IDesktopCaptureService.cs
│   │   ├── DesktopCaptureService.cs
│   │   └── Models/
│   │       ├── CapturedFrame.cs
│   │       └── MonitorInfo.cs
│   ├── Encoding/
│   │   ├── IVideoEncoderService.cs
│   │   ├── VP8EncoderService.cs
│   │   └── JpegEncoderService.cs
│   ├── Server/
│   │   ├── LocalWebSocketServer.cs
│   │   └── WebSocketSession.cs
│   ├── Session/
│   │   └── SessionManager.cs
│   ├── UI/
│   │   ├── MainWindow.xaml
│   │   ├── TrayIconManager.cs
│   │   └── SettingsWindow.xaml
│   ├── wwwroot/                   ← Browser-Client-Dateien (eingebettet)
│   │   ├── index.html
│   │   ├── viewer.js
│   │   └── codec.js
│   └── RemoteDesk.Host.csproj
│
├── RemoteDesk.Relay/              ← ASP.NET Core Relay (Phase 2)
│   ├── Hubs/
│   ├── Dockerfile
│   └── RemoteDesk.Relay.csproj
│
├── RemoteDesk.Shared/             ← Gemeinsame Modelle & Protokoll
│   ├── Protocol/
│   │   └── MessageTypes.cs
│   └── RemoteDesk.Shared.csproj
│
└── RemoteDesk.Installer/          ← Inno Setup (Phase 6)
    └── setup.iss
```

**Host .csproj (Kern-Pakete für Phase 1):**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- Screen Capture (DXGI) -->
    <PackageReference Include="SharpDX" Version="4.2.0" />
    <PackageReference Include="SharpDX.DXGI" Version="4.2.0" />
    <PackageReference Include="SharpDX.Direct3D11" Version="4.2.0" />

    <!-- VP8 Encoding -->
    <PackageReference Include="FFMpegCore" Version="5.*" />
    <PackageReference Include="FFMpegCore.Extensions.SkiaSharp" Version="5.*" />

    <!-- Embedded Web Server -->
    <PackageReference Include="Microsoft.AspNetCore.App" />

    <!-- WPF Tray Icon -->
    <PackageReference Include="Hardcodet.NotifyIcon.Wpf" Version="1.1.0" />
  </ItemGroup>

  <!-- Browser-Client als eingebettete Ressource -->
  <ItemGroup>
    <EmbeddedResource Include="wwwroot\**\*" />
  </ItemGroup>
</Project>
```

---

## 1.2 Screen Capture (DXGI Desktop Duplication API)

**Warum DXGI:** Direkter GPU-Zugriff, deutlich effizienter als GDI oder WinRT-Capture. Liefert Frames als BGRA32-Textur auf der GPU, die direkt an den Encoder weitergegeben werden kann.

**Modelle:**

```csharp
// RemoteDesk.Shared/Protocol/MessageTypes.cs
public record MonitorInfo(
    int Index,
    string Name,
    int Width,
    int Height,
    bool IsPrimary,
    int OffsetX,    // Position im virtuellen Desktop
    int OffsetY
);

public record CapturedFrame(
    byte[] BgraData,
    int Width,
    int Height,
    int MonitorIndex,
    DateTime Timestamp,
    bool IsKeyframeRequired  // true für neue Viewer-Verbindungen
);
```

**Interface:**

```csharp
// RemoteDesk.Host/Capture/IDesktopCaptureService.cs
public interface IDesktopCaptureService : IDisposable
{
    IReadOnlyList<MonitorInfo> GetAvailableMonitors();

    void StartCapture(
        int monitorIndex,
        int targetFps,               // 5–30
        Action<CapturedFrame> onFrame);

    void StopCapture();
    bool IsCapturing { get; }

    event EventHandler<MonitorInfo> MonitorConfigurationChanged;
}
```

**Implementierung — Kernlogik:**

```csharp
// RemoteDesk.Host/Capture/DesktopCaptureService.cs
public class DesktopCaptureService : IDesktopCaptureService
{
    private Thread? _captureThread;
    private volatile bool _running;
    private int _targetFps = 15;

    public void StartCapture(int monitorIndex, int targetFps,
        Action<CapturedFrame> onFrame)
    {
        _targetFps = targetFps;
        _running = true;
        _captureThread = new Thread(() => CaptureLoop(monitorIndex, onFrame))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "DesktopCapture"
        };
        _captureThread.Start();
    }

    private void CaptureLoop(int monitorIndex, Action<CapturedFrame> onFrame)
    {
        // DXGI Adapter und Output initialisieren
        using var factory = new Factory1();
        using var adapter = factory.GetAdapter1(0);
        using var device = new SharpDX.Direct3D11.Device(adapter);
        using var output = adapter.GetOutput(monitorIndex);
        using var output1 = output.QueryInterface<Output1>();
        using var duplication = output1.DuplicateOutput(device);

        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
        var nextFrame = DateTime.UtcNow;

        while (_running)
        {
            // Frame-Rate einhalten
            var now = DateTime.UtcNow;
            if (now < nextFrame)
            {
                Thread.Sleep(nextFrame - now);
                continue;
            }
            nextFrame = now + frameInterval;

            try
            {
                // Frame von DXGI holen (100ms Timeout)
                duplication.AcquireNextFrame(100,
                    out var frameInfo, out var desktopResource);

                if (frameInfo.TotalMetadataBufferSize > 0)
                {
                    // Nur wenn sich etwas geändert hat (dirty regions)
                    using var texture = desktopResource
                        .QueryInterface<SharpDX.Direct3D11.Texture2D>();
                    var bgraData = ExtractBgraData(device, texture);
                    onFrame(new CapturedFrame(bgraData,
                        output.Description.DesktopBounds.Right,
                        output.Description.DesktopBounds.Bottom,
                        monitorIndex, DateTime.UtcNow, false));
                }

                duplication.ReleaseFrame();
            }
            catch (SharpDXException ex) when (
                ex.ResultCode == ResultCode.WaitTimeout) { /* kein Frame */ }
            catch (SharpDXException ex) when (
                ex.ResultCode.Code == unchecked((int)0x887A0027))
            {
                // DXGI_ERROR_ACCESS_LOST — z.B. nach UAC-Dialog
                // Duplication neu initialisieren
                ReInitializeDuplication(ref duplication, device, output1);
            }
        }
    }

    private static byte[] ExtractBgraData(
        SharpDX.Direct3D11.Device device,
        SharpDX.Direct3D11.Texture2D texture)
    {
        // CPU-lesbare Staging-Textur erstellen und kopieren
        var desc = texture.Description;
        desc.CpuAccessFlags = CpuAccessFlags.Read;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.OptionFlags = ResourceOptionFlags.None;

        using var staging = new SharpDX.Direct3D11.Texture2D(device, desc);
        device.ImmediateContext.CopyResource(texture, staging);

        var mapped = device.ImmediateContext.MapSubresource(
            staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var size = desc.Width * desc.Height * 4;
            var data = new byte[size];
            Marshal.Copy(mapped.DataPointer, data, 0, size);
            return data;
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(staging, 0);
        }
    }
}
```

**Fehlerbehandlung:**

| Fehlerfall | Verhalten |
|---|---|
| UAC-Dialog erscheint | `DXGI_ERROR_ACCESS_LOST` → Duplication neu initialisieren, schwarzer Frame |
| Monitor abgesteckt | Exception → `MonitorConfigurationChanged` Event, Fallback auf Monitor 0 |
| Sperrbildschirm | Schwarzer Frame wird geliefert (automatisch von DXGI) |
| Hohe CPU-Last | `dirty regions` auswerten → Frame nur senden wenn wirklich Änderung |

---

## 1.3 VP8-Encoding (FFMpegCore)

**Warum VP8:** Komplett lizenzfrei (BSD/LGPL), nativ in allen modernen Browsern via WebCodecs API decodierbar, gute Qualität bei niedrigen Bitraten.

**Interface:**

```csharp
// RemoteDesk.Host/Encoding/IVideoEncoderService.cs
public interface IVideoEncoderService : IDisposable
{
    void Initialize(int width, int height, int fps, VideoQuality quality);
    void EncodeFrame(byte[] bgraData, Action<byte[]> onEncodedChunk);
    void ForceKeyframe();   // Sofortiger Keyframe für neue Viewer
    VideoStats GetStats();  // Aktuelle Bitrate, Encoder-Latenz
}

public enum VideoQuality { Low, Medium, High }

public record VideoStats(double ActualFps, int BitrateKbps, int EncodeLatencyMs);
```

**Qualitäts-Konfiguration:**

| Preset | CRF | Bitrate-Ziel | Verwendung |
|---|---|---|---|
| Low | 45 | ~150 kbit/s | Langsame Verbindung |
| Medium | 33 | ~400 kbit/s | Standard (empfohlen) |
| High | 20 | ~900 kbit/s | LAN / schnelle Verbindung |

**Encoding-Pipeline:**

```csharp
// BGRA32 → YUV420 → VP8 → IVF-Chunks → WebSocket
public class VP8EncoderService : IVideoEncoderService
{
    private FFMpegArgumentProcessor? _processor;
    private PipeSource? _inputPipe;
    private PipeDestination? _outputPipe;

    public void Initialize(int width, int height, int fps, VideoQuality quality)
    {
        var crf = quality switch {
            VideoQuality.Low    => 45,
            VideoQuality.Medium => 33,
            VideoQuality.High   => 20,
            _ => 33
        };

        // FFMpeg-Pipeline konfigurieren:
        // rawvideo (BGRA32) → libvpx VP8 → IVF
        _inputPipe  = new RawVideoPipeSource(width, height, fps, "bgra");
        _outputPipe = new StreamPipeSink(OnEncodedData);

        _processor = FFMpegArguments
            .FromPipeInput(_inputPipe, opts => opts
                .WithFramerate(fps)
                .ForcePixelFormat("bgra"))
            .OutputToPipe(_outputPipe, opts => opts
                .WithVideoCodec("libvpx")
                .WithConstantRateFactor(crf)
                .WithVideoBitrate(GetBitrateForQuality(quality))
                .WithCustomArgument("-deadline realtime")
                .WithCustomArgument("-cpu-used 5")
                .ForceFormat("ivf"))
            .ProcessAsynchronously();
    }

    public void EncodeFrame(byte[] bgraData, Action<byte[]> onEncodedChunk)
    {
        _onChunk = onEncodedChunk;
        _inputPipe!.WriteFrame(bgraData);
    }

    public void ForceKeyframe()
    {
        // FFMpeg-Argument: -force_key_frames "expr:gte(t,0)"
        _inputPipe!.RequestKeyframe();
    }
}
```

**JPEG-Fallback (für ältere Browser):**

```csharp
public class JpegEncoderService : IVideoEncoderService
{
    private int _quality = 75; // JPEG-Qualität 1–100

    public void EncodeFrame(byte[] bgraData, Action<byte[]> onEncodedChunk)
    {
        using var bitmap = new System.Drawing.Bitmap(
            _width, _height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // BGRA32 in Bitmap kopieren → JPEG encodieren
        var jpegBytes = ConvertToJpeg(bgraData, _quality);
        onEncodedChunk(jpegBytes);
    }
}
```

---

## 1.4 Kestrel WebSocket-Server (LAN)

**Aufgabe:** Dient im LAN als direkter WebSocket-Endpunkt und liefert gleichzeitig die Browser-Client-Dateien aus.

```csharp
// RemoteDesk.Host/Server/LocalWebSocketServer.cs
public class LocalWebSocketServer
{
    private WebApplication? _app;

    public async Task StartAsync(int port = 8443, string? certPath = null)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseKestrel(opts =>
        {
            opts.ListenAnyIP(port, listenOpts =>
            {
                if (certPath != null)
                    listenOpts.UseHttps(certPath, certPassword);
                else
                    listenOpts.UseHttps(); // Dev-Zertifikat
            });
        });

        _app = builder.Build();

        // Statische Dateien (Browser-Client)
        _app.UseStaticFiles();
        _app.UseWebSockets();

        // WebSocket-Endpunkt
        _app.Map("/stream", HandleStream);

        await _app.StartAsync();
    }

    private async Task HandleStream(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await _sessionManager.HandleNewConnectionAsync(ws, ctx);
    }
}
```

**Handshake-Protokoll (Phase 1, vereinfacht):**

```
Browser → WebSocket-Verbindung aufbauen
Browser → { "type": "hello", "capabilities": ["vp8", "jpeg"], "version": "1.0" }
Server  → { "type": "config",
             "codec": "vp8",           // oder "jpeg" je nach Capabilities
             "fps": 15,
             "width": 1920,
             "height": 1080 }
Server  → [binary VP8-Chunks, kontinuierlich]
```

**Frame-Paket-Format (binär):**

```
Byte 0:     Frame-Typ (0x01 = VP8, 0x02 = JPEG, 0x03 = Keyframe)
Bytes 1–4:  Timestamp (uint32, Millisekunden seit Session-Start)
Bytes 5–8:  Payload-Länge (uint32)
Bytes 9+:   VP8/JPEG Payload
```

---

## 1.5 Browser-Client (Phase 1)

Alle Browser-Dateien liegen unter `RemoteDesk.Host/wwwroot/` und werden vom Kestrel-Server ausgeliefert.

### index.html

```html
<!DOCTYPE html>
<html lang="de">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>RemoteDesk</title>
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body { background: #1a1a1a; color: #fff; font-family: sans-serif;
           display: flex; flex-direction: column; height: 100vh; }
    #toolbar { padding: 8px 12px; background: #2a2a2a;
               display: flex; align-items: center; gap: 12px; }
    #canvas-wrap { flex: 1; display: flex;
                   align-items: center; justify-content: center; }
    canvas { max-width: 100%; max-height: 100%; object-fit: contain; }
    #statusbar { padding: 4px 12px; background: #2a2a2a; font-size: 12px;
                 display: flex; gap: 16px; color: #aaa; }
    .status-ok  { color: #4caf50; }
    .status-err { color: #f44336; }
  </style>
</head>
<body>
  <div id="toolbar">
    <span>RemoteDesk</span>
    <input id="host-input" placeholder="IP:Port (z.B. 192.168.1.10:8443)"
           style="padding:4px 8px; background:#333; color:#fff;
                  border:1px solid #555; border-radius:4px; width:220px">
    <button id="connect-btn" onclick="viewer.connect()">Verbinden</button>
    <button id="fullscreen-btn" onclick="toggleFullscreen()">Vollbild</button>
  </div>

  <div id="canvas-wrap">
    <canvas id="remote-canvas"></canvas>
  </div>

  <div id="statusbar">
    <span id="status-conn">● Getrennt</span>
    <span id="status-fps">FPS: –</span>
    <span id="status-latency">Latenz: –</span>
    <span id="status-codec">Codec: –</span>
  </div>

  <script src="codec.js"></script>
  <script src="viewer.js"></script>
</body>
</html>
```

### viewer.js

```javascript
class RemoteDeskViewer {
    constructor() {
        this.canvas = document.getElementById('remote-canvas');
        this.ctx = this.canvas.getContext('2d');
        this.ws = null;
        this.codec = null;

        // FPS-Messung
        this._frameTimestamps = [];
    }

    async connect() {
        const host = document.getElementById('host-input').value
                     || location.host;
        const url = `wss://${host}/stream`;

        this.ws = new WebSocket(url);
        this.ws.binaryType = 'arraybuffer';

        this.ws.onopen = () => this._onOpen();
        this.ws.onmessage = (e) => this._onMessage(e);
        this.ws.onclose = () => this._onClose();
        this.ws.onerror = () => this._updateStatus('conn', '✗ Fehler', 'err');
    }

    async _onOpen() {
        this._updateStatus('conn', '● Verbunden', 'ok');

        // Capabilities melden
        const caps = ['jpeg'];
        if (typeof VideoDecoder !== 'undefined') caps.unshift('vp8');

        this.ws.send(JSON.stringify({
            type: 'hello', capabilities: caps, version: '1.0'
        }));
    }

    _onMessage(event) {
        if (typeof event.data === 'string') {
            this._handleJson(JSON.parse(event.data));
        } else {
            this._handleFrame(event.data);
        }
    }

    _handleJson(msg) {
        if (msg.type === 'config') {
            this.canvas.width  = msg.width;
            this.canvas.height = msg.height;
            this._updateStatus('codec', `Codec: ${msg.codec.toUpperCase()}`);

            // Codec initialisieren
            if (msg.codec === 'vp8') {
                this.codec = new VP8Decoder(this.canvas, () => this._countFrame());
            } else {
                this.codec = new JpegDecoder(this.canvas, () => this._countFrame());
            }
            this.codec.initialize(msg.width, msg.height);
        }
    }

    _handleFrame(buffer) {
        const view = new DataView(buffer);
        const frameType = view.getUint8(0);
        const timestamp = view.getUint32(1, false);
        const length    = view.getUint32(5, false);
        const payload   = buffer.slice(9, 9 + length);

        // Latenz-Messung (grobe Schätzung via Roundtrip)
        const latency = Date.now() - this._sessionStart - timestamp;
        this._updateStatus('latency', `Latenz: ${Math.abs(latency)}ms`);

        this.codec?.decodeFrame(payload, frameType === 0x03);
    }

    _countFrame() {
        const now = performance.now();
        this._frameTimestamps.push(now);
        // Rolling Window: letzte 2 Sekunden
        this._frameTimestamps = this._frameTimestamps.filter(
            t => now - t < 2000
        );
        const fps = Math.round(this._frameTimestamps.length / 2);
        this._updateStatus('fps', `FPS: ${fps}`);
    }

    _updateStatus(id, text, cls = '') {
        const el = document.getElementById(`status-${id}`);
        el.textContent = text;
        el.className = cls ? `status-${cls}` : '';
    }

    _onClose() {
        this._updateStatus('conn', '● Getrennt', '');
        this.codec?.dispose();
    }

    disconnect() {
        this.ws?.close();
    }
}

function toggleFullscreen() {
    if (!document.fullscreenElement)
        document.getElementById('canvas-wrap').requestFullscreen();
    else
        document.exitFullscreen();
}

const viewer = new RemoteDeskViewer();
```

### codec.js

```javascript
// VP8-Decoder via WebCodecs API
class VP8Decoder {
    constructor(canvas, onFrameDecoded) {
        this.canvas = canvas;
        this.onFrameDecoded = onFrameDecoded;
        this.decoder = null;
    }

    initialize(width, height) {
        this.width = width;
        this.height = height;
        const ctx = this.canvas.getContext('2d');

        this.decoder = new VideoDecoder({
            output: (frame) => {
                ctx.drawImage(frame, 0, 0);
                frame.close();
                this.onFrameDecoded();
            },
            error: (e) => console.error('VP8 Decoder:', e)
        });

        this.decoder.configure({
            codec: 'vp8',
            codedWidth: width,
            codedHeight: height,
            optimizeForLatency: true  // Wichtig für Remote Desktop
        });
    }

    decodeFrame(payload, isKeyframe) {
        if (this.decoder?.state !== 'configured') return;

        const chunk = new EncodedVideoChunk({
            type: isKeyframe ? 'key' : 'delta',
            timestamp: performance.now() * 1000, // Mikrosekunden
            data: payload
        });
        this.decoder.decode(chunk);
    }

    dispose() {
        this.decoder?.close();
    }
}

// JPEG-Fallback-Decoder
class JpegDecoder {
    constructor(canvas, onFrameDecoded) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.onFrameDecoded = onFrameDecoded;
    }

    initialize(width, height) { /* canvas bereits gesetzt */ }

    decodeFrame(payload) {
        const blob = new Blob([payload], { type: 'image/jpeg' });
        const url  = URL.createObjectURL(blob);
        const img  = new Image();
        img.onload = () => {
            this.ctx.drawImage(img, 0, 0);
            URL.revokeObjectURL(url);
            this.onFrameDecoded();
        };
        img.src = url;
    }

    dispose() { /* nichts zu tun */ }
}
```

---

## 1.6 WPF Tray-Icon (Phase 1, Grundgerüst)

Minimales Tray-Icon für Phase 1 — wird in späteren Phasen ausgebaut:

```xml
<!-- RemoteDesk.Host/UI/MainWindow.xaml -->
<!-- Unsichtbares Hauptfenster (nur Tray-Icon sichtbar) -->
<Window Visibility="Hidden" ShowInTaskbar="False">
  <tb:TaskbarIcon x:Name="TrayIcon"
                  IconSource="/Resources/icon.ico"
                  ToolTipText="RemoteDesk">
    <tb:TaskbarIcon.ContextMenu>
      <ContextMenu>
        <MenuItem Header="Einstellungen" Click="OnSettings"/>
        <Separator/>
        <MenuItem Header="Beenden" Click="OnExit"/>
      </ContextMenu>
    </tb:TaskbarIcon.ContextMenu>
  </tb:TaskbarIcon>
</Window>
```

**Settings-Fenster (Phase 1, vereinfacht):**

```
┌──────────────────────────────────────┐
│  RemoteDesk — Einstellungen          │
├──────────────────────────────────────┤
│  Server-Port: [8443          ]       │
│                                      │
│  Bildrate:   [15 ═══════░░░░] 30    │
│                                      │
│  Qualität:   ○ Niedrig               │
│              ● Mittel                │
│              ○ Hoch                  │
│                                      │
│  Status: ● Server läuft auf :8443   │
│  URL: https://192.168.1.10:8443      │
│                                      │
│         [ Speichern ]  [ Abbrechen ] │
└──────────────────────────────────────┘
```

---

## 1.7 Deliverables & Akzeptanzkriterien

### Checkliste Phase 1

- [ ] Solution mit allen 4 Projekten angelegt und kompilierbar
- [ ] Screen Capture läuft stabil (Primary Monitor)
- [ ] VP8-Encoding funktioniert (FFMpegCore, libvpx)
- [ ] Kestrel-Server startet auf konfigurierbarem Port (Standard: 8443)
- [ ] Browser-Client wird von Kestrel ausgeliefert (`https://[ip]:8443`)
- [ ] VP8-Stream wird im Browser decodiert und auf Canvas angezeigt
- [ ] JPEG-Fallback funktioniert wenn WebCodecs nicht verfügbar
- [ ] FPS-Einstellung (5–30) wirkt sofort, ohne Server-Neustart
- [ ] Qualitäts-Einstellung (Low/Medium/High) anwendbar
- [ ] FPS-Anzeige im Browser (tatsächlich gemessen, nicht nur Ziel)
- [ ] Latenz-Anzeige im Browser (grobe Schätzung)
- [ ] WPF-Tray-Icon erscheint beim Start
- [ ] Server-URL wird im Settings-Fenster angezeigt
- [ ] Stabiler Betrieb über 30 Minuten ohne Memory-Leak oder Freeze

### Testszenarien

| Szenario | Erwartetes Ergebnis |
|---|---|
| Browser öffnet URL | Verbindung baut sich auf, Stream startet |
| FPS-Slider auf 5 | Sichtbare Verlangsamung, ca. 5 FPS im Browser |
| FPS-Slider auf 30 | Flüssige Darstellung, ca. 30 FPS |
| Fenster bewegen auf Host | Bewegung ist im Browser sichtbar |
| Video auf Host abspielen | Erkennbare (nicht perfekte) Wiedergabe |
| Browser-Tab schließen | Server läuft weiter, kein Fehler |
| Browser neu verbinden | Stream startet sofort wieder |
| Qualität auf "Niedrig" | Sichtbar komprimierter, kleinere Pakete |

---

## Übergang zu Phase 2

Am Ende von Phase 1 funktioniert die Verbindung **nur im lokalen Netzwerk** über direkte IP-Eingabe. Phase 2 baut darauf auf und fügt hinzu:

- Relay-Server auf dem QNAP NAS (Docker Container Station)
- Session-Codes für einfaches Verbinden
- Self-signed SSL-Zertifikat (generiert vom Host)
- Verbindung über das Internet (ohne Port-Forwarding am Windows-Host)

---

*RemoteDesk · Phase 1 von 6 · Stand: April 2026*
