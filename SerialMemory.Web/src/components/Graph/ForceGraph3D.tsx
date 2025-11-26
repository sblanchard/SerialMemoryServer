import { useCallback, useRef, useMemo, useEffect } from 'react';
import ForceGraph3DLib from 'react-force-graph-3d';
import type { ForceGraphNode, ForceGraphLink } from '../../types/graph';
import { ENTITY_COLORS } from '../../types/graph';
import * as THREE from 'three';
import { EffectComposer } from 'three/examples/jsm/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/examples/jsm/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/examples/jsm/postprocessing/UnrealBloomPass.js';

interface ForceGraph3DProps {
  nodes: ForceGraphNode[];
  links: ForceGraphLink[];
  onNodeClick?: (node: ForceGraphNode) => void;
  onNodeHover?: (node: ForceGraphNode | null) => void;
  showLabels?: boolean;
  clusterByType?: boolean;
}

// Deep tech background color
const BACKGROUND_COLOR = '#0B0F1A';

export function ForceGraph3D({
  nodes,
  links,
  onNodeClick,
  onNodeHover,
  showLabels: _showLabels = true,
  clusterByType = true,
}: ForceGraph3DProps) {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const fgRef = useRef<any>(null);
  const hoveredNodeRef = useRef<ForceGraphNode | null>(null);

  // Setup bloom effect and scene fog
  useEffect(() => {
    const fg = fgRef.current;
    if (!fg) return;

    // Add subtle fog for depth
    const scene = fg.scene();
    if (scene) {
      scene.fog = new THREE.Fog(BACKGROUND_COLOR, 50, 600);
    }

    // Setup bloom post-processing
    const renderer = fg.renderer();
    const camera = fg.camera();

    if (renderer && scene && camera) {
      const composer = new EffectComposer(renderer);
      composer.addPass(new RenderPass(scene, camera));

      const bloomPass = new UnrealBloomPass(
        new THREE.Vector2(window.innerWidth, window.innerHeight),
        1.2,  // strength
        0.6,  // radius
        0.2   // threshold
      );
      composer.addPass(bloomPass);

      fg.postProcessingComposer(composer);
    }

    // Camera tuning for cinematic look
    if (camera) {
      camera.position.set(0, 0, 280);
      camera.near = 1;
      camera.far = 2000;
      camera.updateProjectionMatrix();
    }

    // Slow rotation to feel "alive"
    let animationId: number;
    const animate = () => {
      animationId = requestAnimationFrame(animate);
      if (scene) {
        scene.rotation.y += 0.00025;
      }
    };
    animate();

    return () => {
      if (animationId) {
        cancelAnimationFrame(animationId);
      }
    };
  }, []);

  // Apply clustering forces based on entity type
  const clusteredNodes = useMemo(() => {
    if (!clusterByType) return nodes;

    // Group nodes by type and assign cluster positions
    const typeGroups = new Map<string, number>();
    let groupIndex = 0;

    nodes.forEach(node => {
      if (!typeGroups.has(node.group)) {
        typeGroups.set(node.group, groupIndex++);
      }
    });

    const totalGroups = typeGroups.size;
    const radius = 100;

    return nodes.map(node => {
      const groupIdx = typeGroups.get(node.group) || 0;
      const angle = (groupIdx / totalGroups) * Math.PI * 2;

      return {
        ...node,
        fx: undefined, // Let physics handle it but with initial position
        fy: undefined,
        fz: undefined,
        x: Math.cos(angle) * radius + (Math.random() - 0.5) * 50,
        y: Math.sin(angle) * radius + (Math.random() - 0.5) * 50,
        z: (Math.random() - 0.5) * 50,
      };
    });
  }, [nodes, clusterByType]);

  // Handle node click
  const handleNodeClick = useCallback((node: ForceGraphNode) => {
    // Focus on node
    if (fgRef.current) {
      const distance = 100;
      const distRatio = 1 + distance / Math.hypot(node.x || 0, node.y || 0, node.z || 0);

      fgRef.current.cameraPosition(
        {
          x: (node.x || 0) * distRatio,
          y: (node.y || 0) * distRatio,
          z: (node.z || 0) * distRatio,
        },
        node,
        1000
      );
    }

    onNodeClick?.(node);
  }, [onNodeClick]);

  // Create glow ring for node aura
  const createGlowRing = useCallback((color: string) => {
    const geometry = new THREE.RingGeometry(4, 5.5, 32);
    const material = new THREE.MeshBasicMaterial({
      color,
      side: THREE.DoubleSide,
      transparent: true,
      opacity: 0.3,
    });
    return new THREE.Mesh(geometry, material);
  }, []);

  // Custom node rendering with emissive glow spheres
  const nodeThreeObject = useCallback((node: ForceGraphNode) => {
    const color = node.color || ENTITY_COLORS.default;

    const geometry = new THREE.SphereGeometry(3.2, 16, 16);
    const material = new THREE.MeshStandardMaterial({
      color,
      emissive: color,
      emissiveIntensity: 0.6,
      roughness: 0.2,
      metalness: 0.1,
    });

    const mesh = new THREE.Mesh(geometry, material);

    // Add glow ring aura
    const glowRing = createGlowRing(color);
    mesh.add(glowRing);

    return mesh;
  }, [createGlowRing]);

  // Handle node hover - increase emissive intensity
  const handleNodeHover = useCallback((node: ForceGraphNode | null) => {
    // Reset previous hovered node
    if (hoveredNodeRef.current && fgRef.current) {
      const prevObj = fgRef.current.scene()?.getObjectByProperty('__data', hoveredNodeRef.current) as THREE.Mesh | undefined;
      if (prevObj?.material && 'emissiveIntensity' in prevObj.material) {
        (prevObj.material as THREE.MeshStandardMaterial).emissiveIntensity = 0.6;
      }
    }

    // Apply glow to new hovered node
    if (node && fgRef.current) {
      const obj = fgRef.current.scene()?.getObjectByProperty('__data', node) as THREE.Mesh | undefined;
      if (obj?.material && 'emissiveIntensity' in obj.material) {
        (obj.material as THREE.MeshStandardMaterial).emissiveIntensity = 1.4;
      }
    }

    hoveredNodeRef.current = node;
    onNodeHover?.(node);
  }, [onNodeHover]);

  // Node label
  const nodeLabel = useCallback((node: ForceGraphNode) => {
    return `<div style="
      background: rgba(11, 15, 26, 0.95);
      padding: 8px 12px;
      border-radius: 6px;
      border: 1px solid ${node.color || ENTITY_COLORS.default};
      font-size: 12px;
      max-width: 200px;
      box-shadow: 0 0 10px ${node.color || ENTITY_COLORS.default}40;
    ">
      <div style="color: ${node.color || ENTITY_COLORS.default}; font-weight: bold; margin-bottom: 4px;">
        ${node.group}
      </div>
      <div style="color: #eaeaea;">
        ${node.label}
      </div>
    </div>`;
  }, []);

  // Link color with neural feel
  const linkColor = useCallback((link: ForceGraphLink) => {
    if (typeof link.color === 'string') return link.color;
    // Default subtle cyan for neural look
    return 'rgba(34, 211, 238, 0.35)';
  }, []);

  // Link width based on weight
  const linkWidth = useCallback((link: ForceGraphLink) => {
    return link.value ? Math.min(link.value, 2.5) : 1;
  }, []);

  return (
    <ForceGraph3DLib
      ref={fgRef}
      graphData={{ nodes: clusteredNodes, links }}
      nodeId="id"
      nodeLabel={nodeLabel}
      nodeColor={(node: ForceGraphNode) => node.color || ENTITY_COLORS.default}
      nodeThreeObject={nodeThreeObject}
      nodeThreeObjectExtend={false}
      linkColor={linkColor}
      linkWidth={linkWidth}
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
