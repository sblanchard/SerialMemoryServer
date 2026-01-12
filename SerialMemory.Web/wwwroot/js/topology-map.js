// Topology Map - System Architecture Visualization
class TopologyMap {
    constructor(canvasId) {
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) return;

        this.ctx = this.canvas.getContext('2d');
        this.animationId = null;
        this.frame = 0;

        // Default topology
        this.nodes = [
            { id: 'api', type: 'API', label: 'API Server', x: 0.5, y: 0.25, status: 'ok', load: 0 },
            { id: 'db', type: 'DB', label: 'PostgreSQL', x: 0.25, y: 0.75, status: 'ok', load: 0 },
            { id: 'signalr', type: 'SignalR', label: 'SignalR Hub', x: 0.75, y: 0.75, status: 'ok', load: 0 },
            { id: 'worker', type: 'Worker', label: 'Background Worker', x: 0.5, y: 0.85, status: 'ok', load: 0 }
        ];

        this.edges = [
            { from: 'api', to: 'db', latency: 5, active: true },
            { from: 'api', to: 'signalr', latency: 2, active: true },
            { from: 'worker', to: 'db', latency: 8, active: true }
        ];

        this.resize();
        window.addEventListener('resize', () => this.resize());
    }

    resize() {
        const rect = this.canvas.parentElement.getBoundingClientRect();
        this.canvas.width = rect.width;
        this.canvas.height = rect.height || 250;
    }

    getStatusColor(status) {
        switch (status) {
            case 'ok': return '#22c55e';
            case 'warning': return '#f59e0b';
            case 'error': return '#ef4444';
            default: return '#6b7280';
        }
    }

    getNodePosition(node) {
        return {
            x: node.x * this.canvas.width,
            y: node.y * this.canvas.height
        };
    }

    drawEdge(edge) {
        const fromNode = this.nodes.find(n => n.id === edge.from);
        const toNode = this.nodes.find(n => n.id === edge.to);
        if (!fromNode || !toNode) return;

        const from = this.getNodePosition(fromNode);
        const to = this.getNodePosition(toNode);

        // Line thickness based on latency (thinner = faster)
        const thickness = Math.max(1, Math.min(4, edge.latency / 5));

        // Base line
        this.ctx.beginPath();
        this.ctx.moveTo(from.x, from.y);
        this.ctx.lineTo(to.x, to.y);
        this.ctx.strokeStyle = 'rgba(0, 212, 255, 0.2)';
        this.ctx.lineWidth = thickness;
        this.ctx.stroke();

        // Animated flow
        if (edge.active) {
            const progress = (this.frame * 2) % 100 / 100;
            const flowX = from.x + (to.x - from.x) * progress;
            const flowY = from.y + (to.y - from.y) * progress;

            // Flow dot
            this.ctx.beginPath();
            this.ctx.arc(flowX, flowY, 4, 0, Math.PI * 2);
            this.ctx.fillStyle = '#00d4ff';
            this.ctx.fill();

            // Glow
            this.ctx.beginPath();
            this.ctx.arc(flowX, flowY, 8, 0, Math.PI * 2);
            this.ctx.fillStyle = 'rgba(0, 212, 255, 0.3)';
            this.ctx.fill();
        }

        // Latency label
        const midX = (from.x + to.x) / 2;
        const midY = (from.y + to.y) / 2;
        this.ctx.font = '9px JetBrains Mono, monospace';
        this.ctx.fillStyle = 'rgba(255, 255, 255, 0.5)';
        this.ctx.textAlign = 'center';
        this.ctx.fillText(`${edge.latency}ms`, midX, midY - 5);
    }

    drawNode(node) {
        const pos = this.getNodePosition(node);
        const color = this.getStatusColor(node.status);
        const radius = 25;

        // Outer glow
        const gradient = this.ctx.createRadialGradient(pos.x, pos.y, radius * 0.5, pos.x, pos.y, radius * 2);
        gradient.addColorStop(0, `${color}40`);
        gradient.addColorStop(1, 'transparent');
        this.ctx.fillStyle = gradient;
        this.ctx.fillRect(pos.x - radius * 2, pos.y - radius * 2, radius * 4, radius * 4);

        // Main circle
        this.ctx.beginPath();
        this.ctx.arc(pos.x, pos.y, radius, 0, Math.PI * 2);
        this.ctx.fillStyle = 'rgba(13, 17, 23, 0.9)';
        this.ctx.fill();
        this.ctx.strokeStyle = color;
        this.ctx.lineWidth = 2;
        this.ctx.stroke();

        // Status indicator
        this.ctx.beginPath();
        this.ctx.arc(pos.x + radius * 0.6, pos.y - radius * 0.6, 5, 0, Math.PI * 2);
        this.ctx.fillStyle = color;
        this.ctx.fill();

        // Pulsing effect for active status
        if (node.status === 'ok') {
            const pulseSize = 5 + Math.sin(this.frame * 0.1) * 2;
            this.ctx.beginPath();
            this.ctx.arc(pos.x + radius * 0.6, pos.y - radius * 0.6, pulseSize, 0, Math.PI * 2);
            this.ctx.strokeStyle = `${color}80`;
            this.ctx.lineWidth = 1;
            this.ctx.stroke();
        }

        // Icon based on type
        this.ctx.font = 'bold 14px Inter, sans-serif';
        this.ctx.fillStyle = color;
        this.ctx.textAlign = 'center';
        this.ctx.textBaseline = 'middle';

        let icon = '';
        switch (node.type) {
            case 'API': icon = 'API'; break;
            case 'DB': icon = 'DB'; break;
            case 'SignalR': icon = 'SR'; break;
            case 'Worker': icon = 'WK'; break;
            default: icon = '?';
        }
        this.ctx.fillText(icon, pos.x, pos.y);

        // Label
        this.ctx.font = '10px JetBrains Mono, monospace';
        this.ctx.fillStyle = '#e5e7eb';
        this.ctx.textAlign = 'center';
        this.ctx.fillText(node.label, pos.x, pos.y + radius + 15);

        // Load indicator
        if (node.load > 0) {
            this.ctx.font = '9px JetBrains Mono, monospace';
            this.ctx.fillStyle = node.load > 80 ? '#ef4444' : node.load > 50 ? '#f59e0b' : '#22c55e';
            this.ctx.fillText(`${node.load}%`, pos.x, pos.y + radius + 27);
        }
    }

    animate() {
        // Clear with fade effect
        this.ctx.fillStyle = 'rgba(10, 10, 15, 0.3)';
        this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);

        // Draw edges first (below nodes)
        this.edges.forEach(edge => this.drawEdge(edge));

        // Draw nodes
        this.nodes.forEach(node => this.drawNode(node));

        this.frame++;
        this.animationId = requestAnimationFrame(() => this.animate());
    }

    update(data) {
        if (data.nodes) {
            data.nodes.forEach(nodeData => {
                const existing = this.nodes.find(n => n.id === nodeData.id);
                if (existing) {
                    existing.status = nodeData.status || existing.status;
                    existing.load = nodeData.load || existing.load;
                }
            });
        }

        if (data.edges) {
            data.edges.forEach(edgeData => {
                const existing = this.edges.find(e =>
                    e.from === edgeData.from && e.to === edgeData.to
                );
                if (existing) {
                    existing.latency = edgeData.latencyMs || existing.latency;
                    existing.active = edgeData.active !== false;
                }
            });
        }
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
}

// Export for use
window.TopologyMap = TopologyMap;
