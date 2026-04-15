// VP8 decoder via WebCodecs API
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
            error: (e) => console.error('VP8 Decoder error:', e)
        });

        this.decoder.configure({
            codec: 'vp8',
            codedWidth: width,
            codedHeight: height,
            optimizeForLatency: true
        });
    }

    decodeFrame(payload, isKeyframe) {
        if (this.decoder?.state !== 'configured') return;

        const chunk = new EncodedVideoChunk({
            type: isKeyframe ? 'key' : 'delta',
            timestamp: performance.now() * 1000,
            data: payload
        });
        this.decoder.decode(chunk);
    }

    dispose() {
        if (this.decoder?.state === 'configured') {
            this.decoder.close();
        }
        this.decoder = null;
    }
}

// AV1 decoder via WebCodecs API
class AV1Decoder {
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
            error: (e) => console.error('AV1 Decoder error:', e)
        });

        this.decoder.configure({
            codec: 'av01.0.08M.08',
            codedWidth: width,
            codedHeight: height,
            optimizeForLatency: true
        });
    }

    decodeFrame(payload, isKeyframe) {
        if (this.decoder?.state !== 'configured') return;

        const chunk = new EncodedVideoChunk({
            type: isKeyframe ? 'key' : 'delta',
            timestamp: performance.now() * 1000,
            data: payload
        });
        this.decoder.decode(chunk);
    }

    dispose() {
        if (this.decoder?.state === 'configured') {
            this.decoder.close();
        }
        this.decoder = null;
    }
}

// JPEG fallback decoder
class JpegDecoder {
    constructor(canvas, onFrameDecoded) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.onFrameDecoded = onFrameDecoded;
    }

    initialize(width, height) {
        // Canvas dimensions are set by the caller
    }

    decodeFrame(payload, _isKeyframe) {
        const blob = new Blob([payload], { type: 'image/jpeg' });
        const url = URL.createObjectURL(blob);
        const img = new Image();
        img.onload = () => {
            this.ctx.drawImage(img, 0, 0);
            URL.revokeObjectURL(url);
            this.onFrameDecoded();
        };
        img.onerror = () => {
            URL.revokeObjectURL(url);
        };
        img.src = url;
    }

    dispose() {
        // Nothing to clean up
    }
}
