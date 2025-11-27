// =============================================================================
// SerialMemory SDK Example: Project Context Memory (Node.js)
// =============================================================================
// This example demonstrates how to use SerialMemory for project-specific
// context isolation. It shows:
//
// 1. Using metadata to associate memories with specific projects
// 2. Filtering searches by project context
// 3. Multi-tenant memory isolation patterns
// 4. Project-scoped knowledge graphs
//
// Use case: A development tool that maintains separate memory contexts
// for different projects/repositories the user is working on.
//
// Run with: npm start
// =============================================================================

import { SerialMemoryClient } from '@serialmemory/client';

// -----------------------------------------------------------------------------
// Project Memory Manager - Wrapper for project-scoped memory operations
// -----------------------------------------------------------------------------

/**
 * Manages project-scoped memories using SerialMemory.
 * Each project gets isolated memory context through metadata filtering.
 */
class ProjectMemoryManager {
  constructor(client, projectId, projectName) {
    this.client = client;
    this.projectId = projectId;
    this.projectName = projectName;
  }

  /**
   * Store a memory scoped to this project.
   */
  async store(content, category, extraMetadata = {}) {
    const metadata = {
      projectId: this.projectId,
      projectName: this.projectName,
      category,
      timestamp: new Date().toISOString(),
      ...extraMetadata
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

  /**
   * Search memories within this project's context.
   */
  async search(query, limit = 5) {
    // Search with project context in the query for better relevance
    const contextualQuery = `${this.projectName} ${query}`;

    const results = await this.client.search(contextualQuery, {
      mode: 'hybrid',
      limit: limit * 2, // Fetch extra to filter
      threshold: 0.4
    });

    // Filter to only this project's memories
    // In production, you'd use server-side filtering with metadata queries
    return results.memories
      .filter(m =>
        m.source === `project-${this.projectId}` ||
        m.content.startsWith(`[${this.projectName}]`)
      )
      .slice(0, limit);
  }

  /**
   * Store an architecture decision record (ADR).
   */
  async storeDecision(title, context, decision, consequences) {
    const content = `Architecture Decision: ${title}

Context:
${context}

Decision:
${decision}

Consequences:
${consequences}`;

    return this.store(content, 'architecture-decision', { adrTitle: title });
  }

  /**
   * Store a bug fix record for future reference.
   */
  async storeBugFix(issue, rootCause, solution, affectedFiles) {
    const content = `Bug Fix: ${issue}

Root Cause:
${rootCause}

Solution:
${solution}

Affected Files:
${affectedFiles.map(f => `- ${f}`).join('\n')}`;

    return this.store(content, 'bug-fix', { affectedFiles });
  }

  /**
   * Store a code pattern or convention.
   */
  async storePattern(name, description, example) {
    const content = `Code Pattern: ${name}

Description:
${description}

Example:
\`\`\`
${example}
\`\`\``;

    return this.store(content, 'code-pattern', { patternName: name });
  }
}

// -----------------------------------------------------------------------------
// Demo Application
// -----------------------------------------------------------------------------

const client = new SerialMemoryClient({
  baseUrl: process.env.SERIALMEMORY_URL || 'http://localhost:5000',
  apiKey: process.env.SERIALMEMORY_API_KEY || 'demo-key',
  defaultSource: 'project-memory-demo'
});

console.log('🗂️ SerialMemory Project Context Memory Demo');
console.log('============================================\n');

// -----------------------------------------------------------------------------
// Simulate Two Different Projects
// -----------------------------------------------------------------------------

// Project A: E-commerce Backend
const ecommerceProject = new ProjectMemoryManager(
  client,
  'ecommerce-api',
  'E-commerce Backend'
);

// Project B: Mobile App
const mobileProject = new ProjectMemoryManager(
  client,
  'mobile-app',
  'Mobile App'
);

console.log('📦 Setting up two projects with isolated memory contexts...\n');

// -----------------------------------------------------------------------------
// Store E-commerce Project Memories
// -----------------------------------------------------------------------------
console.log('Project: E-commerce Backend');
console.log('----------------------------');

await ecommerceProject.storeDecision(
  'Use Stripe for payments',
  'Need a payment processor that supports multiple currencies and subscription billing.',
  'Adopt Stripe as our payment processor with their Node.js SDK.',
  'Requires PCI compliance review. 2.9% + $0.30 per transaction.'
);
console.log('✅ Stored ADR: Payment processor selection');

await ecommerceProject.storeBugFix(
  'Cart totals incorrect with percentage discounts',
  'Discount calculation was using integer division, losing decimal precision.',
  'Changed to proper decimal arithmetic and added rounding at the final step only.',
  ['services/cart-service.js', 'models/cart-item.js']
);
console.log('✅ Stored bug fix: Cart calculation issue');

await ecommerceProject.storePattern(
  'Repository Pattern',
  'All database access goes through repository classes. No direct Prisma client usage in services.',
  `class OrderService {
  constructor(orderRepository) {
    this.orders = orderRepository;
  }

  async getById(id) {
    return this.orders.findById(id);
  }
}`
);
console.log('✅ Stored code pattern: Repository Pattern\n');

// -----------------------------------------------------------------------------
// Store Mobile App Project Memories
// -----------------------------------------------------------------------------
console.log('Project: Mobile App');
console.log('--------------------');

await mobileProject.storeDecision(
  'Use React Native',
  'Need cross-platform mobile support with a shared codebase.',
  'Use React Native with TypeScript for iOS and Android development.',
  'Team needs React Native training. Some native modules may require platform-specific code.'
);
console.log('✅ Stored ADR: Framework selection');

await mobileProject.storeBugFix(
  'App crashes on Android when switching tabs rapidly',
  'Race condition in navigation state updates. Multiple screens mounting simultaneously.',
  'Added debouncing to tab switches and proper cleanup in useEffect hooks.',
  ['src/navigation/TabNavigator.tsx', 'src/hooks/useTabSwitch.ts']
);
console.log('✅ Stored bug fix: Tab navigation crash');

await mobileProject.storePattern(
  'Custom Hook Pattern',
  "Extract reusable logic into custom hooks prefixed with 'use'. Keep components thin.",
  `// Bad: Logic in component
const MyComponent = () => {
  const [data, setData] = useState(null);
  useEffect(() => { fetch()... }, []);
  return <View>...</View>;
};

// Good: Logic in hook
const useMyData = () => {
  const [data, setData] = useState(null);
  useEffect(() => { fetch()... }, []);
  return data;
};`
);
console.log('✅ Stored code pattern: Custom Hook Pattern\n');

// -----------------------------------------------------------------------------
// Demonstrate Project-Scoped Search
// -----------------------------------------------------------------------------
console.log('🔍 Demonstrating project-scoped search...\n');

// Search within E-commerce project
console.log("Query: 'payment' in E-commerce Backend:");
const ecommerceResults = await ecommerceProject.search('payment', 3);
for (const memory of ecommerceResults) {
  const preview = memory.content.split('\n')[0];
  console.log(`   [${(memory.similarity * 100).toFixed(0)}%] ${preview}`);
}

console.log();

// Same query in Mobile App project - should return different/no results
console.log("Query: 'payment' in Mobile App:");
const mobileResults = await mobileProject.search('payment', 3);
if (mobileResults.length === 0) {
  console.log('   (No results - payment decisions are in E-commerce project)');
} else {
  for (const memory of mobileResults) {
    const preview = memory.content.split('\n')[0];
    console.log(`   [${(memory.similarity * 100).toFixed(0)}%] ${preview}`);
  }
}

console.log();

// Search for bug fixes in Mobile App
console.log("Query: 'crash navigation bug' in Mobile App:");
const bugResults = await mobileProject.search('crash navigation bug', 3);
for (const memory of bugResults) {
  const preview = memory.content.split('\n')[0];
  console.log(`   [${(memory.similarity * 100).toFixed(0)}%] ${preview}`);
}

// -----------------------------------------------------------------------------
// Cross-Project Search (Global Context)
// -----------------------------------------------------------------------------
console.log('\n🌐 Cross-project search (all projects)...\n');

// Direct client search without project filtering
const globalResults = await client.search('code patterns best practices', {
  mode: 'hybrid',
  limit: 5
});

console.log("Query: 'code patterns best practices' (all projects):");
for (const memory of globalResults.memories) {
  const projectTag = memory.content.startsWith('[')
    ? memory.content.split(']')[0] + ']'
    : `(${memory.source})`;
  const preview = memory.content.replace(projectTag, '').trim().split('\n')[0];
  console.log(`   ${projectTag} [${(memory.similarity * 100).toFixed(0)}%] ${preview}`);
}

// -----------------------------------------------------------------------------
// Summary
// -----------------------------------------------------------------------------
console.log('\n📊 Project Memory Summary');
console.log('-------------------------');

const stats = await client.getGraphStatistics();
console.log(`Total memories across all projects: ${stats.totalMemories}`);
console.log(`Total entities extracted: ${stats.totalEntities}`);
console.log(`Total relationships discovered: ${stats.totalRelationships}`);

console.log('\n✨ Demo complete!');
console.log('Key takeaways:');
console.log('  • Use metadata (projectId) to scope memories to projects');
console.log('  • Prefix content with project name for better search relevance');
console.log('  • Use source field for filtering at query time');
console.log('  • Cross-project search is still possible when needed');
