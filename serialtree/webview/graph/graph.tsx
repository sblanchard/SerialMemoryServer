import { useState, useCallback, useRef, useMemo, useEffect } from 'react';
import { createRoot } from 'react-dom/client';
import ForceGraph3DLib from 'react-force-graph-3d';
import * as THREE from 'three';
import { EffectComposer } from 'three/examples/jsm/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/examples/jsm/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/examples/jsm/postprocessing/UnrealBloomPass.js';

// VS Code webview API
declare function acquireVsCodeApi(): {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
};

const vscode = acquireVsCodeApi();

// --- Types ---

interface GraphNode {
  id: string;
  label: string;
  group: string;
  title?: string;
  x?: number;
  y?: number;
  z?: number;
  color?: string;
}

interface GraphLink {
  source: string | GraphNode;
  target: string | GraphNode;
  label?: string;
  color?: string;
  value?: number;
}

// --- Constants ---

const BACKGROUND_COLOR = '#0B0F1A';

const ENTITY_COLORS: Record<string, string> = {
  PERSON: '#3B82F6',
  ORG: '#9333EA',
  GPE: '#22C55E',
  DATE: '#f59e0b',
  TIME: '#f59e0b',
  EMAIL: '#22D3EE',
  URL: '#ec4899',
  TITLE: '#6366f1',
  MONEY: '#22D3EE',
  PERCENT: '#22D3EE',
  default: '#6B7280',
};

const LINK_TYPE_COLORS: Record<string, string> = {
  direct: 'rgba(59,130,246,0.35)',
  weak: 'rgba(107,114,128,0.25)',
  default: 'rgba(34,211,238,0.35)',
};

const ENTITY_TYPES = Object.keys(ENTITY_COLORS).filter(k => k !== 'default');

// --- Graph Controls ---

function GraphControls({ onSearch, onFilterChange, onReset }: {
  onSearch: (query: string) => void;
  onFilterChange: (types: string[]) => void;
  onReset: () => void;
}) {
  const [searchQuery, setSearchQuery] = useState('');
  const [activeFilters, setActiveFilters] = useState<Set<string>>(new Set(ENTITY_TYPES));
  const [showFilters, setShowFilters] = useState(false);

  const handleSearch = () => onSearch(searchQuery);
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') { handleSearch(); }
  };

  const toggleFilter = (type: string) => {
    const next = new Set(activeFilters);
    if (next.has(type)) { next.delete(type); } else { next.add(type); }
    setActiveFilters(next);
    onFilterChange(Array.from(next));
  };

  return (
    <div style={{ position: 'absolute', top: 12, left: 12, zIndex: 10, display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{ display: 'flex', gap: 6 }}>
        <input
          type="text"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Search graph..."
          style={{
            width: 180, padding: '6px 10px', fontSize: 12,
            background: 'rgba(11,15,26,0.9)', color: '#eaeaea',
            border: '1px solid rgba(34,211,238,0.3)', borderRadius: 6, outline: 'none',
          }}
        />
        <button onClick={handleSearch} style={btnStyle}>Search</button>
      </div>
      <div style={{ display: 'flex', gap: 6 }}>
        <button onClick={() => setShowFilters(!showFilters)} style={btnStyle}>
          {showFilters ? 'Hide' : 'Filters'}
        </button>
        <button onClick={onReset} style={btnStyle}>Reset</button>
      </div>
      {showFilters && (
        <div style={{
          background: 'rgba(11,15,26,0.95)', padding: 12, borderRadius: 8,
          border: '1px solid rgba(34,211,238,0.2)', maxWidth: 260,
        }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 8 }}>
            <span style={{ fontSize: 11, fontWeight: 600, color: '#eaeaea' }}>Entity Types</span>
            <div style={{ display: 'flex', gap: 8 }}>
              <button onClick={() => { setActiveFilters(new Set(ENTITY_TYPES)); onFilterChange(ENTITY_TYPES); }} style={linkBtnStyle}>All</button>
              <button onClick={() => { setActiveFilters(new Set()); onFilterChange([]); }} style={linkBtnStyle}>None</button>
            </div>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 4 }}>
            {ENTITY_TYPES.map(type => (
              <label key={type} style={{ display: 'flex', alignItems: 'center', gap: 6, cursor: 'pointer', fontSize: 11, padding: 4, color: activeFilters.has(type) ? '#eaeaea' : '#6B7280' }}>
                <div style={{
                  width: 10, height: 10, borderRadius: '50%',
                  background: activeFilters.has(type) ? ENTITY_COLORS[type] : 'transparent',
                  border: `2px solid ${ENTITY_COLORS[type]}`,
                  boxShadow: activeFilters.has(type) ? `0 0 6px ${ENTITY_COLORS[type]}60` : 'none',
                }} />
                <input type="checkbox" checked={activeFilters.has(type)} onChange={() => toggleFilter(type)} style={{ display: 'none' }} />
                <span style={{ textTransform: 'capitalize' }}>{type}</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

const btnStyle: React.CSSProperties = {
  padding: '6px 12px', fontSize: 11, cursor: 'pointer',
  background: 'rgba(34,211,238,0.15)', color: '#22D3EE',
  border: '1px solid rgba(34,211,238,0.3)', borderRadius: 6,
};

const linkBtnStyle: React.CSSProperties = {
  background: 'none', border: 'none', cursor: 'pointer', fontSize: 10,
  color: '#22D3EE', padding: 0,
};

// --- Force Graph ---

function ForceGraph3D({ nodes, links, onNodeClick }: {
  nodes: GraphNode[];
  links: GraphLink[];
  onNodeClick?: (node: GraphNode) => void;
}) {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const fgRef = useRef<any>(null);
  const hoveredNodeRef = useRef<GraphNode | null>(null);

  useEffect(() => {
    const fg = fgRef.current;
    if (!fg) { return; }

    const scene = fg.scene();
    if (scene) { scene.fog = new THREE.Fog(BACKGROUND_COLOR, 50, 600); }

    const renderer = fg.renderer();
    const camera = fg.camera();

    if (renderer && scene && camera) {
      const composer = new EffectComposer(renderer);
      composer.addPass(new RenderPass(scene, camera));
      composer.addPass(new UnrealBloomPass(
        new THREE.Vector2(window.innerWidth, window.innerHeight),
        1.2, 0.6, 0.2,
      ));
      fg.postProcessingComposer(composer);
    }

    if (camera) {
      camera.position.set(0, 0, 280);
      camera.near = 1;
      camera.far = 2000;
      camera.updateProjectionMatrix();
    }

    let animationId: number;
    const animate = () => {
      animationId = requestAnimationFrame(animate);
      if (scene) { scene.rotation.y += 0.00025; }
    };
    animate();

    return () => { if (animationId) { cancelAnimationFrame(animationId); } };
  }, []);

  const clusteredNodes = useMemo(() => {
    const typeGroups = new Map<string, number>();
    let groupIndex = 0;
    nodes.forEach(node => {
      if (!typeGroups.has(node.group)) { typeGroups.set(node.group, groupIndex++); }
    });

    const totalGroups = typeGroups.size;
    const radius = 100;

    return nodes.map(node => {
      const groupIdx = typeGroups.get(node.group) ?? 0;
      const angle = (groupIdx / totalGroups) * Math.PI * 2;
      return {
        ...node,
        x: Math.cos(angle) * radius + (Math.random() - 0.5) * 50,
        y: Math.sin(angle) * radius + (Math.random() - 0.5) * 50,
        z: (Math.random() - 0.5) * 50,
      };
    });
  }, [nodes]);

  const handleNodeClick = useCallback((node: GraphNode) => {
    if (fgRef.current) {
      const distance = 100;
      const distRatio = 1 + distance / Math.hypot(node.x ?? 0, node.y ?? 0, node.z ?? 0);
      fgRef.current.cameraPosition(
        { x: (node.x ?? 0) * distRatio, y: (node.y ?? 0) * distRatio, z: (node.z ?? 0) * distRatio },
        node, 1000,
      );
    }
    onNodeClick?.(node);
  }, [onNodeClick]);

  const nodeThreeObject = useCallback((node: GraphNode) => {
    const color = node.color ?? ENTITY_COLORS.default;
    const geometry = new THREE.SphereGeometry(3.2, 16, 16);
    const material = new THREE.MeshStandardMaterial({
      color, emissive: color, emissiveIntensity: 0.6, roughness: 0.2, metalness: 0.1,
    });
    const mesh = new THREE.Mesh(geometry, material);

    const ringGeo = new THREE.RingGeometry(4, 5.5, 32);
    const ringMat = new THREE.MeshBasicMaterial({ color, side: THREE.DoubleSide, transparent: true, opacity: 0.3 });
    mesh.add(new THREE.Mesh(ringGeo, ringMat));

    return mesh;
  }, []);

  const handleNodeHover = useCallback((node: GraphNode | null) => {
    if (hoveredNodeRef.current && fgRef.current) {
      const prev = fgRef.current.scene()?.getObjectByProperty('__data', hoveredNodeRef.current);
      if (prev?.material && 'emissiveIntensity' in prev.material) {
        prev.material.emissiveIntensity = 0.6;
      }
    }
    if (node && fgRef.current) {
      const obj = fgRef.current.scene()?.getObjectByProperty('__data', node);
      if (obj?.material && 'emissiveIntensity' in obj.material) {
        obj.material.emissiveIntensity = 1.4;
      }
    }
    hoveredNodeRef.current = node;
  }, []);

  const nodeLabel = useCallback((node: GraphNode) => {
    const color = node.color ?? ENTITY_COLORS.default;
    return `<div style="background:rgba(11,15,26,0.95);padding:8px 12px;border-radius:6px;border:1px solid ${color};font-size:12px;max-width:200px;box-shadow:0 0 10px ${color}40;">
      <div style="color:${color};font-weight:bold;margin-bottom:4px;">${node.group}</div>
      <div style="color:#eaeaea;">${node.label}</div>
    </div>`;
  }, []);

  const getLinkColor = useCallback((link: GraphLink) => {
    if (typeof link.color === 'string') { return link.color; }
    return LINK_TYPE_COLORS.default;
  }, []);

  return (
    <ForceGraph3DLib
      ref={fgRef}
      graphData={{ nodes: clusteredNodes, links }}
      nodeId="id"
      nodeLabel={nodeLabel}
      nodeColor={(node: GraphNode) => node.color ?? ENTITY_COLORS.default}
      nodeThreeObject={nodeThreeObject}
      nodeThreeObjectExtend={true}
      linkColor={getLinkColor}
      linkWidth={(link: GraphLink) => link.value ? Math.min(link.value, 2.5) : 1}
      linkOpacity={0.35}
      linkDirectionalParticles={2}
      linkDirectionalParticleWidth={2}
      linkDirectionalParticleSpeed={0.005}
      onNodeClick={handleNodeClick}
      onNodeHover={handleNodeHover}
      backgroundColor={BACKGROUND_COLOR}
      showNavInfo={false}
      enableNodeDrag={true}
      enableNavigationControls={true}
      controlType="orbit"
      d3AlphaDecay={0.02}
      d3VelocityDecay={0.3}
      warmupTicks={100}
      cooldownTicks={200}
    />
  );
}

// --- App ---

function App() {
  const [nodes, setNodes] = useState<GraphNode[]>([]);
  const [links, setLinks] = useState<GraphLink[]>([]);
  const [allNodes, setAllNodes] = useState<GraphNode[]>([]);
  const [allLinks, setAllLinks] = useState<GraphLink[]>([]);

  useEffect(() => {
    const handler = (event: MessageEvent) => {
      const message = event.data;
      if (message.type === 'graphData') {
        const graphNodes = (message.nodes ?? []) as GraphNode[];
        const graphLinks = (message.links ?? []) as GraphLink[];

        const coloredNodes = graphNodes.map(n => ({
          ...n,
          color: ENTITY_COLORS[n.group?.toUpperCase()] ?? ENTITY_COLORS.default,
        }));

        setAllNodes(coloredNodes);
        setAllLinks(graphLinks);
        setNodes(coloredNodes);
        setLinks(graphLinks);
      }
    };

    window.addEventListener('message', handler);
    vscode.postMessage({ type: 'requestData' });
    return () => window.removeEventListener('message', handler);
  }, []);

  const handleSearch = useCallback((query: string) => {
    vscode.postMessage({ type: 'search', query });
  }, []);

  const handleFilterChange = useCallback((types: string[]) => {
    const typeSet = new Set(types.map(t => t.toUpperCase()));
    const filtered = allNodes.filter(n => typeSet.has(n.group?.toUpperCase()));
    const filteredIds = new Set(filtered.map(n => n.id));
    const filteredLinks = allLinks.filter(l => {
      const src = typeof l.source === 'string' ? l.source : l.source.id;
      const tgt = typeof l.target === 'string' ? l.target : l.target.id;
      return filteredIds.has(src) && filteredIds.has(tgt);
    });
    setNodes(filtered);
    setLinks(filteredLinks);
  }, [allNodes, allLinks]);

  const handleReset = useCallback(() => {
    setNodes(allNodes);
    setLinks(allLinks);
    vscode.postMessage({ type: 'requestData' });
  }, [allNodes, allLinks]);

  const handleNodeClick = useCallback((node: GraphNode) => {
    vscode.postMessage({ type: 'nodeClick', nodeId: node.id, label: node.label, group: node.group });
  }, []);

  return (
    <div style={{ width: '100vw', height: '100vh', position: 'relative', overflow: 'hidden' }}>
      <GraphControls onSearch={handleSearch} onFilterChange={handleFilterChange} onReset={handleReset} />
      <ForceGraph3D nodes={nodes} links={links} onNodeClick={handleNodeClick} />
    </div>
  );
}

const root = document.getElementById('root');
if (root) {
  createRoot(root).render(<App />);
}
