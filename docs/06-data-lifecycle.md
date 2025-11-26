# Data Lifecycle

SerialMemory provides comprehensive data management capabilities including export, deletion, retention policies, and audit logging.

## Data Export

### Full Workspace Export

Export all tenant data including memories, entities, relationships, and optionally events.

**Via API:**
```bash
curl -X POST "http://localhost:5001/tenant/export" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "format": "json",
    "includeEntities": true,
    "includeRelationships": true,
    "activeOnly": true
  }'
```

**Response:**
```json
{
  "exportId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "processing",
  "estimatedCompletionAt": "2024-01-15T10:05:00Z"
}
```

### Export Formats

| Format | Description |
|--------|-------------|
| `json` | Full JSON export with all metadata |
| `csv` | Tabular export (memories only) |
| `graphml` | Knowledge graph in GraphML format |

### Encrypted Export

For sensitive data, enable AES-256 encryption:

```json
{
  "format": "json",
  "encrypt": true,
  "encryptionKey": "your-secret-key-32-chars-minimum"
}
```

## Tenant Deletion

### Requesting Deletion

Tenant owners can request account deletion. This initiates a 30-day grace period.

**Via API:**
```bash
curl -X DELETE "http://localhost:5001/tenant" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"confirmationPhrase": "DELETE MY ACCOUNT"}'
```

### Deletion Timeline

1. **Day 0**: Deletion requested, tenant marked "pending_deletion"
2. **Days 1-30**: Grace period - can cancel deletion
3. **Day 30**: Permanent deletion begins
   - All memories deleted
   - All entities deleted
   - All relationships deleted
   - API keys revoked
   - User accounts removed

### Canceling Deletion

During the grace period:

```bash
curl -X POST "http://localhost:5001/tenant/deletion/cancel" \
  -H "Authorization: Bearer <token>"
```

## Data Retention

### Memory Confidence Decay

Memories have a confidence score that decays over time:

```
confidence = initial_confidence * 0.5^(days / half_life)
```

Default half-life: 90 days

Memories below threshold (default 0.1) are candidates for archival.

### Memory Layers

| Layer | Description | Typical Retention |
|-------|-------------|-------------------|
| `L0_RAW` | Raw input data | 30 days |
| `L1_CONTEXT` | Contextual understanding | 90 days |
| `L2_SUMMARY` | Summarized information | 180 days |
| `L3_KNOWLEDGE` | Extracted knowledge | 365 days |
| `L4_HEURISTIC` | Learned patterns | Indefinite |

### Archival

Low-confidence, low-access memories are archived:

- Moved to cold storage
- Not included in search results
- Can be restored if needed

## Admin Audit Log

All administrative actions are logged in a tamper-evident hash chain.

### Logged Actions

| Action | Description |
|--------|-------------|
| `tenant_created` | New tenant signup |
| `api_key_created` | API key generated |
| `api_key_revoked` | API key revoked |
| `data_exported` | Export requested |
| `deletion_requested` | Tenant deletion initiated |
| `deletion_cancelled` | Deletion cancelled |
| `settings_changed` | Tenant settings modified |

### Hash Chain Integrity

Each audit log entry includes:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440001",
  "actionType": "api_key_created",
  "tenantId": "550e8400-e29b-41d4-a716-446655440000",
  "actorId": "admin@example.com",
  "timestamp": "2024-01-15T10:00:00Z",
  "details": { "keyId": "...", "keyName": "Production" },
  "previousHash": "abc123...",
  "currentHash": "def456..."
}
```

The hash chain ensures:
- No entries can be deleted
- No entries can be modified
- Tampering is detectable

### Verifying Integrity

```bash
curl "http://localhost:5001/admin/audit/verify" \
  -H "Authorization: Bearer <admin-token>"
```

Returns:
```json
{
  "status": "valid",
  "entriesChecked": 1523,
  "lastVerifiedAt": "2024-01-15T10:00:00Z"
}
```

## Event Sourcing

All memory mutations are stored as immutable events:

| Event | Description |
|-------|-------------|
| `MemoryCreated` | New memory added |
| `MemoryUpdated` | Content modified |
| `MemoryMerged` | Memories combined |
| `MemoryInvalidated` | Memory soft-deleted |
| `MemoryDecayed` | Confidence decreased |
| `MemoryReinforced` | Memory validated |
| `MemoryArchived` | Moved to cold storage |

### Event Replay

Events can be replayed to reconstruct state at any point in time:

```bash
# Export events for replay
curl "http://localhost:5001/tenant/export" \
  -H "Authorization: Bearer <token>" \
  -d '{"includeEvents": true}'
```

## GDPR Compliance

SerialMemory supports GDPR compliance through:

1. **Data Portability**: Full export in machine-readable format
2. **Right to Erasure**: Tenant deletion with 30-day grace period
3. **Data Minimization**: Confidence decay reduces stale data
4. **Audit Trail**: Complete log of data access and modifications
5. **Consent**: API key authentication represents consent

## Next Steps

- [Self-Hosting Guide](./07-self-hosting.md)
