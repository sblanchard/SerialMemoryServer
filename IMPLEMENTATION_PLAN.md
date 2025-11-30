# Implementation Plan: Billing Pack v2 & Memory Layer Worker

## Overview

This plan covers two major feature sets:
1. **Billing Pack v2** - Self-service UI, Usage forecasting, Flexible billing cycles, Advanced metering
2. **Memory Layer Worker** - Automatic layer promotion, Summarization, Knowledge extraction, Heuristic learning

---

## Part 1: Billing Pack v2

### 1.1 Self-Service UI

#### New Dashboard Pages

**A. Plan Management Page (`/dashboard/plans`)**
- Display all available plans with feature comparison matrix
- Show current plan with highlight
- One-click upgrade/downgrade buttons
- Plan change preview (prorated costs, effective date)
- Pending change indicator and cancellation option

**B. Payment Methods Page (`/dashboard/payment-methods`)**
- List all saved payment methods (cards, bank accounts)
- Add new payment method via Stripe Elements
- Set default payment method
- Remove payment methods
- Card expiration warnings

**C. Usage Analytics Page (`/dashboard/usage-analytics`)**
- Interactive charts (daily, weekly, monthly views)
- Breakdown by operation type (pie chart)
- Cost per operation category
- Peak usage times (heatmap)
- Comparison with previous periods
- Export to CSV/PDF

**D. Invoices Page (`/dashboard/invoices`)**
- List all invoices with status badges
- Download PDF invoices
- Payment retry for failed invoices
- Invoice detail modal

#### Files to Create
```
SerialMemory.Web/Pages/Dashboard/
├── Plans.cshtml + Plans.cshtml.cs
├── PaymentMethods.cshtml + PaymentMethods.cshtml.cs
├── UsageAnalytics.cshtml + UsageAnalytics.cshtml.cs
└── Invoices.cshtml + Invoices.cshtml.cs
```

#### API Endpoints to Add
```
GET  /api/billing/plans              - List available plans with features
POST /api/billing/plan/change        - Request plan change (upgrade/downgrade)
GET  /api/billing/plan/preview       - Preview plan change costs
DELETE /api/billing/plan/pending     - Cancel pending plan change
GET  /api/billing/payment-methods    - List payment methods
POST /api/billing/payment-method     - Add payment method (via Stripe SetupIntent)
DELETE /api/billing/payment-method/{id} - Remove payment method
PUT  /api/billing/payment-method/{id}/default - Set as default
GET  /api/billing/invoices           - List all invoices
GET  /api/billing/invoices/{id}/pdf  - Download invoice PDF
POST /api/billing/invoices/{id}/retry - Retry failed payment
```

### 1.2 Usage Forecasting

#### ML-Based Cost Prediction

**A. Forecasting Service**
- Time-series analysis of historical usage
- Linear regression for trend prediction
- Seasonal decomposition (daily, weekly patterns)
- Anomaly detection for unusual spikes

**B. Cost Recommendations**
- Identify expensive operations
- Suggest caching strategies
- Recommend batch operations
- Plan upgrade/downgrade suggestions based on usage patterns

#### Files to Create
```
SerialMemory.Infrastructure/Billing/
├── UsageForecastingService.cs
├── CostRecommendationService.cs
└── Models/
    ├── UsageForecast.cs
    ├── CostRecommendation.cs
    └── UsagePattern.cs
```

#### API Endpoints
```
GET /api/billing/forecast           - Get usage forecast (7/30/90 days)
GET /api/billing/recommendations    - Get cost optimization recommendations
GET /api/billing/patterns           - Get usage patterns analysis
```

### 1.3 Flexible Billing Cycles

#### Annual/Quarterly Billing

**A. Database Schema Changes**
```sql
ALTER TABLE tenant_plans ADD COLUMN billing_interval VARCHAR(20) DEFAULT 'monthly';
ALTER TABLE tenant_plans ADD COLUMN annual_discount_percent DECIMAL(5,2) DEFAULT 20.00;
ALTER TABLE tenant_plans ADD COLUMN quarterly_discount_percent DECIMAL(5,2) DEFAULT 10.00;

ALTER TABLE tenant_subscriptions ADD COLUMN billing_interval VARCHAR(20) DEFAULT 'monthly';
ALTER TABLE tenant_subscriptions ADD COLUMN next_billing_date TIMESTAMPTZ;
```

**B. Stripe Price Mapping**
- Create annual/quarterly price IDs in Stripe
- Map plan + interval to Stripe price ID
- Handle prorated upgrades/downgrades

**C. UI Changes**
- Billing interval toggle (Monthly/Quarterly/Annual)
- Show savings percentage for longer terms
- Calculate and display prorated amounts

### 1.4 Advanced Metering

#### Detailed Usage Tracking

**A. Enhanced Usage Events**
- Add category field (core, analysis, export, power)
- Add feature tags for granular tracking
- Add request metadata (memory size, entity count)

**B. Cost Breakdown Reports**
```sql
CREATE TABLE usage_cost_breakdown (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    period_start TIMESTAMPTZ NOT NULL,
    period_end TIMESTAMPTZ NOT NULL,
    category VARCHAR(50) NOT NULL,
    operation_type VARCHAR(50) NOT NULL,
    operation_count BIGINT NOT NULL,
    total_credits DECIMAL(15,4) NOT NULL,
    avg_latency_ms DECIMAL(10,2),
    created_at TIMESTAMPTZ DEFAULT NOW()
);
```

**C. Usage Reports**
- Daily/weekly/monthly rollup jobs
- Cost by category pie charts
- Trend analysis per operation
- Exportable reports (CSV, PDF, JSON)

#### Files to Create
```
SerialMemory.Infrastructure/Billing/
├── AdvancedMeteringService.cs
├── UsageReportService.cs
├── CostBreakdownService.cs
└── Jobs/
    └── UsageRollupJob.cs
```

---

## Part 2: Memory Layer Worker

### 2.1 Architecture Overview

The Memory Layer Worker is a background service that automatically processes memories through the cognitive layer hierarchy:

```
L0_RAW → L1_CONTEXT → L2_SUMMARY → L3_KNOWLEDGE → L4_HEURISTIC
```

#### Core Components

```
SerialMemory.Infrastructure/MemoryLayer/
├── MemoryLayerWorker.cs              - Main background service
├── LayerPromotionService.cs          - Promotion logic & criteria
├── MemorySummarizationService.cs     - L1→L2 summarization
├── KnowledgeExtractionService.cs     - L2→L3 fact extraction
├── HeuristicLearningService.cs       - L3→L4 pattern learning
├── LayerTransitionQueue.cs           - Bounded channel for async processing
└── Models/
    ├── LayerPromotionCriteria.cs
    ├── SummarizationResult.cs
    ├── ExtractedKnowledge.cs
    └── LearnedHeuristic.cs
```

### 2.2 Auto-Promotion Logic

#### Promotion Criteria by Layer

**L0_RAW → L1_CONTEXT**
- Trigger: Memory has been accessed 2+ times
- Trigger: Memory is 1+ day old
- Trigger: Memory has entities extracted
- Action: Add contextual metadata, link to related memories

**L1_CONTEXT → L2_SUMMARY**
- Trigger: 3+ related L1 memories exist on same topic
- Trigger: Memory cluster detected (embedding similarity > 0.8)
- Action: Generate summary using OpenAI, create new L2 memory

**L2_SUMMARY → L3_KNOWLEDGE**
- Trigger: Summary has been validated (accessed 5+ times)
- Trigger: Summary contains extractable facts
- Action: Extract structured facts via LLM, create L3 memories

**L3_KNOWLEDGE → L4_HEURISTIC**
- Trigger: Pattern detected across 5+ L3 facts
- Trigger: Repeated query patterns suggest rule
- Action: Generate heuristic rule, store as L4 memory

#### LayerPromotionService Implementation

```csharp
public sealed class LayerPromotionService(
    IKnowledgeGraphStore store,
    ILlmService llm,
    ILogger<LayerPromotionService> logger)
{
    public async Task<List<Guid>> FindPromotionCandidatesAsync(
        MemoryLayer fromLayer,
        int limit = 100,
        CancellationToken ct = default);

    public async Task<bool> ShouldPromoteAsync(
        Memory memory,
        MemoryLayer targetLayer,
        CancellationToken ct = default);

    public async Task<Memory?> PromoteAsync(
        Memory memory,
        MemoryLayer targetLayer,
        CancellationToken ct = default);
}
```

### 2.3 Summarization (L1 → L2)

#### Summarization Service

Uses OpenAI to generate concise summaries from related L1 memories:

```csharp
public sealed class MemorySummarizationService(
    ILlmService llm,
    IKnowledgeGraphStore store,
    IEmbeddingService embeddings)
{
    public async Task<SummarizationResult> SummarizeClusterAsync(
        List<Memory> relatedMemories,
        CancellationToken ct = default);
}
```

**LLM Prompt Template:**
```
You are a memory consolidation system. Given the following related memories,
create a concise summary that captures the key information.

Memories:
{memories_content}

Output a JSON object with:
{
  "summary": "Concise summary text",
  "key_points": ["point1", "point2", ...],
  "entities_mentioned": ["entity1", "entity2", ...],
  "confidence": 0.0-1.0
}
```

### 2.4 Knowledge Extraction (L2 → L3)

#### Knowledge Extraction Service

Extracts structured facts from summaries:

```csharp
public sealed class KnowledgeExtractionService(
    ILlmService llm,
    IEntityExtractionService entities)
{
    public async Task<List<ExtractedKnowledge>> ExtractFromSummaryAsync(
        Memory summary,
        CancellationToken ct = default);
}
```

**LLM Prompt Template:**
```
You are a knowledge extraction system. From the following summary,
extract discrete, factual statements that can stand alone.

Summary:
{summary_content}

For each fact, provide:
{
  "facts": [
    {
      "statement": "Discrete factual statement",
      "subject": "Main entity",
      "predicate": "Relationship or property",
      "object": "Target entity or value",
      "confidence": 0.0-1.0,
      "source_context": "Brief context from original"
    }
  ]
}
```

### 2.5 Heuristic Learning (L3 → L4)

#### Heuristic Learning Service

Detects patterns across L3 knowledge and generates rules:

```csharp
public sealed class HeuristicLearningService(
    ILlmService llm,
    IKnowledgeGraphStore store)
{
    public async Task<List<LearnedHeuristic>> DetectPatternsAsync(
        int minSupportingFacts = 5,
        CancellationToken ct = default);

    public async Task<LearnedHeuristic?> GenerateHeuristicAsync(
        List<Memory> supportingFacts,
        CancellationToken ct = default);
}
```

**Pattern Detection Strategies:**
1. **Entity-based patterns**: Same entity appears with same predicate multiple times
2. **Temporal patterns**: Events that consistently follow each other
3. **Causal patterns**: A→B relationships that repeat
4. **Categorical patterns**: Similar entities share common properties

**LLM Prompt Template:**
```
You are a pattern recognition system. Given these related facts,
identify if there's a generalizable rule or heuristic.

Facts:
{facts_list}

If a pattern exists, output:
{
  "has_pattern": true,
  "heuristic": "General rule statement",
  "pattern_type": "entity|temporal|causal|categorical",
  "confidence": 0.0-1.0,
  "exceptions": ["any known exceptions"],
  "supporting_evidence": ["fact indices that support this"]
}
```

### 2.6 Background Worker

#### MemoryLayerWorker

```csharp
public sealed class MemoryLayerWorker : BackgroundService
{
    private readonly TimeSpan _scanInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessLayerPromotionsAsync(stoppingToken);
            await Task.Delay(_scanInterval, stoppingToken);
        }
    }

    private async Task ProcessLayerPromotionsAsync(CancellationToken ct)
    {
        // Phase 1: L0 → L1 (add context)
        await ProcessL0ToL1Async(ct);

        // Phase 2: L1 → L2 (summarization)
        await ProcessL1ToL2Async(ct);

        // Phase 3: L2 → L3 (knowledge extraction)
        await ProcessL2ToL3Async(ct);

        // Phase 4: L3 → L4 (heuristic learning)
        await ProcessL3ToL4Async(ct);
    }
}
```

#### Configuration

```json
{
  "MemoryLayerWorker": {
    "Enabled": true,
    "ScanIntervalMinutes": 15,
    "BatchSize": 50,
    "L0ToL1": {
      "MinAccessCount": 2,
      "MinAgeDays": 1
    },
    "L1ToL2": {
      "MinClusterSize": 3,
      "SimilarityThreshold": 0.8
    },
    "L2ToL3": {
      "MinAccessCount": 5,
      "MinConfidence": 0.7
    },
    "L3ToL4": {
      "MinSupportingFacts": 5,
      "MinPatternConfidence": 0.8
    }
  }
}
```

---

## Implementation Order

### Phase 1: Foundation (Week 1-2)
1. Database schema changes for billing intervals
2. Create LayerPromotionService with basic criteria
3. Add new billing API endpoints
4. Create Plans.cshtml page

### Phase 2: Core Features (Week 3-4)
5. Implement UsageForecastingService
6. Create MemorySummarizationService
7. Add PaymentMethods.cshtml page
8. Implement MemoryLayerWorker background service

### Phase 3: Advanced Features (Week 5-6)
9. Implement KnowledgeExtractionService
10. Create UsageAnalytics.cshtml with charts
11. Implement HeuristicLearningService
12. Add CostRecommendationService

### Phase 4: Polish & Testing (Week 7-8)
13. Create Invoices.cshtml page
14. Add comprehensive tests
15. Performance optimization
16. Documentation

---

## Database Migrations Required

```sql
-- Billing Pack v2
ALTER TABLE tenant_plans ADD COLUMN billing_interval VARCHAR(20) DEFAULT 'monthly';
ALTER TABLE tenant_plans ADD COLUMN annual_discount_percent DECIMAL(5,2) DEFAULT 20.00;
ALTER TABLE tenant_plans ADD COLUMN quarterly_discount_percent DECIMAL(5,2) DEFAULT 10.00;

ALTER TABLE tenant_subscriptions ADD COLUMN billing_interval VARCHAR(20) DEFAULT 'monthly';
ALTER TABLE tenant_subscriptions ADD COLUMN next_billing_date TIMESTAMPTZ;

CREATE TABLE usage_forecasts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    forecast_date DATE NOT NULL,
    predicted_credits DECIMAL(15,4) NOT NULL,
    confidence_interval_low DECIMAL(15,4),
    confidence_interval_high DECIMAL(15,4),
    model_version VARCHAR(50),
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(tenant_id, forecast_date)
);

CREATE TABLE cost_recommendations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id),
    recommendation_type VARCHAR(50) NOT NULL,
    title VARCHAR(200) NOT NULL,
    description TEXT,
    estimated_savings DECIMAL(15,4),
    priority INTEGER DEFAULT 3,
    is_dismissed BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Memory Layer Worker
CREATE TABLE layer_transition_queue (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL REFERENCES memories(id),
    from_layer VARCHAR(20) NOT NULL,
    to_layer VARCHAR(20) NOT NULL,
    status VARCHAR(20) DEFAULT 'pending',
    scheduled_at TIMESTAMPTZ DEFAULT NOW(),
    processed_at TIMESTAMPTZ,
    error_message TEXT
);

CREATE TABLE learned_heuristics (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    memory_id UUID NOT NULL REFERENCES memories(id),
    pattern_type VARCHAR(50) NOT NULL,
    rule_statement TEXT NOT NULL,
    supporting_fact_ids UUID[] NOT NULL,
    confidence REAL NOT NULL,
    exceptions JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_transition_queue_pending ON layer_transition_queue(status, scheduled_at)
    WHERE status = 'pending';
CREATE INDEX idx_heuristics_tenant ON learned_heuristics(tenant_id);
```

---

## Testing Strategy

### Unit Tests
- LayerPromotionService criteria validation
- UsageForecastingService prediction accuracy
- Summarization prompt formatting
- Knowledge extraction parsing

### Integration Tests
- Full layer promotion workflow
- Stripe payment method operations
- Billing cycle changes
- Usage rollup accuracy

### E2E Tests
- Plan upgrade flow in UI
- Payment method management
- Usage analytics dashboard
- Memory layer progression

---

## Risk Mitigation

1. **LLM Costs**: Batch processing, caching similar requests, rate limiting
2. **Data Consistency**: Use transactions for layer transitions
3. **Performance**: Bounded channels for async processing
4. **Billing Errors**: Comprehensive Stripe webhook handling
5. **Memory Corruption**: Validation before layer transitions
