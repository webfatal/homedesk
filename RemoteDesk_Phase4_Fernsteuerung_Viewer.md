# RemoteDesk · Phase 4 — Fernsteuerung, Multi-Viewer & Rollen

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
| **4 ← Sie sind hier** | Fernsteuerung, Multi-Viewer, Rollen | 1 Woche |
| 5 | Clipboard, Dateiübertragung | 1 Woche |
| 6 | Installer, Firewall-Konfiguration, UI-Polish | 1 Woche |

---

## Phase 4 — Ziel & Abgrenzung

**Ziel:** Vollständige Fernsteuerung (Maus + Tastatur), mehrere simultane Viewer und ein klares Rollen-Modell (View-Only vs. Remote Control) mit Host-gesteuerter Zugriffsverwaltung.

**In dieser Phase enthalten:**
- Input-Injection via Windows `SendInput()` API (Maus + Tastatur)
- Mausbewegung, Klick (alle Tasten), Doppelklick, Scroll
- Tastatureingaben inkl. Sondertasten und Modifier-Keys
- Multi-Viewer: N gleichzeitige Verbindungen
- Kontroll-Anfrage-Protokoll (Viewer fragt, Host genehmigt)
- Exklusives Kontroll-Modell (max. ein Controller gleichzeitig)
- Viewer-Liste im Browser und im Host-UI
- Escape-Taste = sofortige Kontrollfreigabe (Sicherheits-Shortcut)

**Noch nicht in dieser Phase:**
- Clipboard / Dateiübertragung (→ Phase 5)
- Installer (→ Phase 6)

**Voraussetzungen:**
- Phasen 1–3 vollständig abgeschlossen
- `CoordinateMapper` aus Phase 3 vorhanden
- Relay-Server unterstützt Session-Events und Viewer-Routing

---

## 4.1 Windows Input Injection

### InputInjector.cs

```csharp
// RemoteDesk.Host/Input/InputInjector.cs
using System.Runtime.InteropServices;

public class InputInjector
{
    // Windows API: INPUT-Struktur
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;        // 0=MOUSE, 1=KEYBOARD, 2=HARDWARE
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx, dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs,
        INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    // Mausbewegung zu absoluten Desktop-Koordinaten
    public void MoveMouse(int desktopX, int desktopY)
    {
        // SendInput braucht normalisierte Koordinaten (0–65535)
        int screenW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int screenH = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        int normX = (int)((desktopX * 65535.0) / screenW);
        int normY = (int)((desktopY * 65535.0) / screenH);

        var input = new INPUT
        {
            type = 0,
            union = new INPUTUNION { mi = new MOUSEINPUT {
                dx = normX,
                dy = normY,
                dwFlags = 0x8001 // MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
            }}
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // Mausklick (left/right/middle, down oder up)
    public void MouseButton(string button, bool down)
    {
        uint flags = (button, down) switch {
            ("left",   true)  => 0x0002, // MOUSEEVENTF_LEFTDOWN
            ("left",   false) => 0x0004, // MOUSEEVENTF_LEFTUP
            ("right",  true)  => 0x0008, // MOUSEEVENTF_RIGHTDOWN
            ("right",  false) => 0x0010, // MOUSEEVENTF_RIGHTUP
            ("middle", true)  => 0x0020, // MOUSEEVENTF_MIDDLEDOWN
            ("middle", false) => 0x0040, // MOUSEEVENTF_MIDDLEUP
            _ => 0
        };

        if (flags == 0) return;
        var input = new INPUT { type = 0,
            union = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags }}};
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // Mausrad (vertikal oder horizontal)
    public void MouseWheel(int delta, bool horizontal = false)
    {
        uint flags = horizontal ? 0x01000u : 0x0800u; // HWHEEL / WHEEL
        var input = new INPUT { type = 0,
            union = new INPUTUNION { mi = new MOUSEINPUT {
                mouseData = (uint)(delta * 120), // Windows: 1 Klick = 120
                dwFlags = flags
            }}};
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // Tastendruck (Virtual Key Code, down oder up)
    public void KeyPress(ushort vkCode, bool down)
    {
        uint flags = down ? 0u : 0x0002u; // 0=down, KEYEVENTF_KEYUP=up

        // Extended Keys (Pfeiltasten, Num, etc.) brauchen KEYEVENTF_EXTENDEDKEY
        bool isExtended = vkCode is >= 0x21 and <= 0x2E  // PageUp..Delete
            or 0x5B or 0x5C  // Win-Tasten
            or >= 0x6F and <= 0x6F // Num /
            or 0x11 or 0x12; // Ctrl, Alt (rechts)
        if (isExtended) flags |= 0x0001u;

        var input = new INPUT { type = 1,
            union = new INPUTUNION { ki = new KEYBDINPUT {
                wVk = vkCode,
                dwFlags = flags
            }}};
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
```

---

## 4.2 Protokollerweiterung: Input-Messages

Alle Input-Events kommen als JSON vom Browser über den Relay-Kanal:

```json
// Mausbewegung (normalisiert 0.0–1.0)
{ "type": "input_mouse", "action": "move",
  "x": 0.4213, "y": 0.6105 }

// Mausklick
{ "type": "input_mouse", "action": "click",
  "button": "left", "down": true }

// Scroll
{ "type": "input_mouse", "action": "scroll",
  "delta": -3, "horizontal": false }

// Tastendruck
{ "type": "input_key", "action": "keydown",
  "vkCode": 65 }   // 'A'

// Taste loslassen
{ "type": "input_key", "action": "keyup",
  "vkCode": 65 }
```

**Host-seitiger Input-Router:**

```csharp
// RemoteDesk.Host/Input/InputRouter.cs
public class InputRouter
{
    private readonly InputInjector _injector;
    private readonly CoordinateMapper _mapper;
    private string? _authorizedViewerId; // nur dieser darf steuern

    public void SetAuthorizedViewer(string? viewerId)
        => _authorizedViewerId = viewerId;

    public void HandleInputMessage(string viewerId, JsonElement msg)
    {
        // Sicherheits-Check: nur autorisierter Viewer
        if (viewerId != _authorizedViewerId) return;

        var type   = msg.GetProperty("type").GetString();
        var action = msg.GetProperty("action").GetString();

        if (type == "input_mouse")
        {
            switch (action)
            {
                case "move":
                    var nx = msg.GetProperty("x").GetSingle();
                    var ny = msg.GetProperty("y").GetSingle();
                    var (dx, dy) = _mapper.MapToDesktop(nx, ny, _currentMonitor);
                    _injector.MoveMouse(dx, dy);
                    break;

                case "click":
                    var btn  = msg.GetProperty("button").GetString()!;
                    var down = msg.GetProperty("down").GetBoolean();
                    _injector.MouseButton(btn, down);
                    break;

                case "scroll":
                    var delta = msg.GetProperty("delta").GetInt32();
                    var horiz = msg.GetProperty("horizontal").GetBoolean();
                    _injector.MouseWheel(delta, horiz);
                    break;
            }
        }
        else if (type == "input_key")
        {
            var vk   = (ushort)msg.GetProperty("vkCode").GetInt32();
            var down = action == "keydown";
            _injector.KeyPress(vk, down);
        }
    }
}
```

---

## 4.3 Multi-Viewer Kontroll-Modell

### Kontroll-Anfrage-Ablauf (vollständig)

```
Viewer 2 (Browser) klickt "Fernsteuerung anfragen"
    │
    ├─ Browser → Relay: { "type": "control_request", "viewerId": "v2-uuid" }
    │
    ├─ Relay → Host: { "type": "control_request", "viewerId": "v2-uuid",
    │                   "ip": "203.0.113.42", "browser": "Chrome 124" }
    │
    ├─ Host: WPF Approval-Dialog erscheint (modal, immer im Vordergrund)
    │
    ├─ HOST KLICKT "ERLAUBEN":
    │   ├─ Host → Relay: { "type": "control_response",
    │   │                   "viewerId": "v2-uuid", "granted": true }
    │   │
    │   ├─ Relay: setzt controlGrantedTo = "v2-uuid"
    │   │
    │   ├─ Relay → Viewer 2: { "type": "control_granted" }
    │   │
    │   └─ Relay → alle anderen Viewer:
    │               { "type": "control_changed", "controller": "v2-uuid" }
    │
    └─ HOST KLICKT "ABLEHNEN":
        └─ Relay → Viewer 2: { "type": "control_denied" }
```

### Relay-Session-Erweiterung (Phase 4)

```csharp
// RemoteDesk.Relay/Session/RelaySession.cs (erweitert)
public class RelaySession
{
    public string Code { get; init; }
    public WebSocket HostConnection { get; set; }
    public ConcurrentDictionary<string, ViewerEntry> Viewers { get; } = new();
    public string? ControlGrantedTo { get; set; }  // ViewerId oder null

    // Nur Input-Events vom autorisierten Controller weiterleiten
    public bool IsController(string viewerId)
        => ControlGrantedTo == viewerId;

    // Controller-Rechte entziehen (vom Host ausgelöst)
    public void RevokeControl()
    {
        ControlGrantedTo = null;
    }
}
```

### Host: Kontroll-Verwaltung (WPF)

**Viewer-Liste im Settings-Fenster:**

```
┌──────────────────────────────────────────────┐
│  Aktive Viewer (3)                           │
├──────────────────────────────────────────────┤
│  🖱 Viewer 1  203.0.113.42  Chrome 124  [X] │  ← Controller (blau)
│  👁 Viewer 2  192.168.1.45  Edge 124    [X] │
│  👁 Viewer 3  77.12.34.56   Firefox 130 [X] │
├──────────────────────────────────────────────┤
│  [Fernsteuerung entziehen]  [Alle trennen]   │
└──────────────────────────────────────────────┘
```

**Approval-Dialog (WPF, immer im Vordergrund):**

```
┌───────────────────────────────────────────────┐
│  🖥 Fernsteuerungs-Anfrage                    │
├───────────────────────────────────────────────┤
│  IP-Adresse:  203.0.113.42                   │
│  Browser:     Chrome 124 / Windows 11        │
│  Uhrzeit:     15:42:07                       │
│                                               │
│  Einem Viewer die Fernsteuerung geben?        │
│                                               │
│  Hinweis: Andere Viewer verlieren die         │
│  Kontrolle sobald du zustimmst.               │
│                                               │
│         [ Erlauben ]     [ Ablehnen ]         │
└───────────────────────────────────────────────┘
```

---

## 4.4 Browser-Client: Input-Capturing

### Input-Handling in viewer.js

```javascript
class InputHandler {
    constructor(canvas, ws, viewerId) {
        this.canvas   = canvas;
        this.ws       = ws;
        this.viewerId = viewerId;
        this._active  = false;
        this._lastMousePos = null;
    }

    // Aktiviert wenn Kontrolle gewährt wurde
    activate() {
        this._active = true;
        this.canvas.style.cursor = 'none'; // Host-Cursor ist sichtbar

        this.canvas.addEventListener('mousemove',  this._onMouseMove.bind(this));
        this.canvas.addEventListener('mousedown',  this._onMouseDown.bind(this));
        this.canvas.addEventListener('mouseup',    this._onMouseUp.bind(this));
        this.canvas.addEventListener('wheel',      this._onWheel.bind(this));
        this.canvas.addEventListener('contextmenu', e => e.preventDefault());

        // Tastatur-Capturing
        this.canvas.tabIndex = 0;
        this.canvas.focus();
        document.addEventListener('keydown', this._onKeyDown.bind(this));
        document.addEventListener('keyup',   this._onKeyUp.bind(this));
    }

    // Deaktiviert (Escape oder Kontrolle entzogen)
    deactivate() {
        this._active = false;
        this.canvas.style.cursor = 'default';
        this.canvas.removeEventListener('mousemove',  this._onMouseMove);
        this.canvas.removeEventListener('mousedown',  this._onMouseDown);
        this.canvas.removeEventListener('mouseup',    this._onMouseUp);
        this.canvas.removeEventListener('wheel',      this._onWheel);
        document.removeEventListener('keydown', this._onKeyDown);
        document.removeEventListener('keyup',   this._onKeyUp);
    }

    _getNormalized(e) {
        const rect = this.canvas.getBoundingClientRect();
        return {
            x: Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width)),
            y: Math.max(0, Math.min(1, (e.clientY - rect.top)  / rect.height))
        };
    }

    _send(msg) {
        if (this.ws.readyState === WebSocket.OPEN)
            this.ws.send(JSON.stringify(msg));
    }

    _onMouseMove(e) {
        const pos = this._getNormalized(e);
        // Throttle: nur senden wenn Bewegung > 0.001 (1 Promille)
        if (this._lastMousePos &&
            Math.abs(pos.x - this._lastMousePos.x) < 0.001 &&
            Math.abs(pos.y - this._lastMousePos.y) < 0.001) return;
        this._lastMousePos = pos;
        this._send({ type: 'input_mouse', action: 'move', ...pos });
    }

    _onMouseDown(e) {
        const btn = ['left', 'middle', 'right'][e.button] || 'left';
        this._send({ type: 'input_mouse', action: 'click', button: btn, down: true });
    }

    _onMouseUp(e) {
        const btn = ['left', 'middle', 'right'][e.button] || 'left';
        this._send({ type: 'input_mouse', action: 'click', button: btn, down: false });
    }

    _onWheel(e) {
        e.preventDefault();
        const delta = Math.sign(e.deltaY) * Math.ceil(Math.abs(e.deltaY) / 100);
        this._send({ type: 'input_mouse', action: 'scroll',
                     delta, horizontal: e.shiftKey });
    }

    _onKeyDown(e) {
        // Escape = sofortige Kontrolle freigeben
        if (e.key === 'Escape') {
            this._sendControlRelease();
            return;
        }
        // Browser-Shortcuts unterdrücken (F5, Ctrl+T, etc.)
        if (e.ctrlKey && ['t','w','n','r'].includes(e.key.toLowerCase()))
            e.preventDefault();

        this._send({ type: 'input_key', action: 'keydown',
                     vkCode: this._browserKeyToVK(e.code) });
    }

    _onKeyUp(e) {
        this._send({ type: 'input_key', action: 'keyup',
                     vkCode: this._browserKeyToVK(e.code) });
    }

    // Browser KeyboardEvent.code → Windows Virtual Key Code
    _browserKeyToVK(code) {
        const map = {
            'KeyA': 0x41, 'KeyB': 0x42, 'KeyC': 0x43, 'KeyD': 0x44,
            'KeyE': 0x45, 'KeyF': 0x46, 'KeyG': 0x47, 'KeyH': 0x48,
            'KeyI': 0x49, 'KeyJ': 0x4A, 'KeyK': 0x4B, 'KeyL': 0x4C,
            'KeyM': 0x4D, 'KeyN': 0x4E, 'KeyO': 0x4F, 'KeyP': 0x50,
            'KeyQ': 0x51, 'KeyR': 0x52, 'KeyS': 0x53, 'KeyT': 0x54,
            'KeyU': 0x55, 'KeyV': 0x56, 'KeyW': 0x57, 'KeyX': 0x58,
            'KeyY': 0x59, 'KeyZ': 0x5A,
            'Digit0': 0x30, 'Digit1': 0x31, 'Digit2': 0x32, 'Digit3': 0x33,
            'Digit4': 0x34, 'Digit5': 0x35, 'Digit6': 0x36, 'Digit7': 0x37,
            'Digit8': 0x38, 'Digit9': 0x39,
            'F1': 0x70, 'F2': 0x71, 'F3': 0x72, 'F4': 0x73,
            'F5': 0x74, 'F6': 0x75, 'F7': 0x76, 'F8': 0x77,
            'F9': 0x78, 'F10': 0x79, 'F11': 0x7A, 'F12': 0x7B,
            'Enter': 0x0D, 'Space': 0x20, 'Backspace': 0x08, 'Tab': 0x09,
            'Escape': 0x1B, 'Delete': 0x2E, 'Insert': 0x2D,
            'Home': 0x24, 'End': 0x23, 'PageUp': 0x21, 'PageDown': 0x22,
            'ArrowLeft': 0x25, 'ArrowUp': 0x26,
            'ArrowRight': 0x27, 'ArrowDown': 0x28,
            'ControlLeft': 0x11, 'ControlRight': 0x11,
            'ShiftLeft': 0x10, 'ShiftRight': 0x10,
            'AltLeft': 0x12, 'AltRight': 0x12,
            'MetaLeft': 0x5B, 'MetaRight': 0x5C,
        };
        return map[code] ?? 0;
    }

    _sendControlRelease() {
        this._send({ type: 'control_release' });
        this.deactivate();
    }
}
```

---

## 4.5 Browser-UI: Viewer-Rollen

**Kontroll-Leiste (unterhalb Canvas):**

```javascript
// Zustandsmaschine für Viewer-Rolle
class ViewerRoleUI {
    constructor(ws, inputHandler) {
        this.ws = ws;
        this.input = inputHandler;
        this._state = 'view_only'; // view_only | pending | control
    }

    setState(state, controllerName = null) {
        this._state = state;
        const bar = document.getElementById('control-bar');

        switch (state) {
            case 'view_only':
                bar.innerHTML = `
                    <span class="badge view-only">👁 Nur ansehen</span>
                    <button onclick="roleUI.requestControl()">
                        🖱 Fernsteuerung anfragen
                    </button>`;
                this.input.deactivate();
                break;

            case 'pending':
                bar.innerHTML = `
                    <span class="badge pending">
                        ⏳ Warte auf Host-Bestätigung...
                    </span>
                    <button onclick="roleUI.cancelRequest()">Abbrechen</button>`;
                break;

            case 'control':
                bar.innerHTML = `
                    <span class="badge control">🖱 Fernsteuerung aktiv</span>
                    <button onclick="roleUI.releaseControl()">
                        Kontrolle abgeben [Esc]
                    </button>`;
                this.input.activate();
                break;

            case 'viewer_has_control':
                bar.innerHTML = `
                    <span class="badge view-only">
                        👁 ${controllerName} steuert gerade
                    </span>`;
                this.input.deactivate();
                break;
        }
    }

    requestControl() {
        this.ws.send(JSON.stringify({ type: 'control_request' }));
        this.setState('pending');
    }

    releaseControl() {
        this.ws.send(JSON.stringify({ type: 'control_release' }));
        this.setState('view_only');
        this.input.deactivate();
    }

    cancelRequest() {
        this.ws.send(JSON.stringify({ type: 'control_cancel' }));
        this.setState('view_only');
    }
}
```

**Viewer-Liste (einblendbar):**

```html
<div id="viewer-list" class="panel">
  <h3>Verbundene Viewer (3)</h3>
  <ul>
    <li class="controller">🖱 Du &lt;— aktiver Controller</li>
    <li class="viewer">👁 Viewer 2 (192.168.1.45)</li>
    <li class="viewer">👁 Viewer 3 (77.12.34.56)</li>
  </ul>
</div>
```

---

## 4.6 Deliverables & Akzeptanzkriterien

### Checkliste Phase 4

**Input-Injection:**
- [ ] Mausbewegung funktioniert korrekt (Cursor bewegt sich auf Host)
- [ ] Linksklick, Rechtsklick, Mittelklick funktionieren
- [ ] Doppelklick funktioniert (2× Klick-Events in < 300ms)
- [ ] Mausrad (vertikal und horizontal) funktioniert
- [ ] Buchstaben A–Z, Ziffern 0–9 werden korrekt injiziert
- [ ] Sondertasten: Enter, Tab, Backspace, Delete, Escape
- [ ] Navigationstasten: Pfeiltasten, Home, End, PageUp, PageDown
- [ ] Modifier-Keys: Ctrl, Shift, Alt (inkl. Kombinationen wie Ctrl+C)
- [ ] Funktionstasten F1–F12 funktionieren
- [ ] DPI-Skalierung: Maus landet an der richtigen Stelle (auch auf 4K)

**Multi-Viewer & Rollen:**
- [ ] Mehrere Browser-Tabs können sich gleichzeitig verbinden
- [ ] Alle Viewer erhalten denselben Stream
- [ ] Viewer sieht "Nur ansehen"-Badge wenn kein Controller
- [ ] Kontroll-Anfrage-Button sendet Request an Host
- [ ] Approval-Dialog erscheint im Host-WPF (immer vorne)
- [ ] Host genehmigt → Browser-Tab bekommt Kontrolle
- [ ] Host lehnt ab → Browser-Tab bekommt "Abgelehnt"-Meldung
- [ ] Maximal ein Controller gleichzeitig
- [ ] Alle anderen Viewer sehen wer gerade steuert
- [ ] Escape-Taste im Browser = sofortige Kontrollfreigabe
- [ ] Host kann Kontrolle jederzeit entziehen (Button im UI)
- [ ] Viewer-Liste im Host-UI zeigt alle verbundenen Viewer

### Testszenarien

| Szenario | Erwartetes Ergebnis |
|---|---|
| Maus über Canvas bewegen | Cursor bewegt sich auf Host-Bildschirm |
| Text tippen (View-Only) | Nichts passiert auf Host |
| Kontroll-Anfrage → Host lehnt ab | Browser zeigt "Abgelehnt" |
| Kontroll-Anfrage → Host erlaubt | Maus/Tastatur werden aktiv |
| Escape drücken | Kontrolle freigegeben, wieder View-Only |
| Ctrl+C im Browser | Wird an Host weitergeleitet (nicht Browser-Aktion) |
| Viewer 3 verbindet während Viewer 2 steuert | Viewer 3 sieht "Viewer 2 steuert" |
| Viewer 2 trennt Verbindung | Host erhält "viewer_disconnected"-Event |
| 4K Monitor, Maus klicken | Klick landet an korrekter Position |

---

## Übergang zu Phase 5

Phase 4 liefert eine vollständige Fernsteuerungs-Lösung mit Multi-Viewer-Support. Phase 5 ergänzt:

- Text-Zwischenablage bidirektional (Host ↔ Browser)
- Bild-Zwischenablage (PNG)
- Dateiübertragung Browser → Host (Drag & Drop)
- Dateiübertragung Host → Browser (mit Download-Dialog)
- Fortschrittsanzeige für Dateiübertragungen

---

*RemoteDesk · Phase 4 von 6 · Stand: April 2026*
