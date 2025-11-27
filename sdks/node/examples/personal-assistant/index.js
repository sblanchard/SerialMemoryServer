// =============================================================================
// SerialMemory SDK Example: Personal Assistant (Node.js)
// =============================================================================
// This example demonstrates how to use SerialMemory as a persistent memory
// layer for a personal AI assistant. It shows:
//
// 1. Storing personal notes, decisions, and preferences
// 2. Searching for relevant context before responding
// 3. Building up a user persona over time
// 4. Multi-hop search to find related information
//
// Run with: npm start
// =============================================================================

import { SerialMemoryClient } from '@serialmemory/client';

// -----------------------------------------------------------------------------
// Configuration
// -----------------------------------------------------------------------------

const client = new SerialMemoryClient({
  // Point to your local SerialMemory instance
  baseUrl: process.env.SERIALMEMORY_URL || 'http://localhost:5000',

  // Use API key authentication (recommended for server apps)
  apiKey: process.env.SERIALMEMORY_API_KEY || 'demo-key',

  // Tag all memories with this source for easy filtering
  defaultSource: 'personal-assistant-demo',

  // Optional: callbacks for monitoring
  onRetry: (attempt, error) => console.log(`⚠️ Retry attempt ${attempt}: ${error.message}`),
  onCircuitOpen: (duration) => console.log(`🔴 Circuit breaker opened for ${duration / 1000}s`)
});

console.log('🧠 SerialMemory Personal Assistant Demo');
console.log('========================================\n');

// -----------------------------------------------------------------------------
// Part 1: Store Some Personal Notes and Decisions
// -----------------------------------------------------------------------------
console.log('📝 Part 1: Storing personal notes...\n');

// Store a decision with context
const decision1 = await client.ingest(
  `Decision: Adopt PostgreSQL as our primary database for the new project.
Rationale: Excellent support for JSON, full-text search, and pgvector for AI embeddings.
Alternatives considered: MongoDB (rejected due to cost), MySQL (rejected due to limited JSON support).
Date: January 2024`,
  {
    source: 'architecture-decisions',
    metadata: {
      type: 'decision',
      project: 'new-saas-app',
      importance: 'high'
    }
  }
);
console.log(`✅ Stored decision: ${decision1.memoryId}`);
console.log(`   Extracted ${decision1.extractedEntities.length} entities: ${decision1.extractedEntities.map(e => e.name).join(', ')}\n`);

// Store a learning/insight
const learning1 = await client.ingest(
  `Learning: When using pgvector, always normalize embeddings to unit length before storage.
This ensures cosine similarity works correctly and improves query performance.
Discovered while debugging slow semantic search queries.`,
  {
    source: 'technical-learnings',
    metadata: {
      type: 'learning',
      topic: 'database',
      difficultyLevel: 'intermediate'
    }
  }
);
console.log(`✅ Stored learning: ${learning1.memoryId}`);

// Store a personal preference
const preference1 = await client.ingest(
  `Personal preference: I prefer TypeScript over JavaScript for all new projects.
Reason: Better tooling, type safety catches bugs early, improved IDE experience.
I've been using TypeScript exclusively since 2022.`,
  { source: 'personal-preferences' }
);
console.log(`✅ Stored preference: ${preference1.memoryId}`);

// Store a meeting note
const meeting1 = await client.ingest(
  `Meeting with Sarah Chen from Acme Corp on January 15, 2024.
Topics discussed:
- Integration timeline: Q2 2024
- API requirements: REST with OAuth 2.0
- Data format: JSON with snake_case naming
Action items:
- Send API documentation by end of week
- Schedule follow-up for February`,
  {
    source: 'meeting-notes',
    metadata: {
      type: 'meeting',
      attendees: ['Sarah Chen'],
      company: 'Acme Corp'
    }
  }
);
console.log(`✅ Stored meeting note: ${meeting1.memoryId}\n`);

// -----------------------------------------------------------------------------
// Part 2: Set Up User Persona
// -----------------------------------------------------------------------------
console.log('👤 Part 2: Building user persona...\n');

// Record skills
await client.setUserPersona('skill', 'primary_language', 'TypeScript', { confidence: 0.95 });
console.log('✅ Set skill: primary_language = TypeScript');

await client.setUserPersona('skill', 'databases', 'PostgreSQL, Redis', { confidence: 0.9 });
console.log('✅ Set skill: databases = PostgreSQL, Redis');

// Record preferences
await client.setUserPersona('preference', 'code_style', 'Prefer explicit over implicit, clear naming over comments', { confidence: 0.85 });
console.log('✅ Set preference: code_style');

// Record a goal
await client.setUserPersona('goal', 'current_project', 'Build a production-ready SaaS platform', { confidence: 1.0 });
console.log('✅ Set goal: current_project\n');

// Retrieve and display persona
const persona = await client.getUserPersona();
console.log('📋 Current User Persona:');
console.log(`   Skills: ${Object.entries(persona.skills).map(([k, v]) => `${k}=${v.value}`).join(', ')}`);
console.log(`   Preferences: ${Object.keys(persona.preferences).join(', ')}`);
console.log(`   Goals: ${Object.values(persona.goals).map(g => g.value).join(', ')}\n`);

// -----------------------------------------------------------------------------
// Part 3: Search for Relevant Context
// -----------------------------------------------------------------------------
console.log('🔍 Part 3: Searching for context...\n');

// Simulate: User asks "What database should I use?"
console.log('User: What database should I use for the new project?\n');

const searchResults = await client.search('database decision project recommendations', {
  mode: 'hybrid',
  limit: 3,
  threshold: 0.5
});

console.log(`Found ${searchResults.memories.length} relevant memories:\n`);
for (const memory of searchResults.memories) {
  console.log(`📄 [${(memory.similarity * 100).toFixed(0)}% match] (${memory.source})`);
  // Show first 150 chars of content
  const preview = memory.content.length > 150
    ? memory.content.slice(0, 150) + '...'
    : memory.content;
  console.log(`   ${preview.replace(/\n/g, ' ')}\n`);
}

// -----------------------------------------------------------------------------
// Part 4: Multi-Hop Search for Related Information
// -----------------------------------------------------------------------------
console.log('🔗 Part 4: Multi-hop search (finding connections)...\n');

// Simulate: User asks about Acme Corp
console.log('User: What do I know about Acme Corp?\n');

const multiHopResults = await client.multiHopSearch('Acme Corp', {
  hops: 2,
  maxResultsPerHop: 3
});

console.log(`Initial matches: ${multiHopResults.initialMemories.length}`);
for (const memory of multiHopResults.initialMemories) {
  console.log(`   • ${memory.content.slice(0, Math.min(100, memory.content.length))}...`);
}

if (multiHopResults.hops.length > 0) {
  console.log(`\nRelated through knowledge graph (${multiHopResults.hops.length} hops):`);
  for (const hop of multiHopResults.hops) {
    console.log(`   Hop ${hop.hopNumber} via '${hop.sourceEntity}':`);
    for (const memory of hop.memories) {
      console.log(`      • ${memory.content.slice(0, Math.min(80, memory.content.length))}...`);
    }
  }
}

if (multiHopResults.entities.length > 0) {
  console.log(`\nDiscovered entities: ${multiHopResults.entities.map(e => e.name).join(', ')}`);
}

// -----------------------------------------------------------------------------
// Part 5: Knowledge Graph Statistics
// -----------------------------------------------------------------------------
console.log('\n📊 Part 5: Knowledge graph statistics...\n');

const stats = await client.getGraphStatistics();
console.log(`Total memories: ${stats.totalMemories}`);
console.log(`Total entities: ${stats.totalEntities}`);
console.log(`Total relationships: ${stats.totalRelationships}`);

if (Object.keys(stats.entitiesByType).length > 0) {
  console.log('Entities by type:');
  for (const [type, count] of Object.entries(stats.entitiesByType)) {
    console.log(`   ${type}: ${count}`);
  }
}

console.log('\n✨ Demo complete! Your personal assistant now has persistent memory.');
console.log('Run this demo again to see how memories accumulate over time.');
