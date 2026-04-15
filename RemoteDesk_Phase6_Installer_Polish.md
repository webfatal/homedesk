# RemoteDesk · Phase 6 — Installer, Firewall & Finalisierung

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
| 3 | Multi-Monitor, FPS-Steuerung, Skalierung | 1 Woche |
| 4 | Fernsteuerung, Multi-Viewer, Rollen | 1 Woche |
| 5 | Clipboard, Dateiübertragung | 1 Woche |
| **6 ← Sie sind hier** | Installer, Firewall-Konfiguration, UI-Polish | 1 Woche |

---

## Phase 6 — Ziel & Abgrenzung

**Ziel:** Das Projekt professionell abschließen — mit einem One-Click-Installer, automatischer Firewall-Konfiguration, poliertem UI und umfassender Qualitätssicherung. Am Ende dieser Phase ist RemoteDesk ein vollständig nutzbares, stabiles Produkt.

**In dieser Phase enthalten:**
- Inno Setup Installer (`.exe`) mit automatischer Firewall-Konfiguration
- Deinstallation entfernt alle Firewall-Regeln vollständig
- Optionaler Autostart-Eintrag im Installer
- Vollständiges, poliertes WPF-Settings-UI (alle Tabs finalisiert)
- Vollbild-Modus im Browser (F11 / Button)
- Mobile Touch-Unterstützung im Browser
- Auto-Reconnect-Logik (Host ↔ Relay)
- Verbindungsqualitäts-Indikator im Browser
- Dark/Light Mode im Browser
- Umfassende Qualitätssicherung aller Phasen

**Voraussetzungen:**
- Alle Phasen 1–5 vollständig abgeschlossen
- Inno Setup (kostenlos, https://jrsoftware.org/isinfo.php) installiert
- .NET 10 Runtime MSI für Installer-Bundling vorhanden

---

## 6.1 Inno Setup Installer

**Datei:** `RemoteDesk.Installer/setup.iss`

```iss
; RemoteDesk Setup-Skript
; Inno Setup 6.x

#define MyAppName    "RemoteDesk"
#define MyAppVersion "1.0.0"
#define MyAppExe     "RemoteDesk.Host.exe"
#define MyAppPublisher "Privat"
#define MyAppURL     ""

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=RemoteDesk-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
; UAC-Elevation für Firewall-Konfiguration erforderlich
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
DisableProgramGroupPage=yes
; Windows 10+ erforderlich (für DXGI Desktop Duplication)
MinVersion=10.0.19041

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
; Optionaler Autostart
Name: "autostart";  \
  Description: "RemoteDesk automatisch mit Windows starten"; \
  GroupDescription: "Zusätzliche Optionen:"; \
  Flags: unchecked

[Files]
; Haupt-Anwendung (self-contained publish)
Source: "..\RemoteDesk.Host\bin\Release\net10.0-windows\publish\*"; \
  DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Browser-Client (wwwroot)
Source: "..\RemoteDesk.Host\wwwroot\*"; \
  DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs

; FFMpeg-Binaries (LGPL, müssen separat mitgeliefert werden)
Source: "..\libs\ffmpeg\*"; \
  DestDir: "{app}\ffmpeg"; Flags: ignoreversion recursesubdirs

; Konfigurationsverzeichnis vorbereiten
Source: "..\RemoteDesk.Host\config\default.json"; \
  DestDir: "{commonappdata}\{#MyAppName}"; \
  DestName: "settings.json"; \
  Flags: onlyifdoesntexist

[Dirs]
; Verzeichnisse anlegen
Name: "{commonappdata}\{#MyAppName}"
Name: "{commonappdata}\{#MyAppName}\certs"

[Icons]
; Startmenü-Verknüpfung
Name: "{group}\{#MyAppName}"; \
  Filename: "{app}\{#MyAppExe}"
Name: "{group}\{#MyAppName} Deinstallieren"; \
  Filename: "{uninstallexe}"

; Desktop-Verknüpfung (optional)
Name: "{userdesktop}\{#MyAppName}"; \
  Filename: "{app}\{#MyAppExe}"; \
  Tasks: desktopicon

[Run]
; ─── Schritt 1: Windows Defender Firewall konfigurieren ───────────────────

; Eingehende Verbindungen (für LAN-Direktverbindung auf Port 8443)
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule \
    name=""RemoteDesk - LAN Eingehend (TCP 8443)"" \
    dir=in action=allow \
    program=""{app}\{#MyAppExe}"" \
    protocol=TCP localport=8443 \
    enable=yes profile=private,domain"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Firewall wird konfiguriert (eingehend)..."

; Ausgehende Verbindungen (für Relay auf Port 443)
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule \
    name=""RemoteDesk - Relay Ausgehend (TCP 443)"" \
    dir=out action=allow \
    program=""{app}\{#MyAppExe}"" \
    protocol=TCP remoteport=443 \
    enable=yes"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Firewall wird konfiguriert (ausgehend)..."

; ─── Schritt 2: Autostart (optional, per Task) ────────────────────────────

; Autostart via Windows Task Scheduler (läuft auch ohne Anmeldung)
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Create \
    /TN ""RemoteDesk\Autostart"" \
    /TR ""{app}\{#MyAppExe}"" \
    /SC ONLOGON /RL HIGHEST /F"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Autostart wird eingerichtet..."; \
  Tasks: autostart

; ─── Schritt 3: Anwendung starten ─────────────────────────────────────────
Filename: "{app}\{#MyAppExe}"; \
  Description: "RemoteDesk starten"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Firewall-Regeln beim Deinstallieren vollständig entfernen
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule \
    name=""RemoteDesk - LAN Eingehend (TCP 8443)"""; \
  Flags: runhidden waituntilterminated

Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall delete rule \
    name=""RemoteDesk - Relay Ausgehend (TCP 443)"""; \
  Flags: runhidden waituntilterminated

; Task Scheduler Eintrag entfernen
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Delete /TN ""RemoteDesk\Autostart"" /F"; \
  Flags: runhidden waituntilterminated

[UninstallDelete]
; Konfiguration und Zertifikate beim Deinstallieren fragen
Type: filesandordirs; Name: "{commonappdata}\{#MyAppName}"

[Code]
// Prüft ob Windows 10 v1903+ vorhanden (DXGI Desktop Duplication)
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  if Version.Major < 10 then
  begin
    MsgBox('RemoteDesk benötigt Windows 10 (Version 1903 oder neuer).',
           mbError, MB_OK);
    Result := False;
    Exit;
  end;
  Result := True;
end;
```

---

## 6.2 Build & Publish-Pipeline

**Vor dem Installer-Build:**

```powershell
# 1. .NET 10 Self-Contained Publish (kein .NET auf Zielrechner nötig)
dotnet publish RemoteDesk.Host/RemoteDesk.Host.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishReadyToRun=true `
  -o ./RemoteDesk.Host/bin/Release/net10.0-windows/publish

# 2. FFMpeg-Binaries herunterladen (LGPL-Variante, kein GPL-Code)
# Quelle: https://github.com/BtbN/FFmpeg-Builds/releases
# Variante: ffmpeg-n7.x-lgpl-shared-win64

# 3. Inno Setup kompilieren
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" RemoteDesk.Installer/setup.iss
```

---

## 6.3 Vollständiges WPF Settings-UI

### Tab-Struktur (finalisiert)

```
┌────────────────────────────────────────────────────────────┐
│  RemoteDesk — Einstellungen                          [X]   │
├────────────────────────────────────────────────────────────┤
│  [Verbindung] [Anzeige] [Sicherheit] [Zertifikat] [Info]  │
├────────────────────────────────────────────────────────────┤
│  ...Tab-Inhalt...                                          │
├────────────────────────────────────────────────────────────┤
│                      [ Speichern ]  [ Abbrechen ]          │
└────────────────────────────────────────────────────────────┘
```

### Tab 1: Verbindung

```
Relay-Server DNS-Name:
[meinqnap.myqnapcloud.com                     ]

Relay-Port:   [443]

Verbindungsmodus:
● Via Relay (empfohlen, Internet + LAN)
○ Nur LAN-Direktverbindung (kein Relay)

LAN-Port (Direktverbindung):  [8443]

Auto-Reconnect:   ● Aktiviert   Versuche: [5]   Wartezeit: [10]s

─────────────────────────────────────────────
Status:  ● Verbunden mit Relay               ← Live-Indikator
Session-Code:  482 619 073  [Kopieren] [Neu generieren]
─────────────────────────────────────────────
[ Verbindung testen ]
```

### Tab 2: Anzeige

```
Standard-Monitor:
[● Display 1 — SAMSUNG (1920×1080, 60Hz, Primär)     ▼]
[  Display 2 — DELL U2722D (2560×1440, 144Hz)          ]
[Monitore neu einlesen]

Bildrate (FPS):
[5 ▐░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░▌ 30]  15 FPS

Qualität:
○ Niedrig   (CRF 45, ~150 kbit/s)
● Mittel    (CRF 33, ~400 kbit/s)    ← Standard
○ Hoch      (CRF 20, ~900 kbit/s)

Skalierung:
● Nativ   ○ HD (75%)   ○ SD (50%)

Codec:
● VP8   ○ JPEG (Fallback)

─────────────────────────────────────────────
Live-Statistik:
Ist-FPS: 14.8  |  Encoding: 8ms  |  Bitrate: 380 kbps
Viewer: 2  |  Dropped: 0 frames
```

### Tab 3: Sicherheit

```
Viewer-Anfragen:
● Immer manuell bestätigen  (empfohlen)
○ Automatisch ablehnen (kein Zugriff möglich)

Approval-Dialog immer im Vordergrund:  ● Ja

Clipboard-Synchronisation:
● Text und Bilder    ○ Nur Text    ○ Deaktiviert

Dateiübertragung:
☑ Browser → Host erlauben
☑ Host → Browser erlauben
Maximale Dateigröße:  [500] MB

Download-Ordner:
[%USERPROFILE%\Downloads\RemoteDesk    ]  [Ändern...]

Session-Timeout (inaktiv):   [24] Stunden  (0 = kein Timeout)
```

### Tab 4: Zertifikat

```
Relay-Server DNS-Name (für Zertifikat):
[meinqnap.myqnapcloud.com                     ]

Weitere SANs (optional, eine pro Zeile):
[192.168.1.50                                 ]
[remotedesk.local                             ]

─────────────────────────────────────────────
Aktuelles Zertifikat:
  Gültig bis:   15.04.2036 (in 10 Jahren)
  Fingerprint:  AB:CD:EF:12:34:56:78:90:...
  [Fingerprint kopieren]
─────────────────────────────────────────────
[ Neues Zertifikat generieren ]

[ PFX exportieren → QNAP deployen ]
  Ziel: /share/Container/remotedesk/certs/relay.pfx
  [QNAP-Pfad konfigurieren...]

[ CER exportieren ]  ← Für Browser-Import (einmalige Bestätigung)

Hinweis: Nach Zertifikatserneuerung muss Docker-Container
         neu gestartet werden.
```

### Tab 5: Info

```
RemoteDesk Version 1.0.0
.NET 10.0.x

Aktive Session:   482 619 073
Laufzeit:         3h 42min
Übertragen:       ↑ 845 MB   ↓ 12 MB

Log (letzte 20 Einträge):
[15:42:07] Viewer 203.0.113.42 verbunden
[15:38:22] Monitor gewechselt → Display 2
[15:30:00] Session gestartet
[...]

[ Log exportieren ]   [ Feedback / Problem melden ]

Lizenzen:
  FFMpegCore — LGPL 2.1
  BouncyCastle — MIT
  SharpDX — MIT
  Hardcodet.NotifyIcon.Wpf — CPOL
```

---

## 6.4 Tray-Icon (finalisiert)

```csharp
// RemoteDesk.Host/UI/TrayIconManager.cs
public class TrayIconManager
{
    private TaskbarIcon? _tray;
    private ContextMenu? _menu;

    public void Initialize()
    {
        _tray = new TaskbarIcon
        {
            IconSource = LoadIcon("remotedesk.ico"),
            ToolTipText = "RemoteDesk — Kein Viewer",
        };

        _tray.TrayMouseDoubleClick += (_, _) => ShowSettings();
        RebuildMenu();
    }

    public void UpdateStatus(SessionStatus status)
    {
        _tray!.ToolTipText = status.ViewerCount > 0
            ? $"RemoteDesk — {status.ViewerCount} Viewer verbunden"
            : "RemoteDesk — Bereit";

        // Icon-Farbe: grün wenn aktiv, grau wenn warte
        _tray.IconSource = LoadIcon(
            status.ViewerCount > 0 ? "icon_active.ico" : "icon_idle.ico");

        RebuildMenu(status);
    }

    private void RebuildMenu(SessionStatus? status = null)
    {
        _menu = new ContextMenu();

        if (status != null)
        {
            AddMenuItem($"● Session: {FormatCode(status.Code)}", null,
                bold: true, clickable: false);
            AddMenuItem($"Viewer: {status.ViewerCount} verbunden",
                null, clickable: false);
            AddSeparator();

            if (status.HasPendingApproval)
            {
                AddMenuItem("⚡ Anfrage ausstehend — Klicken zum Anzeigen",
                    ShowPendingApprovals, bold: true);
                AddSeparator();
            }

            if (status.ViewerCount > 0)
            {
                AddMenuItem("Fernsteuerung entziehen", RevokeControl);
                AddMenuItem("Alle Viewer trennen", DisconnectAll);
                AddSeparator();
            }
        }

        AddMenuItem("Datei senden...", SendFile);
        AddSeparator();
        AddMenuItem("Einstellungen", ShowSettings);
        AddSeparator();
        AddMenuItem("Beenden", ExitApp);

        _tray!.ContextMenu = _menu;
    }

    private string FormatCode(string code)
        => $"{code[..3]} {code[3..6]} {code[6..]}";
}
```

---

## 6.5 Browser-UI Finalisierung

### Vollbild-Modus

```javascript
// viewer.js — Vollbild-Implementierung
function enterFullscreen() {
    const wrap = document.getElementById('canvas-wrap');
    if (wrap.requestFullscreen) {
        wrap.requestFullscreen();
    } else if (wrap.webkitRequestFullscreen) {
        wrap.webkitRequestFullscreen(); // Safari
    }
}

// Im Vollbild: Toolbar ausblenden, Cursor-Overlay
document.addEventListener('fullscreenchange', () => {
    const toolbar = document.getElementById('toolbar');
    if (document.fullscreenElement) {
        toolbar.style.opacity = '0';
        // Toolbar bei Mausbewegung kurz einblenden
        document.addEventListener('mousemove', showToolbarBriefly);
    } else {
        toolbar.style.opacity = '1';
        document.removeEventListener('mousemove', showToolbarBriefly);
    }
});
```

### Mobile Touch-Unterstützung

```javascript
// Touch → Maus-Event-Konvertierung
class TouchInputAdapter {
    constructor(canvas, inputHandler) {
        this.canvas = canvas;
        this.input  = inputHandler;
        this._lastTap = 0;

        canvas.addEventListener('touchstart',  this._onTouchStart.bind(this), { passive: false });
        canvas.addEventListener('touchmove',   this._onTouchMove.bind(this),  { passive: false });
        canvas.addEventListener('touchend',    this._onTouchEnd.bind(this),   { passive: false });
    }

    _onTouchStart(e) {
        e.preventDefault();
        const touch = e.touches[0];
        const pos = this._getNormalized(touch);

        this.input._onMouseMove({ clientX: touch.clientX, clientY: touch.clientY });

        // Doppeltipp → Doppelklick
        const now = Date.now();
        if (now - this._lastTap < 300) {
            this.input._send({ type: 'input_mouse', action: 'click',
                               button: 'left', down: true });
            this.input._send({ type: 'input_mouse', action: 'click',
                               button: 'left', down: false });
        }
        this._lastTap = now;

        // Einzeltipp → Linksklick
        this._touchTimer = setTimeout(() => {
            this.input._send({ type: 'input_mouse', action: 'click',
                               button: 'left', down: true });
        }, 50);
    }

    _onTouchMove(e) {
        e.preventDefault();
        const touch = e.touches[0];
        this.input._onMouseMove({ clientX: touch.clientX, clientY: touch.clientY });
    }

    _onTouchEnd(e) {
        e.preventDefault();
        clearTimeout(this._touchTimer);
        this.input._send({ type: 'input_mouse', action: 'click',
                           button: 'left', down: false });
    }

    _getNormalized(touch) {
        const rect = this.canvas.getBoundingClientRect();
        return {
            x: (touch.clientX - rect.left) / rect.width,
            y: (touch.clientY - rect.top) / rect.height
        };
    }
}
```

### Verbindungsqualitäts-Indikator

```javascript
class ConnectionQualityIndicator {
    constructor() {
        this._latencies = []; // Rolling Window
        this._fpsValues = [];
    }

    update(latencyMs, actualFps, targetFps) {
        this._latencies.push(latencyMs);
        this._fpsValues.push(actualFps);
        if (this._latencies.length > 30) this._latencies.shift();
        if (this._fpsValues.length > 30) this._fpsValues.shift();

        const avgLatency = this._avg(this._latencies);
        const avgFps     = this._avg(this._fpsValues);
        const fpsDrop    = (targetFps - avgFps) / targetFps;

        // Qualitätsstufe bestimmen
        let quality, color;
        if (avgLatency < 80 && fpsDrop < 0.1) {
            quality = 'Gut';     color = '#4caf50'; // Grün
        } else if (avgLatency < 200 && fpsDrop < 0.3) {
            quality = 'Mittel';  color = '#ff9800'; // Orange
        } else {
            quality = 'Schlecht'; color = '#f44336'; // Rot
        }

        document.getElementById('quality-indicator').style.color = color;
        document.getElementById('quality-indicator').textContent =
            `● ${quality} · ${Math.round(avgLatency)}ms · ${Math.round(avgFps)} FPS`;
    }

    _avg(arr) {
        return arr.reduce((a, b) => a + b, 0) / arr.length;
    }
}
```

### Dark/Light Mode

```css
/* viewer.css */
:root {
    --bg-primary:   #1a1a1a;
    --bg-secondary: #2a2a2a;
    --text-primary: #ffffff;
    --text-muted:   #aaaaaa;
    --accent:       #4caf50;
}

@media (prefers-color-scheme: light) {
    :root {
        --bg-primary:   #f5f5f5;
        --bg-secondary: #e0e0e0;
        --text-primary: #212121;
        --text-muted:   #757575;
        --accent:       #1976d2;
    }
}

body { background: var(--bg-primary); color: var(--text-primary); }
#toolbar, #statusbar { background: var(--bg-secondary); }
```

---

## 6.6 Auto-Reconnect (Host ↔ Relay)

```csharp
// RemoteDesk.Host/Session/ReconnectManager.cs
public class ReconnectManager
{
    private readonly int _maxRetries;
    private int _currentRetry = 0;
    private bool _intentionalDisconnect = false;

    public async Task RunWithReconnect(
        Func<Task> connectAction,
        Action<string> onStatusUpdate)
    {
        while (!_intentionalDisconnect)
        {
            try
            {
                _currentRetry = 0;
                onStatusUpdate("● Verbinde mit Relay...");
                await connectAction();

                // Verbindung verloren (connectAction returned ohne Exception)
                onStatusUpdate("● Verbindung getrennt");
            }
            catch (Exception ex)
            {
                _currentRetry++;
                if (_currentRetry > _maxRetries)
                {
                    onStatusUpdate($"✗ Relay nicht erreichbar nach {_maxRetries} Versuchen");
                    await Task.Delay(TimeSpan.FromMinutes(5)); // Langer Backoff
                    _currentRetry = 0; // Reset für erneuten Versuch
                    continue;
                }

                // Exponentieller Backoff: 5s, 10s, 20s, 30s, 30s, ...
                var delay = Math.Min(30, (int)Math.Pow(2, _currentRetry) * 5);
                onStatusUpdate($"⟳ Reconnect in {delay}s (Versuch {_currentRetry}/{_maxRetries})");
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }
        }
    }

    public void StopReconnecting() => _intentionalDisconnect = true;
}
```

---

## 6.7 Vollständige Qualitätssicherung

### Testmatrix Phase 6 (alle Szenarien)

**Installer:**

| Szenario | Erwartetes Ergebnis |
|---|---|
| Installer auf frischem Windows 10 | Läuft durch, Firewall-Regeln angelegt |
| Installer auf Windows 11 | Korrekte Firewall-Konfiguration |
| Installer ohne Admin-Rechte | UAC-Prompt erscheint, dann korrekte Installation |
| Deinstallation | Alle Firewall-Regeln entfernt, keine Restdateien (außer Konfig) |
| Autostart-Option gewählt | App startet nach Neustart automatisch |
| Autostart-Option abgewählt | Kein Autostart-Eintrag |

**Verbindung & Stream:**

| Szenario | Erwartetes Ergebnis |
|---|---|
| LAN: Browser verbindet direkt (IP) | Stream sofort aktiv |
| Internet: Browser verbindet via Code | Stream über Relay aktiv |
| Relay-Verbindung bricht ab | Auto-Reconnect, Code bleibt gleich wenn möglich |
| 30 Min. Stream ohne Interaktion | Kein Memory-Leak, stabile CPU/RAM-Werte |
| 3 Viewer gleichzeitig | Alle erhalten stabilen Stream |
| Monitor mit 4K auflösung | Stream und Mauskoordinaten korrekt |
| Skalierung SD auf 4K Monitor | ~960×540 effektive Auflösung im Browser |

**Input & Kontrolle:**

| Szenario | Erwartetes Ergebnis |
|---|---|
| Maus über gesamten Bildschirm | Kein Drift, keine Koordinaten-Verschiebung |
| Alle Buchstaben A–Z tippen | Korrekte Zeichen auf Host |
| Ctrl+Z, Ctrl+C, Ctrl+V | Funktionieren im Remote-Kontext |
| Alt+F4 senden | Aktives Fenster schließt sich auf Host |
| Escape drücken | Fernsteuerung freigegeben |

**Clipboard & Dateitransfer:**

| Szenario | Erwartetes Ergebnis |
|---|---|
| Langer Text (10.000 Zeichen) kopieren | Vollständig übertragen |
| PNG-Bild (5 MB) in Clipboard | Korrekt übertragen und eingefügt |
| Datei 499 MB uploaden | Erfolgreich, Prüfsumme korrekt |
| Datei während Stream-Betrieb | Stream bleibt stabil |
| WLAN-Verbindung (schwankend) | Transfer-Backpressure verhindert Crash |

**Mobile:**

| Szenario | Erwartetes Ergebnis |
|---|---|
| iPhone Safari | Verbindung und Stream funktionieren |
| Android Chrome | Vollständige Touch-Eingabe |
| Tablet, Querformat | Canvas füllt Bildschirm korrekt |
| Doppeltipp | Doppelklick wird ausgeführt |

---

## 6.8 Bekannte Einschränkungen (dokumentieren)

Diese Punkte sollten in einer internen README festgehalten werden:

- **UAC-Dialoge:** Nicht sichtbar im Stream (bewusste Designentscheidung, Phase 1 festgelegt). DXGI liefert in diesem Moment automatisch einen schwarzen Frame.
- **Windows Sperrbildschirm:** Nicht sichtbar im Stream. Der Stream zeigt einen schwarzen Frame. Entsperren ist nicht möglich (kein Dienst-Kontext).
- **Ctrl+Alt+Del:** Kann nicht per SendInput gesendet werden (Windows Security). Ist nicht implementiert.
- **Multiple GPUs:** Nur Monitore an der ersten DXGI-Adapter werden unterstützt. Systeme mit eGPUs können Probleme haben.
- **Browser-Clipboard:** Erfordert aktiven Fokus im Tab. Automatische Synchronisation ohne Nutzerinteraktion ist browser-seitig nicht möglich.
- **Touch-Eingabe:** Einfache Touch-zu-Maus-Konvertierung. Keine Multi-Touch-Gesten.

---

## 6.9 Deliverables & Akzeptanzkriterien

### Checkliste Phase 6

**Installer:**
- [ ] `RemoteDesk-Setup-1.0.0.exe` wird korrekt gebaut
- [ ] Installation ohne vorherige .NET-Installation möglich (self-contained)
- [ ] Firewall-Regel (eingehend, Port 8443) wird korrekt angelegt
- [ ] Firewall-Regel (ausgehend, Port 443) wird korrekt angelegt
- [ ] Deinstallation entfernt beide Firewall-Regeln vollständig
- [ ] Autostart-Task wird bei gewählter Option angelegt
- [ ] Autostart-Task wird bei Deinstallation entfernt
- [ ] Installation auf Windows 10 (21H2) getestet
- [ ] Installation auf Windows 11 getestet

**UI-Polish:**
- [ ] Alle 5 Settings-Tabs vollständig und funktionsfähig
- [ ] Tray-Icon wechselt zwischen idle/aktiv-Icons
- [ ] Live-Statistik in Settings aktualisiert alle 2 Sekunden
- [ ] Session-Code im Tray-Menü sichtbar und kopierbar

**Browser-Finalisierung:**
- [ ] Vollbild-Modus (F11 und Button) funktioniert in Chrome, Firefox, Edge
- [ ] Toolbar blendet sich im Vollbild bei Maus-Inaktivität aus
- [ ] Mobile: Touch-Eingabe auf iPhone und Android getestet
- [ ] Dark/Light Mode folgt Browser-/OS-Einstellung
- [ ] Verbindungsqualitäts-Indikator (grün/orange/rot) korrekt
- [ ] Auto-Reconnect im Browser bei WebSocket-Verbindungsverlust

**Stabilität:**
- [ ] 30-Minuten-Dauertest ohne Speicher-/CPU-Leak bestanden
- [ ] 3 gleichzeitige Viewer über 10 Minuten stabil
- [ ] Auto-Reconnect Host ↔ Relay getestet (Relay manuell neu starten)

---

## Projekt abgeschlossen

Nach Phase 6 ist **RemoteDesk** ein vollständiges, nutzbares Produkt mit:

- Sicherem, selbst gehostetem Relay-Server auf dem QNAP NAS
- Einfacher Browser-basierter Verbindung via Session-Code
- Voller Fernsteuerung mit Multi-Viewer-Support
- Clipboard-Sync und Dateiübertragung
- Professionellem Windows-Installer mit automatischer Firewall-Konfiguration

---

*RemoteDesk · Phase 6 von 6 · Stand: April 2026*
