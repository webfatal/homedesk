# RemoteDesk · Phase 2 — Relay-Server, Docker (QNAP), SSL, Session-Code

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
| **2 ← Sie sind hier** | Relay-Server, Docker (QNAP), SSL, Session-Code | 1–2 Wochen |
| 3 | Multi-Monitor, FPS-Steuerung, Skalierung | 1 Woche |
| 4 | Fernsteuerung, Multi-Viewer, Rollen | 1 Woche |
| 5 | Clipboard, Dateiübertragung | 1 Woche |
| 6 | Installer, Firewall-Konfiguration, UI-Polish | 1 Woche |

---

## Phase 2 — Ziel & Abgrenzung

**Ziel:** Die in Phase 1 aufgebaute LAN-Verbindung über einen selbst gehosteten Relay-Server (QNAP NAS, Docker) auf Internet-Verbindungen ausdehnen — ohne Port-Forwarding am Windows-Host, mit self-signed SSL und einfachem Session-Code-System.

**In dieser Phase enthalten:**
- Relay-Server (ASP.NET Core) als Docker Container auf dem QNAP NAS
- Self-signed SSL-Zertifikat (generiert vom Host via BouncyCastle)
- Session-Code-System (9-stellig, zeitbegrenzt)
- Host verbindet sich ausgehend mit Relay (kein offener Port am Host nötig)
- Viewer-Anfrage mit Host-Approval
- Browser-Client: Verbindungs-UI mit Session-Code-Eingabe

**Noch nicht in dieser Phase:**
- Multi-Monitor (→ Phase 3)
- Fernsteuerung / Input-Injection (→ Phase 4)
- Clipboard / Dateiübertragung (→ Phase 5)
- Installer (→ Phase 6)

**Voraussetzungen:**
- Phase 1 vollständig abgeschlossen
- QNAP NAS mit Container Station (Docker-Support)
- DNS-Name des QNAP (intern + extern erreichbar)
- Port 443 im QNAP-Netzwerk verfügbar (kein Konflikt mit QNAP Management auf 8443)

---

## 2.1 Relay-Server Architektur

Der Relay-Server ist ein **reiner Datendurchleiter** — er versteht den Inhalt der Frames nicht und speichert keine Bilddaten. Er kennt nur Session-Codes und WebSocket-Verbindungen.

**Session-Datenmodell (In-Memory, kein Datenbank-Bedarf):**

```csharp
// RemoteDesk.Relay/Session/SessionStore.cs
public class RelaySession
{
    public string Code { get; init; }              // "482619073"
    public WebSocket HostConnection { get; set; }  // die eine Host-Verbindung
    public List<ViewerEntry> Viewers { get; } = new();
    public List<ViewerEntry> PendingApproval { get; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivity { get; set; }
}

public class ViewerEntry
{
    public string Id { get; init; }          // UUID
    public WebSocket Connection { get; init; }
    public string IpAddress { get; init; }
    public string BrowserInfo { get; init; }
    public ViewerMode Mode { get; set; }     // ViewOnly / Control
}

public enum ViewerMode { ViewOnly, Control }
```

**Session-Lebenszyklus:**

```
1.  Host verbindet → POST /api/session/create → erhält Code "482619073"
2.  Host öffnet WebSocket: wss://qnap.dns.name/relay/host?code=482619073
3.  Relay speichert { Code → HostConnection }

4.  Viewer öffnet Browser: https://qnap.dns.name/
5.  Viewer gibt Code ein → WebSocket: wss://qnap.dns.name/relay/viewer?code=482619073
6.  Relay sendet an Host: { "type": "viewer_join_request", "viewerId": "...", "ip": "..." }
7.  Host-UI zeigt Popup → Nutzer klickt "Erlauben"
8.  Host sendet: { "type": "viewer_approve", "viewerId": "..." }
9.  Relay: Viewer ist nun aktiv in Session
10. Video-Frames: Host → Relay → alle aktiven Viewer (Broadcast)

11. Host trennt → alle Viewer erhalten { "type": "host_disconnected" }
    → Session wird aus Relay gelöscht
12. Inaktive Sessions: nach 24h automatisch bereinigt
```

---

## 2.2 Relay-Server Implementierung (ASP.NET Core, .NET 10)

**Projektdatei:**

```xml
<!-- RemoteDesk.Relay/RemoteDesk.Relay.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <!-- Keine externen Abhängigkeiten — nur ASP.NET Core -->
</Project>
```

**Program.cs:**

```csharp
// RemoteDesk.Relay/Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseKestrel(opts =>
{
    var certPath = builder.Configuration["Cert:Path"]
                   ?? "/app/certs/relay.pfx";
    var certPass = builder.Configuration["Cert:Password"] ?? "";

    opts.ListenAnyIP(443, listen =>
        listen.UseHttps(certPath, certPass));

    // HTTP → HTTPS Redirect (optional, für Browser-Komfort)
    opts.ListenAnyIP(80);
});

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<RelayHub>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// Statische Dateien (Browser-Client wird vom Host bereitgestellt,
// Relay liefert nur eine Weiterleitung)
app.MapGet("/", () => Results.Redirect("/connect"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// WebSocket-Endpunkte
app.Map("/relay/host",   RelayEndpoints.HandleHost);
app.Map("/relay/viewer", RelayEndpoints.HandleViewer);

app.Run();
```

**RelayEndpoints.cs:**

```csharp
// RemoteDesk.Relay/Relay/RelayEndpoints.cs
public static class RelayEndpoints
{
    public static async Task HandleHost(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

        var code = ctx.Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(code)) { ctx.Response.StatusCode = 400; return; }

        var store = ctx.RequestServices.GetRequiredService<SessionStore>();
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

        var session = store.RegisterHost(code, ws);

        // Frames vom Host empfangen und an alle Viewer weiterleiten
        await RelayHub.RunHostLoop(ws, session, store);
    }

    public static async Task HandleViewer(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

        var code = ctx.Request.Query["code"].ToString();
        var store = ctx.RequestServices.GetRequiredService<SessionStore>();

        if (!store.TryGetSession(code, out var session))
        {
            // Session nicht gefunden → Browser erhält Fehlermeldung
            ctx.Response.StatusCode = 404;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

        var viewer = new ViewerEntry {
            Id = Guid.NewGuid().ToString(),
            Connection = ws,
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
            BrowserInfo = ctx.Request.Headers.UserAgent.ToString(),
            Mode = ViewerMode.ViewOnly
        };

        // Approval-Request an Host senden
        await RelayHub.RequestViewerApproval(session, viewer);

        // Warten auf Approval (max. 60 Sekunden)
        await RelayHub.RunViewerLoop(ws, session, viewer, store);
    }
}
```

**RelayHub.cs — Frame-Broadcasting:**

```csharp
// RemoteDesk.Relay/Relay/RelayHub.cs
public static class RelayHub
{
    // Host sendet Frames → an alle aktiven Viewer weiterleiten
    public static async Task RunHostLoop(
        WebSocket hostWs, RelaySession session, SessionStore store)
    {
        var buffer = new byte[256 * 1024]; // 256 KB Puffer

        while (hostWs.State == WebSocketState.Open)
        {
            var result = await hostWs.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await store.CloseSession(session.Code);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Frame an alle Viewer gleichzeitig broadcasten
                var frame = buffer[..result.Count];
                var tasks = session.Viewers
                    .Where(v => v.Connection.State == WebSocketState.Open)
                    .Select(v => v.Connection.SendAsync(
                        frame, WebSocketMessageType.Binary,
                        true, CancellationToken.None));
                await Task.WhenAll(tasks);
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // JSON-Nachrichten vom Host (z.B. control_response, viewer_kick)
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await HandleHostMessage(json, session, store);
            }
        }
    }
}
```

---

## 2.3 Docker-Setup für QNAP Container Station

**Datei:** `RemoteDesk.Relay/Dockerfile`

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /publish .

# Nicht-Root-User für Sicherheit
RUN adduser -D -u 1001 relayuser
USER relayuser

# Verzeichnisse für Volumes
VOLUME ["/app/certs", "/app/config"]

EXPOSE 443
EXPOSE 80

HEALTHCHECK --interval=30s --timeout=10s \
  CMD wget -q --spider --no-check-certificate https://localhost/health || exit 1

ENTRYPOINT ["dotnet", "RemoteDesk.Relay.dll"]
```

**Datei:** `RemoteDesk.Relay/docker-compose.yml`

```yaml
version: "3.8"

services:
  remotedesk-relay:
    build: .
    container_name: remotedesk-relay
    restart: unless-stopped

    ports:
      - "443:443"   # HTTPS / WSS
      - "80:80"     # HTTP (Redirect zu HTTPS)

    volumes:
      # Self-signed Zertifikat (wird vom Host-Tool befüllt)
      - ./certs:/app/certs:ro

      # Konfigurationsdatei
      - ./config/relay.json:/app/config/relay.json:ro

    environment:
      - Cert__Path=/app/certs/relay.pfx
      - Cert__Password=${RELAY_CERT_PASSWORD}
      - ASPNETCORE_ENVIRONMENT=Production

    healthcheck:
      test: ["CMD", "wget", "-q", "--spider",
             "--no-check-certificate", "https://localhost/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 10s

    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"
```

**Datei:** `RemoteDesk.Relay/config/relay.json`

```json
{
  "SessionTimeoutHours": 24,
  "MaxViewersPerSession": 10,
  "ApprovalTimeoutSeconds": 60,
  "AllowedOrigins": ["*"],
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

**QNAP Container Station Setup (einmalig):**

```
1. QNAP Container Station öffnen
2. "Erstellen" → "docker-compose erstellen"
3. docker-compose.yml einfügen
4. Shared Folder anlegen: /share/Container/remotedesk/certs/
5. PFX-Datei dorthin kopieren (vom Host-Tool exportiert)
6. .env-Datei anlegen mit RELAY_CERT_PASSWORD=<passwort>
7. Container starten
8. Port-Weiterleitung im Router: Extern 443 → QNAP-IP 443
```

---

## 2.4 Self-signed SSL — Zertifikat-Management im Host

**NuGet-Paket:**
```xml
<PackageReference Include="BouncyCastle.Cryptography" Version="2.*" />
```

**CertificateManager.cs:**

```csharp
// RemoteDesk.Host/SSL/CertificateManager.cs
public class CertificateManager
{
    /// <summary>
    /// Generiert ein self-signed X.509 Zertifikat.
    /// DNS-Name des QNAP wird als CN und SAN eingetragen.
    /// </summary>
    public X509Certificate2 GenerateSelfSignedCertificate(
        string dnsName,         // z.B. "meinqnap.myqnapcloud.com"
        string[]? additionalSans = null,   // Weitere IPs/Hostnamen
        int validDays = 3650)   // 10 Jahre
    {
        var keyPairGenerator = new RsaKeyPairGenerator();
        keyPairGenerator.Init(new KeyGenerationParameters(
            new SecureRandom(), 4096));
        var keyPair = keyPairGenerator.GenerateKeyPair();

        var certGenerator = new X509V3CertificateGenerator();

        // Seriennummer
        certGenerator.SetSerialNumber(
            BigInteger.ProbablePrime(120, new SecureRandom()));

        // Gültigkeit
        certGenerator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGenerator.SetNotAfter(DateTime.UtcNow.AddDays(validDays));

        // Subject / Issuer
        var subject = new X509Name($"CN={dnsName}, O=RemoteDesk");
        certGenerator.SetSubjectDN(subject);
        certGenerator.SetIssuerDN(subject); // self-signed

        // Public Key
        certGenerator.SetPublicKey(keyPair.Public);

        // Extensions
        certGenerator.AddExtension(
            X509Extensions.BasicConstraints, true,
            new BasicConstraints(false));

        certGenerator.AddExtension(
            X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));

        certGenerator.AddExtension(
            X509Extensions.ExtendedKeyUsage, false,
            new ExtendedKeyUsage(KeyPurposeID.IdKPServerAuth));

        // Subject Alternative Names (SAN) — Browser prüfen diese!
        var sanList = new List<GeneralName>
        {
            new(GeneralName.DnsName, dnsName)
        };
        if (additionalSans != null)
            foreach (var san in additionalSans)
                sanList.Add(IPAddress.TryParse(san, out _)
                    ? new GeneralName(GeneralName.IPAddress, san)
                    : new GeneralName(GeneralName.DnsName, san));

        certGenerator.AddExtension(
            X509Extensions.SubjectAlternativeName, false,
            new DerSequence(sanList.ToArray()));

        // Signieren mit SHA256
        var signatureFactory = new Asn1SignatureFactory(
            "SHA256WITHRSA", keyPair.Private);
        var cert = certGenerator.Generate(signatureFactory);

        // BouncyCastle → .NET X509Certificate2
        return ConvertToX509Certificate2(cert, keyPair.Private);
    }

    /// <summary>Exportiert als PFX für Docker-Volume.</summary>
    public void ExportToPfx(X509Certificate2 cert,
        string path, string password)
    {
        var pfxBytes = cert.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(path, pfxBytes);
    }

    /// <summary>Exportiert Public Key als CER für Browser-Import.</summary>
    public void ExportPublicKeyCer(X509Certificate2 cert, string path)
    {
        var cerBytes = cert.Export(X509ContentType.Cert);
        File.WriteAllBytes(path, cerBytes);
    }

    /// <summary>SHA256-Fingerprint für manuelle Browser-Verifikation.</summary>
    public string GetFingerprint(X509Certificate2 cert)
    {
        var hash = cert.GetCertHashString(HashAlgorithmName.SHA256);
        return string.Join(":", Enumerable.Range(0, hash.Length / 2)
            .Select(i => hash.Substring(i * 2, 2)));
    }
}
```

**Settings-UI — SSL-Tab (WPF):**

```
┌────────────────────────────────────────────────────┐
│ Tab: Verbindung / SSL                              │
├────────────────────────────────────────────────────┤
│ Relay-Server DNS-Name:                             │
│ [meinqnap.myqnapcloud.com              ]           │
│                                                    │
│ Relay-Port: [443]                                  │
│                                                    │
│ SSL-Zertifikat:                                    │
│ ● Self-signed (empfohlen)                          │
│ ○ Eigene PFX-Datei [Durchsuchen...]                │
│                                                    │
│ Weitere SANs (optional, eine pro Zeile):           │
│ [192.168.1.50                          ]           │
│ [remotedesk.local                      ]           │
│                                                    │
│ [Zertifikat generieren & exportieren]              │
│                                                    │
│ Status: Zertifikat gültig bis 15.04.2036           │
│ Fingerprint: AB:CD:EF:12:34:...    [Kopieren]      │
│                                                    │
│ [CER exportieren (für Browser-Import)]             │
│                                                    │
│ Zertifikat-Pfad (für Docker-Volume):               │
│ C:\ProgramData\RemoteDesk\certs\relay.pfx          │
│ [Im Explorer öffnen]                               │
└────────────────────────────────────────────────────┘
```

---

## 2.5 Host verbindet sich mit Relay

**RelayConnection.cs:**

```csharp
// RemoteDesk.Host/Session/RelayConnection.cs
public class RelayConnection : IDisposable
{
    private ClientWebSocket? _ws;
    private readonly ISettings _settings;

    public string? CurrentSessionCode { get; private set; }
    public event EventHandler<ViewerJoinRequest>? ViewerJoinRequested;
    public event EventHandler? RelayDisconnected;

    public async Task ConnectAndRegisterAsync()
    {
        _ws = new ClientWebSocket();

        // Self-signed SSL: Thumbprint-basierte Validierung
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback =
            (msg, cert, chain, errors) =>
            {
                if (cert == null) return false;
                if (_settings.RelayThumbprint == null) return true; // erstes Verbinden
                return cert.GetCertHashString(HashAlgorithmName.SHA256)
                    .Equals(_settings.RelayThumbprint,
                            StringComparison.OrdinalIgnoreCase);
            };

        // Session-Code anfordern
        using var http = new HttpClient(handler);
        var response = await http.PostAsync(
            $"https://{_settings.RelayDns}/api/session/create",
            JsonContent.Create(new { hostVersion = "1.0" }));
        var json = await response.Content.ReadFromJsonAsync<CreateSessionResponse>();
        CurrentSessionCode = json!.Code;

        // WebSocket-Verbindung zum Relay
        var uri = new Uri(
            $"wss://{_settings.RelayDns}/relay/host?code={CurrentSessionCode}");
        await _ws.ConnectAsync(uri, CancellationToken.None);

        // Hintergrund-Loop: Nachrichten vom Relay empfangen
        _ = Task.Run(ReceiveLoop);
    }

    // Sendet Video-Frame an alle Viewer (via Relay)
    public async Task BroadcastFrameAsync(ReadOnlyMemory<byte> frameData)
    {
        if (_ws?.State == WebSocketState.Open)
            await _ws.SendAsync(frameData,
                WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[4096];
        while (_ws?.State == WebSocketState.Open)
        {
            var result = await _ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var msg = JsonSerializer.Deserialize<RelayMessage>(
                    buffer[..result.Count]);
                HandleRelayMessage(msg!);
            }
        }
        RelayDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private void HandleRelayMessage(RelayMessage msg)
    {
        if (msg.Type == "viewer_join_request")
            ViewerJoinRequested?.Invoke(this,
                new ViewerJoinRequest(msg.ViewerId!, msg.Ip!, msg.BrowserInfo!));
    }

    public async Task ApproveViewerAsync(string viewerId)
        => await SendJsonAsync(new { type = "viewer_approve", viewerId });

    public async Task DenyViewerAsync(string viewerId)
        => await SendJsonAsync(new { type = "viewer_deny", viewerId });
}
```

**Auto-Reconnect bei Verbindungsverlust:**

```csharp
// SessionManager: überwacht Relay-Verbindung
private async Task EnsureRelayConnectionAsync()
{
    int retries = 0;
    while (true)
    {
        try
        {
            await _relay.ConnectAndRegisterAsync();
            retries = 0; // Reset bei Erfolg
            UpdateTrayStatus("● Verbunden");
        }
        catch (Exception ex)
        {
            retries++;
            var delay = Math.Min(30, retries * 5); // Max 30s Wartezeit
            UpdateTrayStatus($"⟳ Reconnect in {delay}s...");
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }
    }
}
```

---

## 2.6 Session-Code UI

**Host — Tray-Kontext-Menü (Phase 2 Erweiterung):**

```
[RemoteDesk Icon]
  ─────────────────────────────────
  ● Verbunden mit Relay
  Session-Code: 482 619 073        ← fett, anklickbar (kopiert Code)
  Viewer: 0 verbunden
  ─────────────────────────────────
  Viewer-Anfragen: 1 ausstehend ●  ← rot wenn Anfrage wartet
  ─────────────────────────────────
  Einstellungen
  Beenden
```

**Host — Approval-Dialog (WPF):**

```
┌────────────────────────────────────────────┐
│  🖥 Viewer möchte beitreten                │
├────────────────────────────────────────────┤
│  IP-Adresse:   203.0.113.42               │
│  Browser:      Chrome 124 / Windows       │
│  Uhrzeit:      15:42:07                   │
│                                            │
│  Modus beim Beitritt:                      │
│  ● Nur anschauen (View Only)               │
│  ○ Fernsteuerung erlauben                  │
│                                            │
│        [ Erlauben ]  [ Ablehnen ]          │
└────────────────────────────────────────────┘
```

**Browser — Connect-Seite:**

```html
<!-- Einfache Connect-Seite, ausgeliefert vom Relay-Server -->
┌───────────────────────────────────────────┐
│         RemoteDesk                        │
│                                           │
│  Session-Code eingeben:                   │
│                                           │
│  [___] [___] [___]                        │
│   482   619   073                         │
│                                           │
│  Relay-Server: meinqnap.myqnapcloud.com   │
│                                           │
│         [ Verbinden ]                     │
│                                           │
│  ──────── oder ────────                   │
│  Direktverbindung (LAN):                  │
│  [192.168.1.10:8443      ] [ Direkt ]     │
└───────────────────────────────────────────┘
```

---

## 2.7 Browser-Client Erweiterung (Phase 2)

Erweiterung von `viewer.js` um Relay-Unterstützung:

```javascript
class RemoteDeskViewer {
    // Phase 2: Verbindung via Relay oder direkt
    async connect() {
        const code = this._getSessionCode();
        const directHost = document.getElementById('direct-input').value;

        let wsUrl;
        if (code) {
            // Relay-Verbindung: Code an Relay senden
            const relayHost = document.getElementById('relay-host').value
                              || location.host;
            wsUrl = `wss://${relayHost}/relay/viewer?code=${code}`;
        } else if (directHost) {
            // LAN-Direktverbindung (Phase 1)
            wsUrl = `wss://${directHost}/stream`;
        } else {
            alert('Bitte Session-Code oder Direkt-Adresse eingeben.');
            return;
        }

        this.ws = new WebSocket(wsUrl);
        // ... rest wie Phase 1
    }

    _handleJson(msg) {
        switch (msg.type) {
            case 'config':     this._applyConfig(msg); break;
            case 'waiting_approval':
                this._showStatus('⏳ Warte auf Host-Bestätigung...');
                break;
            case 'approved':
                this._showStatus('✓ Verbunden');
                this._initCodec(msg);
                break;
            case 'denied':
                this._showStatus('✗ Verbindung abgelehnt');
                this.ws.close();
                break;
            case 'host_disconnected':
                this._showStatus('● Host hat die Verbindung getrennt');
                break;
        }
    }

    _getSessionCode() {
        const parts = ['code1', 'code2', 'code3']
            .map(id => document.getElementById(id).value.trim());
        return parts.every(p => p.length === 3) ? parts.join('') : '';
    }
}
```

---

## 2.8 Deliverables & Akzeptanzkriterien

### Checkliste Phase 2

- [ ] Relay-Server kompiliert und läuft lokal (dotnet run)
- [ ] Docker-Image wird gebaut (`docker build`)
- [ ] docker-compose startet Container korrekt auf QNAP
- [ ] `/health`-Endpoint antwortet mit `200 OK`
- [ ] Self-signed Zertifikat wird vom Host generiert (BouncyCastle)
- [ ] PFX-Export für Docker-Volume funktioniert
- [ ] CER-Export für Browser-Import funktioniert
- [ ] Fingerprint wird korrekt angezeigt und kann kopiert werden
- [ ] Host verbindet sich ausgehend mit Relay (Port 443)
- [ ] Session-Code (9-stellig) wird generiert und im Tray angezeigt
- [ ] Browser kann sich via Session-Code mit Relay verbinden
- [ ] Approval-Popup erscheint im Host-WPF-Fenster
- [ ] Host kann Verbindung erlauben oder ablehnen
- [ ] Stream läuft über Relay (nicht mehr nur direkt)
- [ ] LAN-Direktverbindung funktioniert weiterhin parallel
- [ ] Auto-Reconnect bei Relay-Verbindungsverlust (max. 5 Versuche)
- [ ] Verbindung von außen (Internet) funktioniert via DNS-Name

### Testszenarien

| Szenario | Erwartetes Ergebnis |
|---|---|
| Browser im LAN verbindet via Code | Stream funktioniert |
| Browser von außen verbindet via Code | Stream funktioniert über Internet |
| Browser gibt falschen Code ein | `404`-Fehlermeldung im Browser |
| Host lehnt Viewer ab | Browser zeigt "Verbindung abgelehnt" |
| Host trennt Relay | Browser zeigt "Host getrennt" |
| Relay-Server neu starten | Host reconnectet automatisch |
| Browser-Zertifikatswarnung | Kann akzeptiert werden, Stream läuft dann |

---

## QNAP-spezifische Hinweise

**Port-Konflikt prüfen:**
QNAP-Management läuft auf Port 8080 (HTTP) und 8443 (HTTPS). Port 443 ist normalerweise frei, sollte aber vor dem Deployment geprüft werden:

```bash
# Im QNAP SSH-Terminal:
netstat -tlnp | grep 443
```

Falls Port 443 belegt: Im docker-compose auf `8443:443` ändern und Router entsprechend konfigurieren.

**Dateipfade auf QNAP (Container Station):**
```
/share/Container/remotedesk/
├── docker-compose.yml
├── .env                      ← RELAY_CERT_PASSWORD=...
├── certs/
│   └── relay.pfx             ← vom Host-Tool hierher kopieren
└── config/
    └── relay.json
```

**Zertifikat auf QNAP deployen (zwei Möglichkeiten):**

Option A — Manuell via File Station:
- Host exportiert PFX nach `C:\ProgramData\RemoteDesk\certs\relay.pfx`
- QNAP File Station: Datei hochladen nach `/share/Container/remotedesk/certs/`

Option B — Automatisch via SCP (Host-Tool):
```csharp
// Im Settings-UI: "Auf QNAP deployen" Button
// Nutzt SSH.NET NuGet-Paket für SCP-Upload
var client = new ScpClient(qnapHost, qnapUser, qnapPassword);
client.Connect();
client.Upload(new FileInfo(localPfxPath), remoteCertsPath);
```

---

## Übergang zu Phase 3

Am Ende von Phase 2 funktioniert die Verbindung **über LAN und Internet** via Session-Code und Relay auf dem QNAP NAS. Phase 3 ergänzt:

- Auswahl zwischen mehreren Monitoren (DXGI Multi-Output)
- Nahtloser Monitor-Wechsel während laufender Session
- Adaptive FPS-Steuerung mit Backpressure
- Optionale Auflösungs-Skalierung

---

*RemoteDesk · Phase 2 von 6 · Stand: April 2026*
