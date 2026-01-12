// Query Tracer - Live Database Activity Visualization
class QueryTracer {
    constructor(canvasId) {
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) return;

        this.ctx = this.canvas.getContext('2d');
        this.pulses = [];
        this.maxPulses = 50;
        this.animationId = null;

        this.resize();
        window.addEventListener('resize', () => this.resize());
    }

    resize() {
        const rect = this.canvas.parentElement.getBoundingClientRect();
        this.canvas.width = rect.width;
        this.canvas.height = rect.height || 200;
    }

    // Color based on duration
    getColor(durationMs) {
        if (durationMs < 20) return { r: 34, g: 197, b: 94, a: 1 };      // Green
        if (durationMs < 100) return { r: 245, g: 158, b: 11, a: 1 };    // Amber
        return { r: 239, g: 68, b: 68, a: 1 };                            // Red
    }

    addQuery(query) {
        const color = this.getColor(query.durationMs);

        // Random position on canvas
        const x = Math.random() * this.canvas.width;
        const y = Math.random() * this.canvas.height;

        // Size based on rows returned (capped)
        const baseSize = Math.min(20 + (query.rows || 0) * 0.5, 60);

        this.pulses.push({
            x,
            y,
            radius: 5,
            maxRadius: baseSize,
            color,
            opacity: 1,
            query: query.operation,
            duration: query.durationMs,
            startTime: Date.now(),
            lifetime: 5000 + Math.random() * 3000 // 5-8 seconds
        });

        // Limit pulses
        if (this.pulses.length > this.maxPulses) {
            this.pulses.shift();
        }
    }

    animate() {
        this.ctx.fillStyle = 'rgba(10, 10, 15, 0.15)';
        this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);

        const now = Date.now();

        this.pulses = this.pulses.filter(pulse => {
            const age = now - pulse.startTime;
            const progress = age / pulse.lifetime;

            if (progress >= 1) return false;

            // Expand radius
            pulse.radius = 5 + (pulse.maxRadius - 5) * Math.min(progress * 2, 1);

            // Fade out in second half
            pulse.opacity = progress > 0.5 ? 1 - ((progress - 0.5) * 2) : 1;

            // Draw ripple
            const { r, g, b } = pulse.color;

            // Outer glow
            this.ctx.beginPath();
            this.ctx.arc(pulse.x, pulse.y, pulse.radius * 1.5, 0, Math.PI * 2);
            this.ctx.fillStyle = `rgba(${r}, ${g}, ${b}, ${pulse.opacity * 0.1})`;
            this.ctx.fill();

            // Main circle
            this.ctx.beginPath();
            this.ctx.arc(pulse.x, pulse.y, pulse.radius, 0, Math.PI * 2);
            this.ctx.strokeStyle = `rgba(${r}, ${g}, ${b}, ${pulse.opacity * 0.8})`;
            this.ctx.lineWidth = 2;
            this.ctx.stroke();

            // Center dot
            this.ctx.beginPath();
            this.ctx.arc(pulse.x, pulse.y, 3, 0, Math.PI * 2);
            this.ctx.fillStyle = `rgba(${r}, ${g}, ${b}, ${pulse.opacity})`;
            this.ctx.fill();

            // Label (only for larger, recent pulses)
            if (pulse.opacity > 0.5 && pulse.radius > 15) {
                this.ctx.font = '10px JetBrains Mono, monospace';
                this.ctx.fillStyle = `rgba(255, 255, 255, ${pulse.opacity * 0.7})`;
                this.ctx.textAlign = 'center';
                this.ctx.fillText(`${pulse.duration}ms`, pulse.x, pulse.y + pulse.radius + 12);
            }

            return true;
        });

        this.animationId = requestAnimationFrame(() => this.animate());
    }

    start() {
        if (!this.canvas) return;
        this.animate();
    }

    stop() {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
            this.animationId = null;
        }
    }

    clear() {
        this.pulses = [];
        if (this.ctx) {
            this.ctx.fillStyle = '#0a0a0f';
            this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);
        }
    }
}

// Export for use
window.QueryTracer = QueryTracer;
