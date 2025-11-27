# Plans and Limits

SerialMemory uses a credit-based system to meter usage. Different plans provide different credit allocations and limits.

## Plans Overview

| Feature | Free | Starter | Pro | Enterprise |
|---------|------|---------|-----|------------|
| Monthly Credits | 100 | 1,000 | 10,000 | Unlimited |
| Rate Limit (RPM) | 10 | 60 | 300 | Custom |
| Rate Limit (RPD) | 100 | 1,000 | 10,000 | Custom |
| Max Memories | 1,000 | 10,000 | 100,000 | Unlimited |
| Max Entities | 500 | 5,000 | 50,000 | Unlimited |
| Max Memory Size | 10KB | 50KB | 100KB | Custom |
| Data Export | Basic | Full | Full | Full |
| Support | Community | Email | Priority | Dedicated |

## Credit Costs

| Operation | Credits |
|-----------|---------|
| `memory_ingest` | 1.0 |
| `memory_search` | 0.5 |
| `memory_update` | 0.5 |
| `memory_delete` | 0.1 |
| `memory_multi_hop_search` | 1.0 |
| `memory_about_user` | 0.1 |
| `set_user_persona` | 0.2 |

## The /tenant/limits Endpoint

SDKs can call `GET /tenant/limits` to get current usage information.

### Response Fields

```json
{
  "plan": "starter",
  "displayName": "Starter Plan",

  "monthlyCredits": 1000,
  "creditsUsed": 450,
  "creditsRemaining": 550,
  "daysUntilReset": 15,
  "cycleResetAt": "2024-02-01T00:00:00Z",

  "rateLimitRpm": 60,
  "rateLimitRpd": 1000,

  "maxMemories": 10000,
  "maxEntities": 5000,
  "maxMemorySizeBytes": 51200,

  "currentMemories": 2500,
  "currentEntities": 1200,

  "warnings": [
    {
      "type": "credits",
      "message": "You have used 75% of your monthly credits",
      "percentUsed": 75,
      "severity": "info"
    }
  ]
}
```

### Field Descriptions

| Field | Description |
|-------|-------------|
| `plan` | Plan identifier (free, starter, pro, enterprise) |
| `displayName` | Human-readable plan name |
| `monthlyCredits` | Total credits allocated per billing cycle |
| `creditsUsed` | Credits consumed this cycle |
| `creditsRemaining` | Credits available until reset |
| `daysUntilReset` | Days until credit counter resets |
| `cycleResetAt` | Timestamp when credits reset |
| `rateLimitRpm` | Requests per minute limit |
| `rateLimitRpd` | Requests per day limit |
| `maxMemories` | Maximum memories allowed (null = unlimited) |
| `maxEntities` | Maximum entities allowed (null = unlimited) |
| `maxMemorySizeBytes` | Maximum size per memory |
| `currentMemories` | Current memory count |
| `currentEntities` | Current entity count |
| `warnings` | Active warnings about approaching limits |

### Warning Severities

| Severity | Threshold | Meaning |
|----------|-----------|---------|
| `info` | 75% | Approaching limit, no action required |
| `warning` | 90% | Near limit, consider upgrading |
| `critical` | 95%+ | At or over limit, requests may fail |

## Rate Limiting

When rate limits are exceeded, the API returns:

- HTTP Status: `429 Too Many Requests`
- Header: `Retry-After: <seconds>`

SDKs automatically:
1. Detect the 429 response
2. Read the `Retry-After` header
3. Wait and retry the request

## Usage Limit Exceeded

When credit limits are exceeded, the API returns:

- HTTP Status: `402 Payment Required`
- Body: `{"error": "usage_limit_exceeded", "message": "..."}`

Options when this occurs:
1. Wait for the billing cycle to reset
2. Upgrade to a higher plan
3. Contact support for additional credits

## SDK Integration

### .NET

```csharp
client.OnUsageWarning += warning =>
{
    if (warning.Severity == "critical")
    {
        // Alert or pause operations
    }
};

var limits = await client.GetLimitsAsync();
if (limits.CreditsRemaining < 50)
{
    // Consider reducing operations
}
```

### Node.js

```typescript
client.onUsageWarning = (warning) => {
  if (warning.severity === 'critical') {
    // Alert or pause operations
  }
};

const limits = await client.getLimits();
if (limits.creditsRemaining < 50) {
  // Consider reducing operations
}
```

## Self-Hosted Mode

When running in self-hosted mode (`SERIALMEMORY_MODE=self-hosted`), limits are:

- **Unlimited credits** - No credit metering
- **No rate limits** - Subject to your infrastructure
- **No hard caps** - Subject to your storage

Self-hosted mode bypasses all usage enforcement, suitable for:
- Development environments
- Private deployments
- Unlimited usage scenarios

## Next Steps

- [Data Lifecycle](./06-data-lifecycle.md)
- [Self-Hosting Guide](./07-self-hosting.md)
