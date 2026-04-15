class RemoteDeskViewer {
    constructor() {
        this.canvas = document.getElementById('remote-canvas');
        this.ctx = this.canvas.getContext('2d');
        this.ws = null;
        this.codec = null;
        this._sessionStart = 0;
        this._frameTimestamps = [];
    }

    async connect() {
        const host = document.getElementById('host-input').value || location.host;
        const url = 'wss://' + host + '/stream';

        this._updateStatus('conn', 'Verbinde...', '');

        try {
            this.ws = new WebSocket(url);
            this.ws.binaryType = 'arraybuffer';

            this.ws.onopen = () => this._onOpen();
            this.ws.onmessage = (e) => this._onMessage(e);
            this.ws.onclose = () => this._onClose();
            this.ws.onerror = () => this._updateStatus('conn', 'Fehler', 'err');
        } catch (e) {
            this._updateStatus('conn', 'Fehler: ' + e.message, 'err');
        }
    }

    _onOpen() {
        this._sessionStart = Date.now();
        this._updateStatus('conn', 'Verbunden', 'ok');

        document.getElementById('connect-btn').style.display = 'none';
        document.getElementById('disconnect-btn').style.display = '';

        // Report capabilities
        const caps = ['jpeg'];
        if (typeof VideoDecoder !== 'undefined') {
            caps.unshift('vp8');
            caps.unshift('av1');
        }

        this.ws.send(JSON.stringify({
            type: 'hello',
            capabilities: caps,
            version: '1.0'
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
            this.canvas.width = msg.width;
            this.canvas.height = msg.height;
            this._updateStatus('codec', 'Codec: ' + msg.codec.toUpperCase());

            // Dispose previous codec if any
            if (this.codec) {
                this.codec.dispose();
            }

            if (msg.codec === 'vp8') {
                this.codec = new VP8Decoder(this.canvas, () => this._countFrame());
            } else if (msg.codec === 'av1') {
                this.codec = new AV1Decoder(this.canvas, () => this._countFrame());
            } else {
                this.codec = new JpegDecoder(this.canvas, () => this._countFrame());
            }
            this.codec.initialize(msg.width, msg.height);
        }
    }

    _handleFrame(buffer) {
        if (buffer.byteLength < 9) return;

        const view = new DataView(buffer);
        const frameType = view.getUint8(0);
        const timestamp = view.getUint32(1, false);
        const length = view.getUint32(5, false);

        if (buffer.byteLength < 9 + length) return;

        const payload = buffer.slice(9, 9 + length);

        this._totalFrames = (this._totalFrames || 0) + 1;
        if (this._totalFrames <= 3) {
            console.log(`[Frame #${this._totalFrames}] type=0x${frameType.toString(16)} size=${length} bytes`);
        }

        // Latency estimate
        const elapsed = Date.now() - this._sessionStart;
        const latency = Math.abs(elapsed - timestamp);
        this._updateStatus('latency', 'Latenz: ' + latency + 'ms');

        // 0x03 = keyframe, or detect VP8 keyframe from bitstream
        const isKeyframe = frameType === 0x03;
        this.codec?.decodeFrame(payload, isKeyframe);
    }

    _countFrame() {
        const now = performance.now();
        this._frameTimestamps.push(now);

        // Rolling window: last 2 seconds
        this._frameTimestamps = this._frameTimestamps.filter(
            t => now - t < 2000
        );
        const fps = Math.round(this._frameTimestamps.length / 2);
        this._updateStatus('fps', 'FPS: ' + fps);
    }

    _updateStatus(id, text, cls) {
        const el = document.getElementById('status-' + id);
        if (!el) return;
        el.textContent = text;
        if (cls !== undefined) {
            el.className = cls ? ('status-' + cls) : '';
        }
    }

    _onClose() {
        this._updateStatus('conn', 'Getrennt', '');
        this._updateStatus('fps', 'FPS: –');
        this._updateStatus('latency', 'Latenz: –');

        document.getElementById('connect-btn').style.display = '';
        document.getElementById('disconnect-btn').style.display = 'none';

        if (this.codec) {
            this.codec.dispose();
            this.codec = null;
        }
    }

    disconnect() {
        if (this.ws) {
            this.ws.close();
            this.ws = null;
        }
    }
}

function toggleFullscreen() {
    if (!document.fullscreenElement) {
        document.getElementById('canvas-wrap').requestFullscreen();
    } else {
        document.exitFullscreen();
    }
}

const viewer = new RemoteDeskViewer();
