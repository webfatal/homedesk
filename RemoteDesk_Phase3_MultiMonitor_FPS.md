# RemoteDesk · Phase 3 — Multi-Monitor & FPS-Steuerung

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
| **SSL** | Self-signed, vom Host generiert und auf QNAP deployt |
| **Browser-Client** | Vanilla JS · HTML5 Canvas · WebCodecs API |
| **Sichtbarkeit** | Privates Projekt |

**Phasenübersicht:**

| Phase | Inhalt | Dauer |
|---|---|---|
| 1 | LAN-Kern: Capture, VP8, Canvas | 2–3 Wochen |
| 2 | Relay-Server, Docker (QNAP), SSL, Session-Code | 1–2 Wochen |
| **3 ← Sie sind hier** | Multi-Monitor, FPS-Steuerung, Skalierung | 1 Woche |
| 4 | Fernsteuerung, Multi-Viewer, Rollen | 1 Woche |
| 5 | Clipboard, Dateiübertragung | 1 Woche |
| 6 | Installer, Firewall-Konfiguration, UI-Polish | 1 Woche |

---

## Phase 3 — Ziel & Abgrenzung

**Ziel:** Vollständige Multi-Monitor-Unterstützung und performante, adaptive FPS-Regelung. Nutzer können während einer laufenden Session den Monitor wechseln, die Bildrate anpassen und die Übertragungsqualität skalieren.

**In dieser Phase enthalten:**
- Enumeration aller angeschlossenen Monitore via DXGI
- Monitor-Wechsel während laufender Session (ohne Reconnect)
- Adaptive FPS-Steuerung mit konfigurierbarem Bereich 5–30 FPS
- Optionale Auflösungs-Skalierung (Nativ / 75% / 50%)
- Korrekte Maus-Koordinaten-Skalierung im Browser (vorbereitung für Phase 4)
- `config_sync`-Protokollnachricht für Viewer bei Konfigurationsänderungen

**Noch nicht in dieser Phase:**
- Fernsteuerung / Input-Injection (→ Phase 4)
- Clipboard / Dateiübertragung (→ Phase 5)
- Installer (→ Phase 6)

**Voraussetzungen:**
- Phase 1 und 2 vollständig abgeschlossen
- Stream läuft stabil über Relay und LAN

---

## 3.1 Monitor-Enumeration (DXGI)

**MonitorInfo (erweitert gegenüber Phase 1):**

```csharp
// RemoteDesk.Shared/Protocol/MessageTypes.cs
public record MonitorInfo(
    int Index,
    string Name,           // z.B. "DELL U2722D (Display 2)"
    int Width,
    int Height,
    bool IsPrimary,
    int OffsetX,           // Position im virtuellen Desktop (für Multi-Monitor)
    int OffsetY,
    float DpiScale,        // z.B. 1.25 für 125% DPI
    int RefreshRateHz      // Maximale Bildwiederholrate des Monitors
);
```

**MultiMonitorManager.cs:**

```csharp
// RemoteDesk.Host/Capture/MultiMonitorManager.cs
public class MultiMonitorManager : IDisposable
{
    private readonly Factory1 _dxgiFactory;
    private int _currentMonitorIndex = 0;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        int adapterIdx = 0;

        using var factory = new Factory1();
        foreach (var adapter in factory.Adapters1)
        {
            int outputIdx = 0;
            foreach (var output in adapter.Outputs)
            {
                var desc = output.Description;
                var bounds = desc.DesktopBounds;

                // DPI via Windows API ermitteln
                var dpi = GetDpiForMonitor(desc.MonitorHandle);

                monitors.Add(new MonitorInfo(
                    Index:        monitors.Count,
                    Name:         $"{desc.DeviceName.TrimStart('\\').TrimStart('.')} (Display {monitors.Count + 1})",
                    Width:        bounds.Right  - bounds.Left,
                    Height:       bounds.Bottom - bounds.Top,
                    IsPrimary:    desc.IsAttachedToDesktop && outputIdx == 0 && adapterIdx == 0,
                    OffsetX:      bounds.Left,
                    OffsetY:      bounds.Top,
                    DpiScale:     dpi / 96.0f,
                    RefreshRateHz: GetRefreshRate(output)
                ));
                outputIdx++;
                output.Dispose();
            }
            adapterIdx++;
            adapter.Dispose();
        }
        return monitors;
    }

    /// <summary>Wechselt Monitor — auch während laufender Capture-Session.</summary>
    public void SwitchMonitor(int index, Action<CapturedFrame> onFrame, int fps)
    {
        StopCurrentCapture();

        _currentMonitorIndex = index;

        // Neue Capture-Session starten
        StartCapture(index, fps, frame => {
            onFrame(frame with { IsKeyframeRequired = true }); // Sofort-Keyframe
        });
    }

    // WinEventHook: reagiert auf Monitor-Konfigurationsänderungen
    public void StartMonitorChangeWatcher()
    {
        // WM_DISPLAYCHANGE überwachen
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object sender, EventArgs e)
    {
        var newMonitors = GetMonitors();
        MonitorConfigurationChanged?.Invoke(this,
            new MonitorConfigChangedArgs(newMonitors));

        // Prüfen ob aktueller Monitor noch vorhanden
        if (_currentMonitorIndex >= newMonitors.Count)
        {
            SwitchMonitor(0, _currentOnFrame!, _currentFps);
        }
    }

    public event EventHandler<MonitorConfigChangedArgs>? MonitorConfigurationChanged;
}

public record MonitorConfigChangedArgs(IReadOnlyList<MonitorInfo> NewMonitors);
```

---

## 3.2 Protokollerweiterung: config_sync

Wenn sich Monitor oder Konfiguration ändert, wird `config_sync` an alle Viewer gesendet:

```json
// Typ: config_sync (JSON, über WebSocket)
{
  "type": "config_sync",
  "width": 2560,
  "height": 1440,
  "fps": 15,
  "codec": "vp8",
  "quality": "medium",
  "scale": 1.0,
  "monitorIndex": 1,
  "monitorName": "DELL U2722D (Display 2)",
  "dpiScale": 1.25
}
```

**Relay leitet `config_sync` an alle Viewer weiter** (wie reguläre JSON-Nachrichten).

**Browser-Verhalten bei `config_sync`:**

```javascript
_handleConfigSync(msg) {
    // Canvas-Größe anpassen
    this.canvas.width  = msg.width;
    this.canvas.height = msg.height;

    // DPI-Skalierungsfaktor merken (für Mauskoordinaten in Phase 4)
    this._dpiScale = msg.dpiScale;

    // Codec ggf. neu initialisieren (falls gewechselt)
    if (msg.codec !== this._currentCodec) {
        this.codec?.dispose();
        this.codec = msg.codec === 'vp8'
            ? new VP8Decoder(this.canvas, () => this._countFrame())
            : new JpegDecoder(this.canvas, () => this._countFrame());
        this.codec.initialize(msg.width, msg.height);
        this._currentCodec = msg.codec;
    }

    this._updateStatus('monitor', `Monitor: ${msg.monitorName}`);
}
```

---

## 3.3 Adaptive FPS-Steuerung mit Backpressure

**Problem ohne Backpressure:** Wenn Encoding länger dauert als das Capture-Intervall, staut sich ein Frame-Buffer auf — Latenz steigt unkontrolliert.

**Lösung — Non-blocking Capture mit Drop:**

```csharp
// RemoteDesk.Host/Capture/AdaptiveCaptureController.cs
public class AdaptiveCaptureController
{
    private volatile int _targetFps = 15;
    private volatile bool _encodingBusy = false;

    // Wird from Settings-UI aufgerufen (thread-safe)
    public void SetTargetFps(int fps)
    {
        _targetFps = Math.Clamp(fps, 5, 30);
        _timer.Period = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
    }

    private void OnTimerTick()
    {
        // Backpressure: Frame überspringen wenn Encoder noch beschäftigt
        if (_encodingBusy)
        {
            _droppedFrames++;
            return;
        }

        _encodingBusy = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_captureService.TryCapture(out var frame))
                    _encoderService.EncodeFrame(frame.BgraData, OnEncodedChunk);
            }
            finally
            {
                _encodingBusy = false;
            }
        });
    }

    private void OnEncodedChunk(byte[] chunk)
    {
        // An Relay/LAN-Server weiterleiten
        _sessionManager.BroadcastFrameAsync(chunk);

        // Statistik aktualisieren
        _stats.RecordFrame(chunk.Length);
    }

    // Statistiken für UI
    public CaptureStats GetStats() => new(
        ActualFps:      _stats.CalculateActualFps(),
        DroppedFrames:  _droppedFrames,
        EncodingLatency: _stats.AverageEncodeMs,
        BitrateKbps:    _stats.CurrentBitrateKbps
    );
}
```

**FPS-Anzeige im Host-UI (Live-Statistik):**

```
┌──────────────────────────────────────┐
│ Monitor: [DELL U2722D (Display 2) ▼] │
│ FPS: [15 ══════════════░░░░] 30      │
│                                      │
│ Statistik:                           │
│  Ist-FPS:       14.8                 │
│  Encoding:      8ms                  │
│  Bitrate:       380 kbps             │
│  Dropped:       0 frames             │
└──────────────────────────────────────┘
```

---

## 3.4 Auflösungs-Skalierung

Optionales Downscaling vor dem Encoding für Bandbreitenersparnis:

```csharp
public enum ScaleMode
{
    Native,   // 100% — keine Skalierung
    HD,       // 75%  — z.B. 1920×1080 → 1440×810
    SD        // 50%  — z.B. 1920×1080 → 960×540
}

public class FrameScaler
{
    public byte[] Scale(byte[] bgraData, int srcWidth, int srcHeight,
        ScaleMode mode)
    {
        if (mode == ScaleMode.Native) return bgraData;

        float factor = mode == ScaleMode.HD ? 0.75f : 0.5f;
        int dstWidth  = (int)(srcWidth  * factor) & ~1; // geradzahlig für VP8
        int dstHeight = (int)(srcHeight * factor) & ~1;

        // Bilineares Downscaling via SkiaSharp (SkiaSharp NuGet)
        using var srcBitmap = SKBitmap.Decode(bgraData);
        using var dstBitmap = srcBitmap.Resize(
            new SKSizeI(dstWidth, dstHeight),
            SKFilterQuality.Medium);

        return dstBitmap.Bytes;
    }
}
```

**Typische Bitraten nach Skalierung:**

| Auflösung | Skalierung | VP8 Medium | Verwendung |
|---|---|---|---|
| 3840×2160 (4K) | Nativ | ~2.5 Mbit/s | LAN |
| 2880×1620 (4K→75%) | HD | ~1.4 Mbit/s | schnelles Internet |
| 1920×1080 (FHD) | Nativ | ~500 kbit/s | Standard |
| 1440×810 (FHD→75%) | HD | ~280 kbit/s | mittleres Internet |
| 960×540 (FHD→50%) | SD | ~120 kbit/s | langsame Verbindung |

---

## 3.5 Monitor-Wechsel-Ablauf (vollständig)

```
Nutzer wählt neuen Monitor im Host-Settings-Dropdown
    ↓
MultiMonitorManager.SwitchMonitor(newIndex)
    ↓
Aktuelle DXGI-Capture-Session stoppen
    ↓
Neue DXGI-Output für neuen Monitor initialisieren
    ↓
VP8-Encoder mit neuer Auflösung neu initialisieren
    ↓
KeyframeRequired = true (nächster Frame muss Keyframe sein)
    ↓
Host sendet config_sync an Relay:
  { type: "config_sync", width: 2560, height: 1440, ... }
    ↓
Relay broadcastet config_sync an alle Viewer
    ↓
Alle Browser-Clients passen Canvas-Größe an
    ↓
Nächster Keyframe kommt an → Stream läuft mit neuem Monitor
```

**Latenz dieses Wechsels in der Praxis:** ~200–500ms (1 Keyframe-Intervall)

---

## 3.6 DPI-bewusste Mauskoordinaten (Vorbereitung Phase 4)

Wichtige Grundlage für Phase 4 (Input-Injection):

```csharp
// RemoteDesk.Host/Input/CoordinateMapper.cs
public class CoordinateMapper
{
    /// <summary>
    /// Rechnet normalisierte Browser-Koordinaten (0.0–1.0)
    /// in absolute Windows-Desktop-Koordinaten um.
    /// Berücksichtigt DPI-Skalierung und Monitor-Offset.
    /// </summary>
    public (int x, int y) MapToDesktop(
        float normalizedX, float normalizedY,
        MonitorInfo monitor)
    {
        // Pixel-Position innerhalb des Monitors
        int monitorX = (int)(normalizedX * monitor.Width);
        int monitorY = (int)(normalizedY * monitor.Height);

        // DPI-Korrektur: DXGI liefert physische Pixel,
        // SendInput braucht logische Pixel
        int logicalX = (int)(monitorX / monitor.DpiScale);
        int logicalY = (int)(monitorY / monitor.DpiScale);

        // Monitor-Offset im virtuellen Desktop hinzurechnen
        return (logicalX + monitor.OffsetX, logicalY + monitor.OffsetY);
    }
}
```

**Browser-seitig (viewer.js — normalisierte Koordinaten):**

```javascript
// Mausposition normalisieren (unabhängig von Canvas-Darstellungsgröße)
_getCanvasNormalizedPosition(event) {
    const rect = this.canvas.getBoundingClientRect();
    return {
        x: (event.clientX - rect.left) / rect.width,
        y: (event.clientY - rect.top)  / rect.height
    };
}
```

---

## 3.7 Host-UI: Settings-Fenster (Phase 3 Erweiterung)

**Tab "Anzeige" vollständig:**

```
┌────────────────────────────────────────────────────────┐
│  Tab: Anzeige                                          │
├────────────────────────────────────────────────────────┤
│  Monitor:                                              │
│  [● Display 1 — SAMSUNG (1920×1080, 60Hz, Primär)  ▼] │
│  [  Display 2 — DELL U2722D (2560×1440, 144Hz)      ] │
│  [  Display 3 — BenQ PD3200 (3840×2160, 60Hz)       ] │
│                                                        │
│  [Monitore neu einlesen]                               │
│                                                        │
│  Bildrate:                                             │
│  [5 ▐░░░░░░░░░░░░░░░░░░░░░░░░░░░▌ 30]  Ziel: 15 FPS  │
│                                                        │
│  Qualität:                                             │
│  ○ Niedrig  (CRF 45, ~150 kbit/s)                     │
│  ● Mittel   (CRF 33, ~400 kbit/s)  ← Standard         │
│  ○ Hoch     (CRF 20, ~900 kbit/s)                     │
│                                                        │
│  Skalierung:                                           │
│  ● Nativ    (100%, beste Qualität)                     │
│  ○ HD       (75%, weniger Bandbreite)                  │
│  ○ SD       (50%, minimale Bandbreite)                 │
│                                                        │
│  Codec:                                                │
│  ● VP8  (empfohlen, hardware-beschleunigt im Browser)  │
│  ○ JPEG (Fallback, höhere Latenz)                      │
│                                                        │
│  Live-Statistik:                                       │
│  Ist-FPS: 14.8 / Encoding: 8ms / Bitrate: 380 kbps    │
│  Dropped: 0 frames in letzten 60s                      │
└────────────────────────────────────────────────────────┘
```

---

## 3.8 Browser-UI (Phase 3 Erweiterung)

**Statusleiste erweitert:**

```
┌───────────────────────────────────────────────────────────┐
│             Remote Desktop Canvas                         │
├───────────────────────────────────────────────────────────┤
│ ● Verbunden   FPS: 14/15   Latenz: 38ms   380 kbps       │
│ Monitor: DELL U2722D (2560×1440)   Codec: VP8             │
└───────────────────────────────────────────────────────────┘
```

**Responsive Canvas (CSS):**

```css
#canvas-wrap {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    background: #111;
    overflow: hidden;
}

canvas {
    /* Skaliert proportional in den verfügbaren Platz */
    max-width: 100%;
    max-height: 100%;
    object-fit: contain;
    /* Verhindert unscharfe Darstellung bei CSS-Skalierung */
    image-rendering: crisp-edges;
}
```

---

## 3.9 Deliverables & Akzeptanzkriterien

### Checkliste Phase 3

- [ ] Alle angeschlossenen Monitore werden im Host-UI aufgelistet (Name, Auflösung, Hz)
- [ ] Monitor-Wechsel während laufender Session funktioniert ohne Reconnect
- [ ] `config_sync`-Nachricht wird korrekt an alle Viewer gesendet
- [ ] Browser-Canvas passt sich bei Monitor-Wechsel automatisch an
- [ ] Monitor-Abziehaktion wird erkannt → automatischer Fallback auf Monitor 0
- [ ] FPS-Slider 5–30 wirkt sofort (< 500ms bis erkennbar)
- [ ] Dropped-Frame-Backpressure funktioniert (kein Memory-Aufbau unter Last)
- [ ] Live-Statistik im Host-UI aktualisiert sich alle 2 Sekunden
- [ ] Auflösungs-Skalierung (Nativ/HD/SD) anwendbar
- [ ] DPI-Skalierung wird korrekt berücksichtigt (Basis für Phase 4)
- [ ] Browser-Canvas skaliert responsiv (passt sich Fenstergröße an)
- [ ] Normalisierte Mauskoordinaten werden korrekt berechnet (Basis für Phase 4)

### Testszenarien

| Szenario | Erwartetes Ergebnis |
|---|---|
| Zwei Monitore angeschlossen | Beide in Dropdown, beide streambar |
| Monitor wechseln (Live) | Neues Bild in < 500ms im Browser |
| Monitor abziehen während Session | Fallback auf Monitor 0, kein Absturz |
| FPS von 30 auf 5 | Deutliche Verlangsamung erkennbar |
| FPS von 5 auf 30 | Flüssigere Darstellung |
| 4K Monitor, Skalierung SD | Deutlich kleinere Pakete, ~960×540 |
| Canvas-Fenster verkleinern | Bild skaliert, kein Scrollen |

---

## Übergang zu Phase 4

Phase 3 liefert die Grundlagen für Phase 4: Der `CoordinateMapper` ist vorbereitet, normalisierte Mauskoordinaten werden berechnet. Phase 4 ergänzt:

- Input-Injection via `SendInput()` Windows API
- Mausbewegung, Klick, Scroll
- Tastatureingaben (inkl. Sondertasten)
- Multi-Viewer mit Kontroll-Anfrage und Host-Approval
- View-Only vs. Remote-Control Modus

---

*RemoteDesk · Phase 3 von 6 · Stand: April 2026*
