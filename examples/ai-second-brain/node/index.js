// =============================================================================
// AI Second Brain Example (Node.js)
// =============================================================================
// Demonstrates using SerialMemory as a persistent "second brain" for storing
// and retrieving long-term notes, decisions, and insights.
//
// Run: npm start
// =============================================================================

import { SerialMemoryClient } from '@serialmemory/client';

console.log('🧠 AI Second Brain Example');
console.log('==========================\n');

// Initialize client - uses local dev environment by default
const client = new SerialMemoryClient({
  baseUrl: process.env.SERIALMEMORY_URL || 'http://localhost:5000',
  apiKey: process.env.SERIALMEMORY_API_KEY || 'demo-key',
  defaultSource: 'second-brain'
});

// =============================================================================
// PART 1: Store Different Types of Memories
// =============================================================================

console.log('📝 Part 1: Storing memories of different types...\n');

// Store an architecture decision (ADR)
console.log('Storing architecture decision...');
const adr = await client.ingest(
  `Architecture Decision: Use PostgreSQL with pgvector

Context:
We need a database that supports both traditional queries and semantic
vector search for our AI-powered features.

Decision:
Adopt PostgreSQL 16 with the pgvector extension for all persistent storage.

Rationale:
- Single database for both relational and vector data
- Mature ecosystem and excellent tooling
- Lower operational complexity than separate vector DB
- Cost-effective compared to managed vector services

Alternatives Considered:
- Pinecone: Rejected due to cost at scale ($70/mo minimum)
- Qdrant: Rejected due to operational complexity
- MongoDB Atlas: Rejected due to weaker SQL support

Consequences:
- Need to manage pgvector extension updates
- Vector index maintenance required for large datasets
- Team needs training on vector similarity concepts

Date: 2024-01-15`,
  {
    metadata: {
      type: 'architecture-decision',
      status: 'accepted',
      supersedes: 'none'
    }
  }
);
console.log(`  ✅ Stored ADR: ${adr.memoryId}`);
console.log(`     Entities: ${adr.extractedEntities.map(e => e.name).join(', ')}`);

// Store a bug fix analysis
console.log('\nStoring bug fix analysis...');
const bugfix = await client.ingest(
  `Bug Fix: Memory leak in WebSocket connection handler

Issue: Server memory grew unbounded when clients disconnected unexpectedly

Root Cause:
The connection cleanup handler wasn't being invoked for connections that
timed out (as opposed to clean disconnects). Event handlers remained
subscribed, preventing garbage collection.

Solution:
1. Added timeout detection with a heartbeat mechanism
2. Implemented cleanup for connection state on timeout
3. Added defensive cleanup in finally blocks
4. Set up memory pressure alerts in Grafana

Affected Files:
- src/websocket/ConnectionHandler.ts
- src/websocket/HeartbeatService.ts
- tests/websocket/ConnectionTests.ts

Testing: Added load test simulating 10k connections with random drops.
Memory now stable at ~500MB under load.

Date: 2024-01-20`,
  {
    metadata: {
      type: 'bug-fix',
      severity: 'critical',
      component: 'websocket'
    }
  }
);
console.log(`  ✅ Stored bug fix: ${bugfix.memoryId}`);

// Store a learning/insight
console.log('\nStoring technical learning...');
const learning = await client.ingest(
  `Learning: pgvector index selection matters significantly

Discovery:
While investigating slow semantic search queries, I found that index type
selection dramatically affects performance:

- IVFFlat: Fast build, good for < 1M vectors, requires tuning lists param
- HNSW: Slower build, better recall, recommended for production

Key insight: Always benchmark with realistic data. Our test dataset was
too small to reveal the IVFFlat limitations.

Action: Switched to HNSW index, query time dropped from 200ms to 15ms
for our 500k vector dataset.

Reference: https://github.com/pgvector/pgvector#indexing`,
  {
    metadata: {
      type: 'learning',
      topic: 'database-optimization'
    }
  }
);
console.log(`  ✅ Stored learning: ${learning.memoryId}`);

// Store a meeting note
console.log('\nStoring meeting note...');
const meeting = await client.ingest(
  `Meeting: Product sync with Sarah Chen (Acme Corp)
Date: 2024-01-22
Attendees: Me, Sarah Chen (Product Lead at Acme)

Discussion:
- Acme wants to integrate SerialMemory into their enterprise product
- Timeline: POC by end of February, production Q2
- Key requirement: SOC 2 compliance documentation
- Budget: ~$50k/year for enterprise tier

Action Items:
1. [Me] Send SOC 2 compliance overview by Friday
2. [Me] Set up sandbox environment for Acme
3. [Sarah] Provide list of specific compliance questions
4. [Both] Schedule technical deep-dive for next week

Follow-up: February 5th`,
  {
    metadata: {
      type: 'meeting',
      company: 'Acme Corp',
      contact: 'Sarah Chen'
    }
  }
);
console.log(`  ✅ Stored meeting: ${meeting.memoryId}`);

// =============================================================================
// PART 2: Build User Persona
// =============================================================================

console.log('\n👤 Part 2: Building user persona...\n');

await client.setUserPersona(
  'skill',
  'programming_languages',
  'TypeScript, Python, C# - primary languages',
  { confidence: 0.95 }
);
console.log('  ✅ Set skill: programming_languages');

await client.setUserPersona(
  'skill',
  'infrastructure',
  'PostgreSQL, Redis, Docker, Kubernetes',
  { confidence: 0.9 }
);
console.log('  ✅ Set skill: infrastructure');

await client.setUserPersona(
  'preference',
  'code_style',
  'Prefer explicit code over clever abstractions, good error messages',
  { confidence: 0.85 }
);
console.log('  ✅ Set preference: code_style');

await client.setUserPersona(
  'goal',
  'current_focus',
  'Launch SerialMemory to public, acquire first 10 paying customers',
  { confidence: 1.0 }
);
console.log('  ✅ Set goal: current_focus');

// Retrieve and display persona
const persona = await client.getUserPersona();
console.log('\n  📋 Current Persona:');
for (const [key, attr] of Object.entries(persona.skills)) {
  console.log(`     Skill: ${key} = ${attr.value}`);
}
for (const [key, attr] of Object.entries(persona.preferences)) {
  console.log(`     Pref: ${key} = ${attr.value}`);
}
for (const [key, attr] of Object.entries(persona.goals)) {
  console.log(`     Goal: ${key} = ${attr.value}`);
}

// =============================================================================
// PART 3: Search for Context
// =============================================================================

console.log('\n🔍 Part 3: Searching for context...\n');

// Simulate: "What did we decide about databases?"
console.log('Query: "database decision architecture"\n');

const dbResults = await client.search('database decision architecture', {
  mode: 'hybrid',
  limit: 3,
  threshold: 0.4
});

for (const memory of dbResults.memories) {
  const preview = memory.content.split('\n')[0];
  console.log(`  📄 [${(memory.similarity * 100).toFixed(0)}%] ${preview}`);
}

// Simulate: "What's the status with Acme?"
console.log('\nQuery: "Acme Corp status meeting"\n');

const acmeResults = await client.search('Acme Corp status meeting', { limit: 3 });

for (const memory of acmeResults.memories) {
  const preview = memory.content.split('\n')[0];
  console.log(`  📄 [${(memory.similarity * 100).toFixed(0)}%] ${preview}`);
}

// =============================================================================
// PART 4: Multi-Hop Search
// =============================================================================

console.log('\n🔗 Part 4: Multi-hop search (finding connections)...\n');

console.log('Query: "Sarah Chen" (finding related context)\n');

const multiHop = await client.multiHopSearch('Sarah Chen', {
  hops: 2,
  maxResultsPerHop: 3
});

console.log(`  Direct matches: ${multiHop.initialMemories.length}`);
for (const memory of multiHop.initialMemories) {
  const preview = memory.content.split('\n')[0];
  console.log(`    • ${preview}`);
}

if (multiHop.hops.length > 0) {
  console.log('\n  Related via knowledge graph:');
  for (const hop of multiHop.hops) {
    console.log(`    Hop ${hop.hopNumber} via '${hop.sourceEntity}':`);
    for (const memory of hop.memories.slice(0, 2)) {
      const preview = memory.content.split('\n')[0];
      console.log(`      • ${preview}`);
    }
  }
}

if (multiHop.entities.length > 0) {
  console.log(`\n  Discovered entities: ${multiHop.entities.map(e => e.name).join(', ')}`);
}

// =============================================================================
// PART 5: Statistics
// =============================================================================

console.log('\n📊 Part 5: Knowledge graph statistics...\n');

const stats = await client.getGraphStatistics();
console.log(`  Total memories: ${stats.totalMemories}`);
console.log(`  Total entities: ${stats.totalEntities}`);
console.log(`  Total relationships: ${stats.totalRelationships}`);

if (Object.keys(stats.entitiesByType).length > 0) {
  console.log('  Entities by type:');
  const sorted = Object.entries(stats.entitiesByType)
    .sort(([, a], [, b]) => b - a)
    .slice(0, 5);
  for (const [type, count] of sorted) {
    console.log(`    ${type}: ${count}`);
  }
}

console.log('\n✨ Demo complete!');
console.log("Your 'second brain' now has persistent memory that grows over time.");
console.log('Run again to see memories accumulate and improve search relevance.');
