import { useCallback, useRef, useMemo } from 'react';
import ForceGraph3DLib from 'react-force-graph-3d';
import type { ForceGraphNode, ForceGraphLink } from '../../types/graph';
import { ENTITY_COLORS } from '../../types/graph';
import * as THREE from 'three';

interface ForceGraph3DProps {
  nodes: ForceGraphNode[];
  links: ForceGraphLink[];
  onNodeClick?: (node: ForceGraphNode) => void;
  onNodeHover?: (node: ForceGraphNode | null) => void;
  showLabels?: boolean;
  clusterByType?: boolean;
}

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

  // Custom node rendering with spheres
  const nodeThreeObject = useCallback((node: ForceGraphNode) => {
    const color = node.color || ENTITY_COLORS.default;
    const geometry = new THREE.SphereGeometry(5, 16, 16);
    const material = new THREE.MeshLambertMaterial({
      color,
      transparent: true,
      opacity: 0.9,
    });
    const sphere = new THREE.Mesh(geometry, material);

    // Add glow effect
    const glowGeometry = new THREE.SphereGeometry(7, 16, 16);
    const glowMaterial = new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity: 0.2,
    });
    const glow = new THREE.Mesh(glowGeometry, glowMaterial);
    sphere.add(glow);

    return sphere;
  }, []);

  // Node label
  const nodeLabel = useCallback((node: ForceGraphNode) => {
    return `<div style="
      background: rgba(22, 33, 62, 0.95);
      padding: 8px 12px;
      border-radius: 6px;
      border: 1px solid ${node.color || ENTITY_COLORS.default};
      font-size: 12px;
      max-width: 200px;
    ">
      <div style="color: ${node.color || ENTITY_COLORS.default}; font-weight: bold; margin-bottom: 4px;">
        ${node.group}
      </div>
      <div style="color: #eaeaea;">
        ${node.label}
      </div>
    </div>`;
  }, []);

  // Link color
  const linkColor = useCallback((link: ForceGraphLink) => {
    return typeof link.color === 'string' ? link.color : '#4a4a6a';
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
      linkWidth={0.3}
      linkOpacity={0.4}
      linkDirectionalParticles={2}
      linkDirectionalParticleWidth={2}
      linkDirectionalParticleSpeed={0.005}
      onNodeClick={handleNodeClick}
      onNodeHover={onNodeHover}
      backgroundColor="#1a1a2e"
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
