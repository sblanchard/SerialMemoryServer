// =============================================================================
// Project Context Memory Example (Node.js)
// =============================================================================
// Demonstrates project-specific memory isolation using SerialMemory.
// Ideal for IDEs, development tools, or AI assistants working across projects.
//
// Run: npm start
// =============================================================================

import { SerialMemoryClient } from '@serialmemory/client';

console.log('🗂️ Project Context Memory Example');
console.log('==================================\n');

// Initialize client
const client = new SerialMemoryClient({
  baseUrl: process.env.SERIALMEMORY_URL || 'http://localhost:5000',
  apiKey: process.env.SERIALMEMORY_API_KEY || 'demo-key',
  defaultSource: 'project-context-demo'
});

// =============================================================================
// Project Memory Manager - Wrapper for project-scoped operations
// =============================================================================

class ProjectMemory {
  constructor(client, projectId, projectName) {
    this.client = client;
    this.projectId = projectId;
    this.projectName = projectName;
  }

  async store(content, category, extra = {}) {
    const metadata = {
      projectId: this.projectId,
      projectName: this.projectName,
      category,
      timestamp: new Date().toISOString(),
      ...extra
    };

    const result = await this.client.ingest(
      `[${this.projectName}] ${content}`,
      {
        source: `project-${this.projectId}`,
        metadata
      }
    );
    return result.memoryId;
  }

  async search(query, limit = 5) {
    const contextualQuery = `${this.projectName} ${query}`;
    const results = await this.client.search(contextualQuery, {
      limit: limit * 2,
      threshold: 0.3
    });

    return results.memories
      .filter(m =>
        m.source === `project-${this.projectId}` ||
        m.content.startsWith(`[${this.projectName}]`)
      )
      .slice(0, limit);
  }

  async storeDecision(title, context, decision, consequences) {
    return this.store(
      `Architecture Decision: ${title}

Context:
${context}

Decision:
${decision}

Consequences:
${consequences}`,
      'architecture-decision',
      { adrTitle: title }
    );
  }

  async storeBugFix(issue, rootCause, solution, files) {
    return this.store(
      `Bug Fix: ${issue}

Root Cause:
${rootCause}

Solution:
${solution}

Affected Files:
${files.map(f => `- ${f}`).join('\n')}`,
      'bug-fix',
      { files }
    );
  }

  async storePattern(name, description, example) {
    return this.store(
      `Code Pattern: ${name}

Description:
${description}

Example:
\`\`\`
${example}
\`\`\``,
      'code-pattern',
      { patternName: name }
    );
  }
}

// =============================================================================
// Create Two Projects
// =============================================================================

const backendProject = new ProjectMemory(client, 'ecommerce-backend', 'E-commerce Backend');
const frontendProject = new ProjectMemory(client, 'ecommerce-frontend', 'E-commerce Frontend');

console.log('📦 Setting up two projects with isolated memory...\n');

// =============================================================================
// Backend Project Memories
// =============================================================================

console.log('Project: E-commerce Backend');
console.log('----------------------------');

await backendProject.storeDecision(
  'Use Stripe for Payments',
  'Need payment processing with support for subscriptions and multi-currency.',
  'Adopt Stripe with their Node.js SDK. Use webhooks for async events.',
  '2.9% + $0.30 per transaction. Need PCI compliance review.'
);
console.log('✅ Stored ADR: Payment processor');

await backendProject.storeBugFix(
  'Order total calculation off by $0.01',
  'Floating point arithmetic caused rounding errors when calculating discounts.',
  'Used decimal.js for all monetary calculations. Round only at final display.',
  ['services/orderService.ts', 'models/Order.ts', 'tests/orderCalculation.test.ts']
);
console.log('✅ Stored bug fix: Rounding error');

await backendProject.storePattern(
  'Repository Pattern',
  'Abstract database access behind repository interfaces. Enables testing and future DB swaps.',
  `interface OrderRepository {
  findById(id: string): Promise<Order | null>;
  save(order: Order): Promise<void>;
  findByCustomer(customerId: string): Promise<Order[]>;
}

class PostgresOrderRepository implements OrderRepository {
  constructor(private db: Pool) {}

  async findById(id: string) {
    const result = await this.db.query(
      'SELECT * FROM orders WHERE id = $1',
      [id]
    );
    return result.rows[0] || null;
  }
}`
);
console.log('✅ Stored pattern: Repository Pattern\n');

// =============================================================================
// Frontend Project Memories
// =============================================================================

console.log('Project: E-commerce Frontend');
console.log('-----------------------------');

await frontendProject.storeDecision(
  'Use Next.js App Router',
  'Need SSR for SEO, good DX, and seamless deployment to Vercel.',
  'Adopt Next.js 14 with App Router. Use Server Components by default.',
  'Team needs training on RSC patterns. Some library compatibility issues.'
);
console.log('✅ Stored ADR: Framework selection');

await frontendProject.storeBugFix(
  'Cart count not updating after add',
  'State mutation bug - was modifying cart array directly instead of creating new reference.',
  'Used immer for immutable state updates. Added React.StrictMode to catch mutations.',
  ['store/cartSlice.ts', 'components/CartButton.tsx']
);
console.log('✅ Stored bug fix: State mutation');

await frontendProject.storePattern(
  'Optimistic UI Updates',
  'Update UI immediately, revert on error. Improves perceived performance.',
  `async function addToCart(item) {
  // Optimistically update
  const prevCart = cart;
  setCart([...cart, item]);

  try {
    await api.addToCart(item);
  } catch (error) {
    // Revert on failure
    setCart(prevCart);
    toast.error('Failed to add item');
  }
}`
);
console.log('✅ Stored pattern: Optimistic UI\n');

// =============================================================================
// Project-Scoped Search
// =============================================================================

console.log('🔍 Project-scoped search demo...\n');

// Search in backend project
console.log("Query 'payment' in Backend:");
const backendPayment = await backendProject.search('payment', 3);
if (backendPayment.length > 0) {
  for (const m of backendPayment) {
    console.log(`  [${(m.similarity * 100).toFixed(0)}%] ${m.content.split('\n')[0]}`);
  }
} else {
  console.log('  (No results)');
}

console.log();

// Same query in frontend - should find nothing about payments
console.log("Query 'payment' in Frontend:");
const frontendPayment = await frontendProject.search('payment', 3);
if (frontendPayment.length > 0) {
  for (const m of frontendPayment) {
    console.log(`  [${(m.similarity * 100).toFixed(0)}%] ${m.content.split('\n')[0]}`);
  }
} else {
  console.log('  (No results - payment is backend concern)');
}

console.log();

// Search for state management in frontend
console.log("Query 'state bug' in Frontend:");
const frontendState = await frontendProject.search('state bug', 3);
for (const m of frontendState) {
  console.log(`  [${(m.similarity * 100).toFixed(0)}%] ${m.content.split('\n')[0]}`);
}

// =============================================================================
// Cross-Project Search
// =============================================================================

console.log('\n🌐 Cross-project search (all projects)...\n');

const globalResults = await client.search('code patterns best practices', {
  mode: 'hybrid',
  limit: 5
});

console.log("Query 'code patterns' (global):");
for (const m of globalResults.memories) {
  const tag = m.content.split(']')[0] + ']';
  const content = m.content.replace(tag, '').trim().split('\n')[0];
  console.log(`  ${tag} [${(m.similarity * 100).toFixed(0)}%] ${content}`);
}

// =============================================================================
// Summary
// =============================================================================

console.log('\n📊 Summary...\n');

const stats = await client.getGraphStatistics();
console.log(`  Total memories: ${stats.totalMemories}`);
console.log(`  Total entities: ${stats.totalEntities}`);
console.log(`  Total relationships: ${stats.totalRelationships}`);

console.log('\n✨ Demo complete!');
console.log('Key points:');
console.log('  • Each project has isolated memory context');
console.log('  • Searches are scoped by project');
console.log('  • Cross-project search is available when needed');
console.log('  • Use metadata for categorization and filtering');
