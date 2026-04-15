# RemoteDesk · Phase 5 — Clipboard & Dateiübertragung

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
| **5 ← Sie sind hier** | Clipboard, Dateiübertragung | 1 Woche |
| 6 | Installer, Firewall-Konfiguration, UI-Polish | 1 Woche |

---

## Phase 5 — Ziel & Abgrenzung

**Ziel:** Bidirektionale Zwischenablage-Synchronisation (Text und Bilder) sowie Dateiübertragung in beide Richtungen zwischen Host und Browser-Client.

**In dieser Phase enthalten:**
- Clipboard-Monitor auf Host (WinEventHook)
- Text-Clipboard bidirektional (Host ↔ Browser)
- Bild-Clipboard (PNG, Host → Browser und Browser → Host)
- Echo-Loop-Schutz (keine Endlosschleife beim Synchronisieren)
- Dateiübertragung Browser → Host (Drag & Drop auf Canvas, alternativ Button)
- Dateiübertragung Host → Browser (Tray-Menü, Download-Dialog im Browser)
- Chunked Transfer für große Dateien (bis 500 MB)
- Fortschrittsanzeige in beide Richtungen
- Windows-Benachrichtigung bei empfangener Datei

**Noch nicht in dieser Phase:**
- Installer (→ Phase 6)

**Voraussetzungen:**
- Phasen 1–4 vollständig abgeschlossen
- WebSocket-Kanäle für Host ↔ Relay ↔ Browser bereits vorhanden
- HTTPS/WSS aktiv (Clipboard API erfordert sicheren Kontext)

---

## 5.1 Protokollerweiterung: Clipboard & File

Neue Nachrichtentypen (werden über denselben WebSocket-Kanal gesendet):

```json
// Clipboard: Text
{ "type": "clipboard_sync",
  "contentType": "text",
  "text": "Hallo Welt",
  "sourceId": "host-abc123"   // Echo-Loop-Schutz
}

// Clipboard: Bild (PNG, base64-kodiert)
{ "type": "clipboard_sync",
  "contentType": "image",
  "dataBase64": "iVBORw0KGgo...",
  "width": 800, "height": 600,
  "sourceId": "browser-xyz456"
}

// Datei-Angebot (Host → Browser oder Browser → Host)
{ "type": "file_offer",
  "transferId": "transfer-uuid-1",
  "fileName": "bericht.pdf",
  "fileSize": 2457600,
  "direction": "host_to_browser"   // oder "browser_to_host"
}

// Datei-Chunk (binär, mit Header)
// Byte 0:     Nachrichtentyp 0x10 = file_chunk
// Bytes 1–36: Transfer-ID (UUID als ASCII)
// Bytes 37–40: Chunk-Nummer (uint32 big-endian)
// Bytes 41–44: Chunk-Größe (uint32)
// Bytes 45+:  Nutzdaten

// Datei-Abschluss
{ "type": "file_complete",
  "transferId": "transfer-uuid-1",
  "checksum": "sha256:abcdef..." }

// Datei-Fehler / Abbruch
{ "type": "file_abort",
  "transferId": "transfer-uuid-1",
  "reason": "user_cancelled" }
```

---

## 5.2 Clipboard-Monitor (Host)

**ClipboardMonitor.cs:**

```csharp
// RemoteDesk.Host/Clipboard/ClipboardMonitor.cs
public class ClipboardMonitor : IDisposable
{
    private HwndSource? _hwndSource;
    private string? _lastSentSourceId;

    public event EventHandler<ClipboardChangedArgs>? ClipboardChanged;

    public void Start()
    {
        // Unsichtbares Fenster für Clipboard-Nachrichten
        var p = new HwndSourceParameters("RemoteDeskClipboard")
        {
            Width = 0, Height = 0,
            WindowStyle = 0 // WS_OVERLAPPED
        };
        _hwndSource = new HwndSource(p);
        _hwndSource.AddHook(WndProc);

        // Clipboard-Benachrichtigungen registrieren
        AddClipboardFormatListener(_hwndSource.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg,
        IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;

        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            Task.Run(ReadAndNotify); // Async, nicht im UI-Thread blockieren
        }
        return IntPtr.Zero;
    }

    private async Task ReadAndNotify()
    {
        // WPF-Thread für Clipboard-Zugriff erforderlich
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                ClipboardContent? content = null;

                if (Clipboard.ContainsText())
                {
                    content = new ClipboardContent(
                        ContentType: ClipboardContentType.Text,
                        Text:        Clipboard.GetText(),
                        ImagePng:    null,
                        SourceId:    $"host-{Guid.NewGuid():N}"
                    );
                }
                else if (Clipboard.ContainsImage())
                {
                    var bitmap = Clipboard.GetImage();
                    var pngBytes = BitmapToPng(bitmap);
                    content = new ClipboardContent(
                        ContentType: ClipboardContentType.Image,
                        Text:        null,
                        ImagePng:    pngBytes,
                        SourceId:    $"host-{Guid.NewGuid():N}"
                    );
                }

                if (content != null)
                {
                    // Echo-Loop-Schutz: nicht senden wenn wir selbst gesetzt haben
                    if (content.SourceId != _lastSentSourceId)
                        ClipboardChanged?.Invoke(this,
                            new ClipboardChangedArgs(content));
                }
            }
            catch (ExternalException) { /* Clipboard temporär gesperrt */ }
        });
    }

    // Schreibt empfangenen Clipboard-Inhalt in Windows-Zwischenablage
    public void SetFromRemote(ClipboardContent content)
    {
        _lastSentSourceId = content.SourceId; // Echo-Loop-Schutz setzen

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (content.ContentType == ClipboardContentType.Text &&
                content.Text != null)
            {
                Clipboard.SetText(content.Text);
            }
            else if (content.ContentType == ClipboardContentType.Image &&
                     content.ImagePng != null)
            {
                var bitmap = PngToBitmapSource(content.ImagePng);
                Clipboard.SetImage(bitmap);
            }
        });
    }

    private static byte[] BitmapToPng(BitmapSource bitmap)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(ms);
        return ms.ToArray();
    }

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public void Dispose()
    {
        if (_hwndSource != null)
            RemoveClipboardFormatListener(_hwndSource.Handle);
        _hwndSource?.Dispose();
    }
}

public record ClipboardContent(
    ClipboardContentType ContentType,
    string? Text,
    byte[]? ImagePng,
    string SourceId
);

public enum ClipboardContentType { Text, Image }
public record ClipboardChangedArgs(ClipboardContent Content);
```

---

## 5.3 Clipboard-Routing im Relay

Der Relay-Server leitet Clipboard-Nachrichten wie normale JSON-Messages weiter:

- Host → Relay → alle Viewer (Broadcast)
- Browser (Controller) → Relay → Host

Nur der aktuelle Controller darf Clipboard-Updates an den Host senden
(gleiche Autorisierungs-Prüfung wie Input-Events in Phase 4).

---

## 5.4 Clipboard im Browser

```javascript
// RemoteDesk.Client/clipboard.js
class ClipboardSync {
    constructor(ws) {
        this.ws = ws;
        this._lastSourceId = null;
    }

    // Empfängt Clipboard-Update vom Host → Browser-Zwischenablage setzen
    async onHostClipboard(msg) {
        // Echo-Loop-Schutz
        if (msg.sourceId === this._lastSourceId) return;
        this._lastSourceId = msg.sourceId;

        try {
            if (msg.contentType === 'text') {
                await navigator.clipboard.writeText(msg.text);
                this._showNotification('📋 Zwischenablage übernommen');
            } else if (msg.contentType === 'image') {
                const blob = this._base64ToBlob(msg.dataBase64, 'image/png');
                const item = new ClipboardItem({ 'image/png': blob });
                await navigator.clipboard.write([item]);
                this._showNotification('🖼 Bild in Zwischenablage übernommen');
            }
        } catch (e) {
            // Clipboard API erfordert Fokus — nicht immer verfügbar
            console.warn('Clipboard write failed:', e);
        }
    }

    // Sendet Browser-Clipboard-Inhalt an Host (nur wenn Controller)
    async sendToHost() {
        try {
            const items = await navigator.clipboard.read();
            for (const item of items) {
                if (item.types.includes('text/plain')) {
                    const blob = await item.getType('text/plain');
                    const text = await blob.text();
                    const sourceId = `browser-${crypto.randomUUID().substring(0, 8)}`;
                    this._lastSourceId = sourceId;
                    this.ws.send(JSON.stringify({
                        type: 'clipboard_sync', contentType: 'text',
                        text, sourceId
                    }));
                } else if (item.types.includes('image/png')) {
                    const blob = await item.getType('image/png');
                    const base64 = await this._blobToBase64(blob);
                    const sourceId = `browser-${crypto.randomUUID().substring(0, 8)}`;
                    this._lastSourceId = sourceId;
                    this.ws.send(JSON.stringify({
                        type: 'clipboard_sync', contentType: 'image',
                        dataBase64: base64, sourceId
                    }));
                }
            }
        } catch (e) {
            console.warn('Clipboard read failed:', e);
        }
    }

    _base64ToBlob(base64, mimeType) {
        const bytes = atob(base64);
        const arr   = new Uint8Array(bytes.length);
        for (let i = 0; i < bytes.length; i++) arr[i] = bytes.charCodeAt(i);
        return new Blob([arr], { type: mimeType });
    }

    async _blobToBase64(blob) {
        return new Promise(resolve => {
            const reader = new FileReader();
            reader.onload = () => resolve(reader.result.split(',')[1]);
            reader.readAsDataURL(blob);
        });
    }

    _showNotification(text) {
        // Kleine Toast-Benachrichtigung im Browser
        const toast = document.createElement('div');
        toast.className = 'toast';
        toast.textContent = text;
        document.body.appendChild(toast);
        setTimeout(() => toast.remove(), 3000);
    }
}
```

**Hinweis zur Clipboard API:**
Die Web Clipboard API erfordert einen sicheren Kontext (HTTPS) und Benutzerinteraktion (Fokus im Tab). Das self-signed Zertifikat muss daher im Browser einmalig akzeptiert sein — sonst verweigert der Browser den Zugriff.

---

## 5.5 Dateiübertragung Browser → Host

### Browser-Seite: Drag & Drop und Button-Upload

```javascript
// RemoteDesk.Client/filetransfer.js
class FileTransferClient {
    constructor(ws) {
        this.ws = ws;
        this._activeTransfers = new Map(); // transferId → { progress }
        this._setupDragDrop();
    }

    _setupDragDrop() {
        const canvas = document.getElementById('remote-canvas');

        // Drag & Drop auf Canvas
        canvas.addEventListener('dragover', e => {
            e.preventDefault();
            canvas.style.outline = '3px dashed #4caf50';
        });
        canvas.addEventListener('dragleave', () => {
            canvas.style.outline = '';
        });
        canvas.addEventListener('drop', async e => {
            e.preventDefault();
            canvas.style.outline = '';
            for (const file of e.dataTransfer.files)
                await this.uploadFile(file);
        });
    }

    async uploadFile(file) {
        const CHUNK_SIZE = 256 * 1024; // 256 KB
        const transferId = crypto.randomUUID();

        // Angebot senden
        this.ws.send(JSON.stringify({
            type: 'file_offer',
            transferId,
            fileName:  file.name,
            fileSize:  file.size,
            direction: 'browser_to_host'
        }));

        // Auf Akzeptanz vom Host warten (wird via session_event signalisiert)
        await this._waitForAcceptance(transferId);

        // Datei in Chunks senden
        const totalChunks = Math.ceil(file.size / CHUNK_SIZE);
        for (let chunkIdx = 0; chunkIdx < totalChunks; chunkIdx++) {
            const start = chunkIdx * CHUNK_SIZE;
            const end   = Math.min(start + CHUNK_SIZE, file.size);
            const chunk = await file.slice(start, end).arrayBuffer();

            // Binäres Paket zusammenbauen
            const header  = new Uint8Array(45);
            header[0]     = 0x10; // file_chunk
            // Transfer-ID (36 Bytes ASCII)
            new TextEncoder().encodeInto(transferId, header.subarray(1, 37));
            new DataView(header.buffer).setUint32(37, chunkIdx,   false);
            new DataView(header.buffer).setUint32(41, chunk.byteLength, false);

            const packet = new Uint8Array(header.byteLength + chunk.byteLength);
            packet.set(header);
            packet.set(new Uint8Array(chunk), header.byteLength);

            this.ws.send(packet.buffer);

            // Fortschritt aktualisieren
            const percent = Math.round(((chunkIdx + 1) / totalChunks) * 100);
            this._updateProgress(transferId, file.name, percent, 'upload');

            // Backpressure: kurze Pause wenn Buffer voll
            if (this.ws.bufferedAmount > 1024 * 1024)
                await new Promise(r => setTimeout(r, 50));
        }

        // Abschluss senden
        const sha256 = await this._calcSHA256(file);
        this.ws.send(JSON.stringify({
            type: 'file_complete', transferId,
            checksum: `sha256:${sha256}`
        }));

        this._updateProgress(transferId, file.name, 100, 'done');
    }

    async _calcSHA256(file) {
        const buffer = await file.arrayBuffer();
        const hash   = await crypto.subtle.digest('SHA-256', buffer);
        return Array.from(new Uint8Array(hash))
            .map(b => b.toString(16).padStart(2, '0')).join('');
    }

    _updateProgress(transferId, name, percent, state) {
        // Fortschrittsanzeige im Browser aktualisieren
        let el = document.getElementById(`transfer-${transferId}`);
        if (!el) {
            el = document.createElement('div');
            el.id = `transfer-${transferId}`;
            el.className = 'transfer-item';
            document.getElementById('transfer-panel').appendChild(el);
        }
        el.innerHTML = state === 'done'
            ? `✅ ${name} übertragen`
            : `📤 ${name} ... ${percent}%
               <progress value="${percent}" max="100"></progress>`;
    }
}
```

### Host-Seite: Datei-Empfang

```csharp
// RemoteDesk.Host/FileTransfer/FileReceiver.cs
public class FileReceiver
{
    private readonly string _downloadPath =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile),
            "Downloads", "RemoteDesk");

    private readonly Dictionary<string, FileTransferState> _transfers = new();

    public void HandleFileOffer(FileOfferMessage msg)
    {
        // Größenlimit prüfen
        if (msg.FileSize > _settings.MaxFileSizeBytes)
        {
            SendAbort(msg.TransferId, "file_too_large");
            return;
        }

        Directory.CreateDirectory(_downloadPath);
        var destPath = GetSafePath(msg.FileName);

        _transfers[msg.TransferId] = new FileTransferState
        {
            TransferId  = msg.TransferId,
            FileName    = msg.FileName,
            TotalSize   = msg.FileSize,
            DestPath    = destPath,
            Stream      = File.Create(destPath),
            ReceivedBytes = 0
        };

        // Akzeptanz bestätigen
        SendAccept(msg.TransferId);
    }

    public void HandleFileChunk(Span<byte> data)
    {
        // Header parsen (45 Bytes)
        if (data[0] != 0x10) return;
        var transferId = Encoding.ASCII.GetString(data[1..37]);
        var chunkIdx   = BinaryPrimitives.ReadUInt32BigEndian(data[37..41]);
        var chunkSize  = BinaryPrimitives.ReadUInt32BigEndian(data[41..45]);
        var payload    = data[45..(45 + (int)chunkSize)];

        if (!_transfers.TryGetValue(transferId, out var state)) return;

        state.Stream.Write(payload);
        state.ReceivedBytes += chunkSize;

        // Fortschritt an Tray-Icon-Tooltip senden
        var percent = (int)(state.ReceivedBytes * 100 / state.TotalSize);
        UpdateTrayProgress(state.FileName, percent);
    }

    public async Task HandleFileComplete(FileCompleteMessage msg)
    {
        if (!_transfers.TryGetValue(msg.TransferId, out var state)) return;

        state.Stream.Close();
        _transfers.Remove(msg.TransferId);

        // SHA256-Prüfsumme verifizieren
        var valid = await VerifyChecksum(state.DestPath, msg.Checksum);

        if (valid)
        {
            // Windows-Benachrichtigung
            ShowWindowsNotification(
                "Datei empfangen",
                $"{state.FileName} wurde in RemoteDesk-Downloads gespeichert.",
                state.DestPath);
        }
        else
        {
            File.Delete(state.DestPath);
            ShowWindowsNotification("Fehler", "Prüfsumme ungültig — Datei gelöscht.");
        }
    }

    // Verhindert Path-Traversal-Angriffe
    private string GetSafePath(string fileName)
    {
        var safe = Path.GetFileName(fileName); // entfernt Pfad-Komponenten
        safe = string.Concat(safe.Where(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' '));
        if (string.IsNullOrWhiteSpace(safe)) safe = "received_file";

        var dest = Path.Combine(_downloadPath, safe);

        // Doppelte Dateinamen auflösen
        int i = 1;
        while (File.Exists(dest))
        {
            var name = Path.GetFileNameWithoutExtension(safe);
            var ext  = Path.GetExtension(safe);
            dest = Path.Combine(_downloadPath, $"{name} ({i}){ext}");
            i++;
        }
        return dest;
    }
}
```

---

## 5.6 Dateiübertragung Host → Browser

### Host-Seite: Datei senden

```csharp
// RemoteDesk.Host/FileTransfer/FileSender.cs
public class FileSender
{
    private const int CHUNK_SIZE = 256 * 1024; // 256 KB

    // Wird durch Tray-Menü "Datei senden..." ausgelöst
    public async Task SendFileAsync(string filePath, string? targetViewerId = null)
    {
        var info = new FileInfo(filePath);

        if (info.Length > _settings.MaxFileSizeBytes)
        {
            ShowError("Datei zu groß (max. " +
                _settings.MaxFileSizeMB + " MB)");
            return;
        }

        var transferId = Guid.NewGuid().ToString();

        // Angebot an Browser senden
        await _relay.BroadcastJsonAsync(new {
            type       = "file_offer",
            transferId,
            fileName   = info.Name,
            fileSize   = info.Length,
            direction  = "host_to_browser"
        });

        // Auf Download-Bestätigung warten (Viewer klickt "Herunterladen")
        var viewerAccepted = await WaitForDownloadAcceptance(
            transferId, TimeSpan.FromSeconds(30));

        if (!viewerAccepted) return;

        // Datei in Chunks senden
        await using var fs = File.OpenRead(filePath);
        var buffer    = new byte[CHUNK_SIZE];
        int chunkIdx  = 0;
        int read;

        while ((read = await fs.ReadAsync(buffer)) > 0)
        {
            // Paket zusammenbauen (Header + Chunk)
            var packet = BuildChunkPacket(transferId, chunkIdx, buffer[..read]);
            await _relay.BroadcastBinaryAsync(packet);

            // Fortschritt aktualisieren
            var percent = (int)(fs.Position * 100 / info.Length);
            UpdateTrayProgress(info.Name, percent);

            chunkIdx++;
        }

        // Abschluss-Message
        var checksum = await CalcSHA256Async(filePath);
        await _relay.BroadcastJsonAsync(new {
            type = "file_complete", transferId,
            checksum = $"sha256:{checksum}"
        });
    }
}
```

### Browser-Seite: Datei empfangen und herunterladen

```javascript
// In filetransfer.js — empfängt file_offer vom Host
handleFileOffer(msg) {
    // Benachrichtigung anzeigen
    const sizeMB = (msg.fileSize / 1024 / 1024).toFixed(1);
    const el = document.createElement('div');
    el.className = 'file-offer';
    el.innerHTML = `
        <span>📥 Host sendet: <b>${msg.fileName}</b> (${sizeMB} MB)</span>
        <button onclick="fileTransfer.acceptDownload('${msg.transferId}')">
            Herunterladen
        </button>
        <button onclick="fileTransfer.rejectDownload('${msg.transferId}')">
            Ablehnen
        </button>`;
    document.getElementById('transfer-panel').appendChild(el);
}

acceptDownload(transferId) {
    this.ws.send(JSON.stringify({
        type: 'file_download_accept', transferId }));

    this._activeDownloads.set(transferId, {
        chunks: [], totalReceived: 0 });
}

// Empfängt binäre Chunks und baut Blob zusammen
handleFileChunk(buffer) {
    const view = new DataView(buffer);
    if (view.getUint8(0) !== 0x10) return;

    const transferId = new TextDecoder()
        .decode(new Uint8Array(buffer, 1, 36));
    const payload    = buffer.slice(45);

    const dl = this._activeDownloads.get(transferId);
    if (!dl) return;

    dl.chunks.push(payload);
    dl.totalReceived += payload.byteLength;

    this._updateProgress(transferId, dl.fileName,
        Math.round(dl.totalReceived * 100 / dl.totalSize), 'download');
}

async handleFileComplete(msg) {
    const dl = this._activeDownloads.get(msg.transferId);
    if (!dl) return;

    // Checksum verifizieren
    const blob = new Blob(dl.chunks);
    const hash = await this._calcSHA256(blob);

    if (`sha256:${hash}` !== msg.checksum) {
        this._showError('Prüfsumme ungültig — Download fehlgeschlagen');
        return;
    }

    // Browser-Download auslösen
    const url  = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href  = url;
    link.download = dl.fileName;
    link.click();
    URL.revokeObjectURL(url);

    this._updateProgress(msg.transferId, dl.fileName, 100, 'done');
    this._activeDownloads.delete(msg.transferId);
}
```

---

## 5.7 Host-UI: Einstellungen für Clipboard & Dateitransfer

**Tab "Sicherheit" (Phase 5 Erweiterung):**

```
┌────────────────────────────────────────────────────┐
│  Tab: Sicherheit                                   │
├────────────────────────────────────────────────────┤
│  Clipboard-Synchronisation:                        │
│  ● Aktiviert (Text und Bilder)                     │
│  ○ Nur Text                                        │
│  ○ Deaktiviert                                     │
│                                                    │
│  Dateiübertragung:                                 │
│  ● Browser → Host erlauben                         │
│  ● Host → Browser erlauben                         │
│                                                    │
│  Maximale Dateigröße:                              │
│  [500] MB   (0 = unbegrenzt)                       │
│                                                    │
│  Download-Ordner:                                  │
│  [%USERPROFILE%\Downloads\RemoteDesk]  [Ändern...] │
│                                                    │
│  Aktuell laufende Transfers: 0                     │
│  [Alle Transfers abbrechen]                        │
└────────────────────────────────────────────────────┘
```

---

## 5.8 Browser-UI: Transfer-Panel

```html
<!-- Einblend-Panel für aktive Transfers -->
<div id="transfer-panel" class="side-panel">
  <h3>📁 Dateitransfers</h3>

  <!-- Datei-Angebot vom Host -->
  <div class="file-offer">
    <span>📥 <b>bericht.pdf</b> (2.4 MB)</span>
    <button>Herunterladen</button>
    <button>Ablehnen</button>
  </div>

  <!-- Laufender Upload (Browser → Host) -->
  <div class="transfer-item">
    <span>📤 foto.jpg — 67%</span>
    <progress value="67" max="100"></progress>
    <button>Abbrechen</button>
  </div>

  <!-- Abgeschlossener Transfer -->
  <div class="transfer-item done">
    ✅ tabelle.xlsx übertragen
  </div>
</div>
```

---

## 5.9 Deliverables & Akzeptanzkriterien

### Checkliste Phase 5

**Clipboard:**
- [ ] Text-Clipboard: Host → Browser (Browser-Zwischenablage wird gesetzt)
- [ ] Text-Clipboard: Browser → Host (Windows-Clipboard wird gesetzt)
- [ ] Bild-Clipboard (PNG): Host → Browser
- [ ] Bild-Clipboard (PNG): Browser → Host
- [ ] Echo-Loop wird verhindert (kein Endlos-Ping-Pong)
- [ ] Clipboard-Sync funktioniert auch wenn Browser keinen Fokus hat (Host → Browser)
- [ ] Toast-Benachrichtigung im Browser bei empfangenem Clipboard-Update

**Dateiübertragung Browser → Host:**
- [ ] Drag & Drop von Datei auf Canvas löst Upload aus
- [ ] Button-Upload als Alternative zu Drag & Drop
- [ ] Fortschrittsanzeige in Prozent (live)
- [ ] Abbruch-Button während laufendem Transfer
- [ ] SHA256-Prüfsumme wird verifiziert
- [ ] Windows-Benachrichtigung nach erfolgreichem Empfang
- [ ] Datei landet in konfigurierbarem Download-Ordner
- [ ] Path-Traversal-Schutz (böswillige Dateinamen werden bereinigt)
- [ ] Doppelte Dateinamen werden korrekt aufgelöst (file (1).ext, file (2).ext)

**Dateiübertragung Host → Browser:**
- [ ] "Datei senden..." im Tray-Menü öffnet Datei-Dialog
- [ ] Datei-Angebot erscheint im Browser mit Name und Größe
- [ ] Browser-Nutzer kann annehmen oder ablehnen
- [ ] Fortschrittsanzeige im Browser
- [ ] Download-Dialog öffnet sich automatisch nach Abschluss
- [ ] SHA256-Prüfsumme wird im Browser verifiziert

**Allgemein:**
- [ ] Maximale Dateigröße konfigurierbar (Standard: 500 MB)
- [ ] Mehrere gleichzeitige Transfers möglich
- [ ] Backpressure verhindert WebSocket-Buffer-Überlauf

### Testszenarien

| Szenario | Erwartetes Ergebnis |
|---|---|
| Text auf Host kopieren | Browser-Clipboard enthält denselben Text |
| Text im Browser kopieren (Controller) | Windows-Clipboard aktualisiert |
| Bild auf Host in Clipboard | Browser-Clipboard enthält Bild |
| Datei (1 MB) per Drag & Drop | Upload, Fortschritt, Windows-Benachrichtigung |
| Datei (499 MB) | Upload läuft durch, kein Timeout |
| Datei (501 MB) | Fehlermeldung "Datei zu groß" |
| Datei mit Pfad im Namen (../../etc) | Bereinigter Dateiname, kein Path-Traversal |
| Host sendet Datei, Browser lehnt ab | Kein Download, kein Fehler |
| Transfer während laufendem Stream | Stream bleibt stabil |

---

## Übergang zu Phase 6

Phase 5 vervollständigt alle funktionalen Features. Phase 6 schließt das Projekt ab:

- Professioneller Windows-Installer (Inno Setup)
- Automatische Firewall-Konfiguration (Windows Defender)
- Vollständiges, poliertes WPF-UI mit allen Tabs
- Vollbild-Modus im Browser
- Mobile Touch-Unterstützung
- Auto-Reconnect-Logik
- Abschließende Qualitätssicherung

---

*RemoteDesk · Phase 5 von 6 · Stand: April 2026*
