/**
 * SerialMemory Project Context Memory Example
 *
 * Demonstrates using metadata to create project-scoped memory namespaces.
 * Each project gets isolated context that can be searched independently.
 *
 * Usage:
 *   SERIALMEMORY_URL=http://localhost:5000 SERIALMEMORY_API_KEY=sm_live_xxx node index.js
 */

// Note: In a real project, you would import from '@serialmemory/sdk'
// For this example, we'll import from the local SDK
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';
import { readFileSync } from 'fs';

// Simple SDK implementation for the example (in production, use @serialmemory/sdk)
class SerialMemoryClient {
  constructor({ baseUrl, apiKey }) {
    this.baseUrl = baseUrl.replace(/\/$/, '');
    this.headers = {
      'Content-Type': 'application/json',
    };

    if (apiKey.startsWith('sm_')) {
      this.headers['X-API-Key'] = apiKey;
    } else {
      this.headers['Authorization'] = `Bearer ${apiKey}`;
    }
  }

  async search(query, { metadata } = {}) {
    const request = {
      query,
      mode: 'hybrid',
      limit: 10,
      threshold: 0.5,
      include_entities: true,
    };

    // Add metadata filter if provided
    if (metadata) {
      request.metadata = metadata;
    }

    return this.post('/tools/memory_search', request);
  }

  async ingest(content, { source, metadata } = {}) {
    const request = {
      content,
      source: source ?? 'project-context-example',
      metadata,
      extract_entities: true,
    };

    return this.post('/tools/memory_ingest', request);
  }

  async getLimits() {
    return this.get('/tenant/limits');
  }

  async post(endpoint, body) {
    const response = await fetch(`${this.baseUrl}${endpoint}`, {
      method: 'POST',
      headers: this.headers,
      body: JSON.stringify(body),
    });

    if (!response.ok) {
      const error = await response.text();
      throw new Error(`Request failed (${response.status}): ${error}`);
    }

    return response.json();
  }

  async get(endpoint) {
    const response = await fetch(`${this.baseUrl}${endpoint}`, {
      method: 'GET',
      headers: this.headers,
    });

    if (!response.ok) {
      const error = await response.text();
      throw new Error(`Request failed (${response.status}): ${error}`);
    }

    return response.json();
  }
}

// ===== Main Example =====

console.log('=== SerialMemory Project Context Memory Example ===\n');

const baseUrl = process.env.SERIALMEMORY_URL ?? 'http://localhost:5000';
const apiKey = process.env.SERIALMEMORY_API_KEY ?? 'sm_live_demo_key';

const client = new SerialMemoryClient({ baseUrl, apiKey });

// Define two projects with different contexts
const projects = [
  {
    id: 'proj-ecommerce-api',
    name: 'E-Commerce API',
    memories: [
      'Architecture decision: Using PostgreSQL for orders because we need ACID transactions and complex queries for inventory management.',
      'Bug fix: Order total calculation was wrong due to floating point errors. Solution: Use DECIMAL type and round to cents.',
      'API endpoint: POST /api/orders creates a new order. Requires authentication. Returns order ID and estimated delivery.',
      'Performance optimization: Added Redis cache for product catalog. Reduced API latency from 200ms to 15ms.',
    ],
  },
  {
    id: 'proj-mobile-app',
    name: 'Mobile App',
    memories: [
      'Architecture decision: Using React Native for cross-platform development. iOS and Android from single codebase.',
      'UI/UX: Implemented pull-to-refresh on all list screens. Users expect this on mobile.',
      'Bug fix: App was crashing on Android 12 due to new permissions model. Added runtime permission checks.',
      'Feature: Offline mode stores pending orders in SQLite. Syncs when connection restored.',
    ],
  },
];

try {
  // 1. Ingest memories for each project
  console.log('1. Ingesting project-scoped memories...\n');

  for (const project of projects) {
    console.log(`  Project: ${project.name} (${project.id})`);

    for (const memory of project.memories) {
      const result = await client.ingest(memory, {
        source: 'project-context-example',
        metadata: {
          project_id: project.id,
          project_name: project.name,
          type: memory.includes('Architecture') ? 'architecture' :
                memory.includes('Bug') ? 'bugfix' :
                memory.includes('API') ? 'api' :
                memory.includes('Feature') ? 'feature' : 'general',
        },
      });

      console.log(`    + ${memory.substring(0, 50)}...`);
      console.log(`      Memory ID: ${result.memory_id}`);
    }
    console.log();
  }

  // 2. Search within a specific project
  console.log('\n2. Searching within specific projects...\n');

  // Search E-Commerce project for database-related info
  console.log('  Query: "database and caching" (E-Commerce project only)');
  const ecommerceSearch = await client.search('database and caching', {
    metadata: { project_id: 'proj-ecommerce-api' },
  });

  if (ecommerceSearch.memories?.length > 0) {
    for (const match of ecommerceSearch.memories) {
      console.log(`    [${(match.score * 100).toFixed(0)}%] ${match.content.substring(0, 70)}...`);
    }
  } else {
    console.log('    No results found.');
  }

  console.log();

  // Search Mobile App project for bug fixes
  console.log('  Query: "crash fix" (Mobile App project only)');
  const mobileSearch = await client.search('crash fix', {
    metadata: { project_id: 'proj-mobile-app' },
  });

  if (mobileSearch.memories?.length > 0) {
    for (const match of mobileSearch.memories) {
      console.log(`    [${(match.score * 100).toFixed(0)}%] ${match.content.substring(0, 70)}...`);
    }
  } else {
    console.log('    No results found.');
  }

  // 3. Cross-project search (no project filter)
  console.log('\n\n3. Cross-project search (all projects)...\n');

  console.log('  Query: "architecture decisions"');
  const crossSearch = await client.search('architecture decisions');

  if (crossSearch.memories?.length > 0) {
    for (const match of crossSearch.memories) {
      const projectName = match.metadata?.project_name ?? 'Unknown';
      console.log(`    [${projectName}] ${match.content.substring(0, 60)}...`);
    }
  } else {
    console.log('    No results found.');
  }

  // 4. Check usage
  console.log('\n\n4. Checking usage limits...\n');

  const limits = await client.getLimits();
  console.log(`  Plan: ${limits.display_name ?? limits.plan}`);
  console.log(`  Credits: ${limits.credits_used}/${limits.monthly_credits}`);
  console.log(`  Memories: ${limits.current_memories}${limits.max_memories ? '/' + limits.max_memories : ''}`);

  if (limits.warnings?.length > 0) {
    console.log('\n  Warnings:');
    for (const warning of limits.warnings) {
      console.log(`    [${warning.severity}] ${warning.message}`);
    }
  }

  console.log('\n=== Example complete! ===');
  console.log('\nKey takeaways:');
  console.log('  - Use metadata.project_id to namespace memories by project');
  console.log('  - Search with metadata filter for project-specific context');
  console.log('  - Omit filter for cross-project searches');

} catch (error) {
  console.error('Error:', error.message);
  process.exit(1);
}
