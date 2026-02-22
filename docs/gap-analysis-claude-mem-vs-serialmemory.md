# Gap Analysis: claude-mem vs SerialMemory

**Date:** 2026-02-21
**Purpose:** Identify Claude-specific memory features from [claude-mem](https://github.com/thedotmack/claude-mem) that could be added to SerialMemory to improve Claude Code integration.

---

## Executive Summary

claude-mem is a Claude Code plugin purpose-built for **automatic, invisible session capture** with AI-powered compression and progressive context injection. SerialMemory is a **full knowledge graph** with event sourcing, entity extraction, multi-hop reasoning, and confidence decay — far more powerful as a memory backend, but missing several Claude Code-specific ergonomic features that claude-mem gets right.

The key insight: **claude-mem excels at the "zero-effort capture" developer experience**, while SerialMemory excels at **deep memory management, reasoning, and integrity**. The features to adopt are primarily in the UX/integration layer, not the storage layer.

---

## Feature Comparison Matrix

| Feature | claude-mem | SerialMemory | Gap? |
|---------|-----------|--------------|------|
| **Storage Backend** | SQLite + Chroma | PostgreSQL + pgvector | No gap — SM is superior |
| **Semantic Search** | Chroma vector DB | pgvector cosine similarity | No gap |
| **Full-text Search** | SQLite FTS5 | PostgreSQL tsvector | No gap |
| **Hybrid Search** | Yes (Chroma + FTS5) | Yes (vector + text + combined) | No gap |
| **Entity Extraction** | No | Pattern + NER | SM ahead |
| **Knowledge Graph** | No | Full graph w/ relationships | SM ahead |
| **Multi-hop Reasoning** | No | Yes (graph traversal) | SM ahead |
| **Confidence Decay** | No | Exponential half-life | SM ahead |
| **Event Sourcing** | No | 13 event types, append-only | SM ahead |
| **Memory Lifecycle** | No | Update/merge/split/decay/expire/supersede | SM ahead |
| **Memory Integrity** | No | SHA-256 content hashing | SM ahead |
| **Contradiction Detection** | No | Yes | SM ahead |
| **Export Formats** | No | JSON/CSV/GraphML/Markdown | SM ahead |
| **Auto-Capture via Hooks** | Yes — mature, 5 lifecycle events | Basic (JSONL drain) | **GAP** |
| **AI Summarization** | Yes — Claude Agent SDK | Yes — LLM-based | Partial parity |
| **Progressive Disclosure** | 3-layer search → timeline → fetch | No — returns full results | **GAP** |
| **Token Budget Tracking** | Yes — token economics per result | No | **GAP** |
| **Session Context Injection** | Auto-injects via SessionStart hook | Manual (instantiate_context) | **GAP** |
| **Observation Types** | Structured (title/subtitle/facts/narrative/concepts) | Free-text content | **GAP** |
| **File Tracking** | files_read / files_modified per observation | Entity extraction only | **GAP** |
| **Privacy Tags** | `<private>` content exclusion | No | **GAP** |
| **Timeline View** | Chronological with anchoring | No dedicated timeline tool | **GAP** |
| **Discovery Tokens** | Tracks "work tokens" vs "read tokens" | No | **GAP** |
| **Claude Code Plugin** | Native plugin (marketplace) | MCP server (manual config) | **GAP** |
| **Web Viewer UI** | React UI at localhost:37777 | No web UI | **GAP** (lower priority) |
| **Prior Session Messages** | Extracts from Claude transcript files | No | **GAP** |
| **Project Scoping** | Per-project filtering | Workspace scoping | Equivalent |
| **Worker Process** | Background Bun worker on :37777 | In-process (MCP STDIO) | Different arch |
| **Worktree Support** | Multi-project interleaved queries | No | **GAP** (minor) |

---

## Detailed Gap Analysis

### GAP 1: Automatic Session Capture via Claude Code Hooks (HIGH PRIORITY)

**What claude-mem does:** Registers 5 lifecycle hooks (SessionStart, UserPromptSubmit, PostToolUse, Stop, SessionEnd) that **automatically** capture tool usage, file edits, and session activity without any manual effort from the user or the AI agent. The hooks run as shell scripts and write observation data to the worker service.

**What SerialMemory has:** `AutoCaptureTools.cs` reads JSONL files from `~/.cc-serialmemory/sessions/` and a `session-capture.sh` hook exists, but:
- The JSONL capture is minimal (timestamp, tool, file, result)
- No automatic PostToolUse capture of rich observation data
- No automatic SessionStart context injection
- No automatic SessionEnd summarization trigger
- Hook integration is documented but not bundled as a ready-to-install package

**Recommendation:** Create a complete set of hook scripts that:
1. **SessionStart**: Auto-run `instantiate_context` and inject recent memories
2. **PostToolUse**: Capture structured observations (tool name, files touched, result summary)
3. **Stop**: Auto-drain captures and trigger `summarize_session`
4. **SessionEnd**: Final drain + summary persistence
5. Package as installable hook scripts (not just documentation)

**Implementation approach:**
- Create `hooks/` directory with shell scripts for each lifecycle event
- Each hook calls the SerialMemory MCP worker/API or writes to the JSONL log
- Add an `install-hooks` script that copies to `~/.claude/settings.json`
- Enhance `AutoCaptureTools` to capture richer structured data (see GAP 5)

---

### GAP 2: Progressive Disclosure / Token-Efficient Search (HIGH PRIORITY)

**What claude-mem does:** Implements a deliberate 3-layer workflow:
1. **`search`** returns a compact index (~50-100 tokens per result): just IDs, timestamps, type, title
2. **`timeline`** shows chronological context around a specific result
3. **`get_observations`** fetches full details only for selected IDs

This achieves ~10x token savings by filtering before fetching. The `__IMPORTANT` tool documents this pattern for Claude.

**What SerialMemory has:** `memory_search` returns full memory content in every result. No way to get a compact index first, then drill down. This means every search consumes significant tokens even when most results are irrelevant.

**Recommendation:** Add three new tools:
1. **`memory_search_index`** — Returns compact results: `{id, created_at, memory_type, title/first_line, similarity_score, entity_count}` (~50-80 tokens per result)
2. **`memory_timeline`** — Given an anchor memory ID or timestamp, return N memories before and after in chronological order (compact format)
3. **`memory_fetch`** — Batch-fetch full details by memory IDs (like `get_observations`)

**Keep existing `memory_search`** for backward compatibility — it remains useful when you know you want full content.

**Implementation approach:**
- `memory_search_index`: Same search logic but project only minimal fields
- `memory_timeline`: New query `SELECT * FROM memories WHERE workspace_id = ? ORDER BY created_at, LIMIT ? OFFSET ?` with anchor-based windowing
- `memory_fetch`: Simple `SELECT * FROM memories WHERE id = ANY(?)` batch fetch

---

### GAP 3: Automatic Context Injection at SessionStart (HIGH PRIORITY)

**What claude-mem does:** On SessionStart, automatically:
1. Queries recent observations and summaries for the current project
2. Builds a "context" document with sections: header, timeline, summary, "Previously" (prior session messages)
3. Injects this context into the session so Claude starts with full awareness

The `ContextBuilder` assembles multiple sections, and `ObservationCompiler` merges observations with session summaries chronologically.

**What SerialMemory has:** `instantiate_context` tool exists and returns recent memories + goals + user persona, but:
- Must be explicitly called by the agent (not automatic)
- Doesn't include prior session transcript context
- No "Previously" section from Claude transcript files
- No configurable token budget for context injection

**Recommendation:**
1. Enhance `instantiate_context` output to include a structured context document:
   - Recent session summary (from last `session_summary` memory)
   - Active goals
   - User persona excerpt
   - Recent key memories (last N, filtered by type)
   - Files recently modified (from auto-capture data)
2. Create a SessionStart hook that auto-calls `instantiate_context` and injects the response
3. Add a configurable token budget that truncates context to fit
4. Optionally: parse Claude Code transcript files for "Previously" context

---

### GAP 4: Token Budget Tracking (MEDIUM PRIORITY)

**What claude-mem does:** Tracks "discovery tokens" (how many tokens of work were done to find/create information) vs "read tokens" (how many tokens are consumed displaying it). Shows savings percentage. This helps users understand the cost of memory operations.

**What SerialMemory has:** No token tracking whatsoever.

**Recommendation:**
1. Add `estimated_tokens` to search results (character count / 4 as rough estimate)
2. Track token cost in tool responses: `{results: [...], meta: {result_count, total_tokens, avg_tokens_per_result}}`
3. For the progressive disclosure tools (GAP 2), show savings: "Index: 150 tokens | Full fetch would be: 1,500 tokens | Savings: 90%"

**Implementation approach:**
- Simple character-count estimation (chars / 4)
- Add `meta` field to all search/fetch tool responses
- No external token counter needed

---

### GAP 5: Structured Observation Format (MEDIUM PRIORITY)

**What claude-mem does:** Each observation has rich structure:
- `type` (bugfix, feature, decision, etc.)
- `title` and `subtitle`
- `facts` (array of factual points)
- `narrative` (extended description)
- `concepts` (categorization tags)
- `files_read` and `files_modified` (file tracking)
- `prompt_number` (which prompt in the session)
- `discovery_tokens` (token cost)

**What SerialMemory has:** Memories have `content` (free text), `source`, `memory_type`, and metadata JSON. No structured facts/narrative/concepts fields. File tracking comes only from entity extraction.

**Recommendation:** Enhance the memory data model with optional structured fields:
1. Add `title` column (or extract from first line of content)
2. Add `facts` JSONB column for structured factual points
3. Add `concepts` JSONB column for categorization tags (distinct from entities)
4. Add `files_read` and `files_modified` JSONB columns
5. Enhance `memory_ingest` to accept these structured fields optionally
6. Use these fields for more precise search filtering

**Implementation approach:**
- Add columns to `memories` table (nullable, backward compatible)
- Extend `memory_ingest` schema with optional structured params
- Auto-populate from auto-capture data
- Use concepts for lightweight tagging (faster than entity extraction)

---

### GAP 6: Privacy Tags (MEDIUM PRIORITY)

**What claude-mem does:** Supports `<private>content</private>` tags. Content within these tags is stripped at the hook layer (edge processing) before reaching the database. This gives users control over what gets stored.

**What SerialMemory has:** No privacy mechanism. Everything passed to `memory_ingest` is stored.

**Recommendation:**
1. Add `<private>` tag stripping in the `memory_ingest` pipeline
2. Strip before embedding generation and storage
3. Implement at the KnowledgeGraphService level (not just MCP layer) so it works for all ingestion paths
4. Optionally: add a `<sensitive>` tag that stores content but excludes from search results (different from `<private>` which excludes from storage entirely)

**Implementation approach:**
- Regex strip `<private>.*?</private>` from content before processing
- Add to `KnowledgeGraphService.IngestMemoryAsync()`
- Log stripped content count for debugging (not the content itself)

---

### GAP 7: Timeline / Chronological Navigation (MEDIUM PRIORITY)

**What claude-mem does:** The `timeline` tool lets you anchor on a specific observation and see N items before and after it chronologically. Supports depth_before/depth_after parameters. This enables "time travel" through session history.

**What SerialMemory has:** No dedicated timeline navigation. Search returns results by relevance score, not chronology.

**Recommendation:** Add a `memory_timeline` tool:
- Input: `anchor_id` (memory ID) or `anchor_time` (ISO timestamp), `depth_before`, `depth_after`, optional `project`/`memory_type` filter
- Output: Chronologically ordered memories around the anchor point
- Use compact format (from GAP 2) by default, with option for full content

**Implementation approach:**
- Query: `SELECT * FROM memories WHERE created_at <= anchor ORDER BY created_at DESC LIMIT depth_before` UNION `SELECT * FROM memories WHERE created_at > anchor ORDER BY created_at ASC LIMIT depth_after`
- Add to core tools alongside `memory_search`

---

### GAP 8: Claude Code Plugin Packaging (LOW PRIORITY — FUTURE)

**What claude-mem does:** Distributed as a Claude Code plugin via marketplace (`/plugin marketplace add thedotmack/claude-mem`). Includes plugin.json manifest, hooks configuration, skills definition. One-command install.

**What SerialMemory has:** MCP server requiring manual `claude_desktop_config.json` editing. No plugin packaging.

**Recommendation:** Investigate Claude Code plugin format when it stabilizes:
1. Create `.claude-plugin/plugin.json` manifest
2. Bundle hooks as plugin hooks (not user-managed)
3. Create a `mem-search` skill equivalent
4. Enable marketplace distribution

**Note:** This is lower priority because the plugin marketplace is relatively new and the MCP approach works well. Revisit when plugin ecosystem matures.

---

### GAP 9: Prior Session Transcript Integration (LOW PRIORITY)

**What claude-mem does:** The `ContextBuilder` reads Claude Code transcript files from the filesystem to extract prior assistant messages and inject them as a "Previously" section in context. This provides direct session continuity.

**What SerialMemory has:** Session continuity through explicit `memory_ingest` + `instantiate_context`, not transcript parsing.

**Recommendation:** Consider adding optional transcript integration:
1. Read Claude Code session transcripts from `~/.claude/projects/` directory
2. Extract key assistant messages from the most recent session
3. Include as a "Previously" section in `instantiate_context` output
4. Make this opt-in (privacy considerations)

**Note:** This creates coupling to Claude Code's internal file format, which may change. SerialMemory's approach of explicit memory ingestion is more robust long-term. Only implement if transcript format stabilizes.

---

### GAP 10: Web Viewer UI (LOW PRIORITY)

**What claude-mem does:** Ships a React-based viewer accessible at `http://localhost:37777` with SSE-based real-time updates. Shows observation timeline, search interface, and session history.

**What SerialMemory has:** No web UI. API server exists (`SerialMemory.Api`) with SignalR but no frontend.

**Recommendation:** This is nice-to-have but not critical for Claude Code integration. If pursued:
1. Add a simple web UI to `SerialMemory.Api`
2. Memory timeline visualization
3. Knowledge graph explorer
4. Search interface
5. Session history viewer

---

## Implementation Priority Matrix

| Priority | Gap | Effort | Impact |
|----------|-----|--------|--------|
| **P0** | GAP 1: Auto-capture hooks | Medium | Removes manual effort entirely |
| **P0** | GAP 2: Progressive disclosure | Medium | ~10x token savings |
| **P0** | GAP 3: Auto context injection | Low | Automatic session awareness |
| **P1** | GAP 5: Structured observations | Medium | Better search/filtering |
| **P1** | GAP 7: Timeline navigation | Low | Chronological exploration |
| **P1** | GAP 4: Token budget tracking | Low | Cost visibility |
| **P2** | GAP 6: Privacy tags | Low | User trust |
| **P2** | GAP 8: Plugin packaging | High | Distribution ease |
| **P3** | GAP 9: Transcript integration | Medium | Direct continuity |
| **P3** | GAP 10: Web viewer | High | Visualization |

---

## What SerialMemory Does Better (No Action Needed)

These are areas where SerialMemory is ahead and should be preserved:

1. **Knowledge Graph** — Entity extraction, relationships, multi-hop traversal. claude-mem has nothing comparable.
2. **Event Sourcing** — Full audit trail with 13 event types. Append-only, immutable.
3. **Memory Lifecycle** — Merge, split, decay, reinforce, expire, supersede. claude-mem only has create/read.
4. **Confidence Decay** — Exponential half-life model with reinforcement. claude-mem memories are static.
5. **Contradiction Detection** — Automatic semantic conflict detection.
6. **Memory Integrity** — SHA-256 hash verification on read.
7. **Multi-axis Retrieval** — 6 scoring factors (semantic, recency, confidence, affinity, directive match, contradiction penalty).
8. **Export System** — JSON, CSV, GraphML, Cytoscape, Obsidian Markdown.
9. **Workspace Isolation** — Full RLS-based workspace scoping.
10. **Engineering Reasoning** — Domain-specific analysis (power integrity, signal integrity, dependency analysis).
11. **Goals System** — Persistent goals across sessions.
12. **State Snapshots** — Checkpoint and restore workspace state.

---

## Recommended Implementation Order

### Phase 1: Token Efficiency (P0)
1. Add `memory_search_index` tool (compact index results)
2. Add `memory_timeline` tool (chronological navigation)
3. Add `memory_fetch` tool (batch fetch by IDs)
4. Add token estimation to all search results

### Phase 2: Automatic Capture (P0)
1. Create hook scripts: SessionStart, PostToolUse, Stop, SessionEnd
2. Enhance auto-capture to write structured observations
3. Auto-call `instantiate_context` on SessionStart
4. Auto-drain + summarize on SessionEnd
5. Package as installable hook set

### Phase 3: Data Model Enrichment (P1)
1. Add structured fields to memories (title, facts, concepts, files)
2. Enhance `memory_ingest` to accept structured input
3. Add `<private>` tag stripping
4. Use concepts for lightweight filtering

### Phase 4: Polish (P2-P3)
1. Plugin packaging investigation
2. Transcript integration (opt-in)
3. Web viewer (if demand warrants)

---

## Architecture Notes

### Key Difference in Philosophy

- **claude-mem**: Capture everything automatically, compress with AI, inject transparently. User/agent does nothing.
- **SerialMemory**: Rich memory graph with full lifecycle management. Agent explicitly manages memories.

**The ideal hybrid**: SerialMemory's powerful backend + claude-mem's zero-effort capture UX. Automatic capture feeds the knowledge graph, while explicit tools allow deep management when needed.

### Integration Points

SerialMemory's existing architecture supports all these additions:
- **MCP tools**: Add new tools alongside existing ones
- **KnowledgeGraphService**: Extend with structured ingestion
- **PostgreSQL**: Add columns (backward compatible)
- **Hooks**: Shell scripts + MCP calls (no architecture change)
- **ToolHierarchy**: Add new categories as needed

No fundamental architecture changes are required. All gaps can be addressed as additive features.
