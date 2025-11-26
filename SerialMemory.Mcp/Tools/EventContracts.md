# SerialMemory MCP Event Contracts

## Event Sourcing Principles

All memory mutations emit events. Events are:
- **Append-only**: Never modified after creation
- **Immutable**: Contains all data needed to replay state
- **Auditable**: Full history preserved forever
- **Idempotent**: Safe to replay

## Memory Lifecycle Events

### MemoryCreated
```json
{
  "eventType": "MemoryCreated",
  "streamId": "uuid-v7",
  "eventVersion": 1,
  "content": "string",
  "embedding": "float[]",
  "layer": "L0_RAW|L1_CONTEXT|L2_SUMMARY|L3_KNOWLEDGE|L4_HEURISTIC",
  "confidenceScore": 0.0-1.0,
  "halfLifeDays": 30,
  "causalParents": ["uuid[]"],
  "source": "string?",
  "sessionId": "uuid?",
  "userId": "string",
  "tags": ["string[]"],
  "createdAt": "ISO8601",
  "createdBy": "string?",
  "contentHash": "sha256"
}
```

### MemoryUpdated
```json
{
  "eventType": "MemoryUpdated",
  "streamId": "uuid",
  "eventVersion": n,
  "newContent": "string",
  "previousContentHash": "sha256",
  "newEmbedding": "float[]?",
  "reason": "string?",
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryInvalidated (Soft Delete)
```json
{
  "eventType": "MemoryInvalidated",
  "streamId": "uuid",
  "eventVersion": n,
  "reason": "string",
  "supersededById": "uuid?",
  "contradictedByIds": ["uuid[]"],
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryMerged
```json
{
  "eventType": "MemoryMerged",
  "streamId": "uuid",
  "eventVersion": n,
  "sourceMemoryIds": ["uuid[]"],
  "mergedContent": "string",
  "mergedEmbedding": "float[]?",
  "mergeStrategy": "string?",
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemorySplit
```json
{
  "eventType": "MemorySplit",
  "streamId": "uuid",
  "eventVersion": n,
  "childMemoryIds": ["uuid[]"],
  "splitStrategy": "string?",
  "reason": "string?",
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryDecayed
```json
{
  "eventType": "MemoryDecayed",
  "streamId": "uuid",
  "eventVersion": n,
  "previousConfidence": 0.0-1.0,
  "newConfidence": 0.0-1.0,
  "daysSinceReinforcement": 0,
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryReinforced
```json
{
  "eventType": "MemoryReinforced",
  "streamId": "uuid",
  "eventVersion": n,
  "previousConfidence": 0.0-1.0,
  "newConfidence": 0.0-1.0,
  "reinforcementSource": "string",
  "validatedByIds": ["uuid[]"],
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryExpired
```json
{
  "eventType": "MemoryExpired",
  "streamId": "uuid",
  "eventVersion": n,
  "expirationPolicy": "string",
  "originalTtlDays": 0,
  "confidenceAtExpiration": 0.0-1.0,
  "accessCountAtExpiration": 0,
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryArchived
```json
{
  "eventType": "MemoryArchived",
  "streamId": "uuid",
  "eventVersion": n,
  "reason": "string",
  "confidenceAtArchive": 0.0-1.0,
  "accessCountAtArchive": 0,
  "daysSinceLastAccess": 0,
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryRecalled
```json
{
  "eventType": "MemoryRecalled",
  "streamId": "uuid",
  "eventVersion": n,
  "query": "string?",
  "similarityScore": 0.0-1.0,
  "recallContext": "string?",
  "sessionId": "uuid?",
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryIgnored
```json
{
  "eventType": "MemoryIgnored",
  "streamId": "uuid",
  "eventVersion": n,
  "query": "string?",
  "reason": "string",
  "sessionId": "uuid?",
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

### MemoryContradicted
```json
{
  "eventType": "MemoryContradicted",
  "streamId": "uuid",
  "eventVersion": n,
  "contradictingMemoryIds": ["uuid[]"],
  "contradictionType": "string",
  "detectionMethod": "string?",
  "contradictionConfidence": 0.0-1.0,
  "createdAt": "ISO8601",
  "createdBy": "string?"
}
```

## Safety Events

### ContradictionDetected
```json
{
  "eventType": "ContradictionDetected",
  "streamId": "uuid",
  "memoryAId": "uuid",
  "memoryBId": "uuid",
  "detectionMethod": "string",
  "contradictionScore": 0.0-1.0,
  "details": "string?",
  "createdAt": "ISO8601"
}
```

### HallucinationFlagged
```json
{
  "eventType": "HallucinationFlagged",
  "streamId": "uuid",
  "detectionMethod": "string",
  "confidenceScore": 0.0-1.0,
  "flaggedContent": "string?",
  "reason": "string?",
  "createdAt": "ISO8601"
}
```

### IntegrityCheckFailed
```json
{
  "eventType": "IntegrityCheckFailed",
  "streamId": "uuid",
  "checkType": "string",
  "expectedHash": "sha256",
  "actualHash": "sha256",
  "details": "string?",
  "createdAt": "ISO8601"
}
```

### ExportCompleted
```json
{
  "eventType": "ExportCompleted",
  "streamId": "uuid",
  "exportType": "string",
  "memoriesExported": 0,
  "entitiesExported": 0,
  "relationshipsExported": 0,
  "outputPath": "string?",
  "encrypted": false,
  "compressed": false,
  "createdAt": "ISO8601"
}
```

## MCP Tool Input/Output Contracts

### Memory Lifecycle Tools

| Tool | Input | Output |
|------|-------|--------|
| `memory_update` | `memory_id`, `new_content`, `reason?`, `actor_id?` | Updated memory details |
| `memory_delete` | `memory_id`, `reason`, `superseded_by_id?`, `actor_id?` | Soft delete confirmation |
| `memory_merge` | `source_memory_ids[]`, `merged_content`, `strategy?`, `actor_id?` | New merged memory ID |
| `memory_split` | `memory_id`, `child_contents[]`, `strategy?`, `reason?`, `actor_id?` | Child memory IDs |
| `memory_decay` | `memory_id`, `actor_id?` | Decay calculation result |
| `memory_reinforce` | `memory_id`, `confidence?`, `source?`, `validated_by_ids[]?`, `actor_id?` | Reinforcement result |
| `memory_expire` | `memory_id`, `policy?`, `ttl_days?`, `actor_id?` | Expiration result |

### Observability Tools

| Tool | Input | Output |
|------|-------|--------|
| `memory_trace` | `memory_id`, `include_payloads?` | Event history |
| `memory_lineage` | `memory_id`, `max_depth?`, `direction?` | Causal ancestry tree |
| `memory_explain` | `memory_id` | State explanation |
| `memory_conflicts` | `memory_id?`, `limit?` | Conflict list |

### Safety Tools

| Tool | Input | Output |
|------|-------|--------|
| `detect_contradictions` | `memory_id?`, `similarity_threshold?`, `limit?`, `auto_flag?` | Contradiction list |
| `detect_hallucinations` | `memory_id?`, `confidence_threshold?`, `limit?`, `auto_flag?` | Hallucination list |
| `verify_memory_integrity` | `memory_id?`, `limit?`, `fix_corrupted?` | Integrity report |
| `scan_loops` | `max_depth?`, `limit?` | Cycle detection report |

### Export Tools

| Tool | Input | Output |
|------|-------|--------|
| `export_workspace` | `output_path?`, `include_events?`, `active_only?`, `encrypt?`, `encryption_key?`, `compress?` | Export summary |
| `export_memories` | `output_path?`, `layer?`, `min_confidence?`, `from_date?`, `to_date?`, `limit?`, `format?` | Export summary |
| `export_graph` | `output_path?`, `format?`, `include_isolated?` | Export summary |
| `export_user_profile` | `user_id?`, `output_path?`, `include_interactions?` | Export summary |
