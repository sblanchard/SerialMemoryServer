# 🔍 MassTransit Microservices Exploration Guide

## 🎯 What You Just Built

A production-grade, event-driven microservices architecture with:
- **Message-based communication** (not HTTP)
- **Automatic retries** with exponential backoff
- **Circuit breaker** pattern
- **Distributed tracing** with correlation IDs
- **Rate limiting**
- **Strongly-typed contracts**

---

## 🧪 Hands-On Exercises

### **Exercise 1: Trace a Single Request**

**Goal**: Follow a message from API → RabbitMQ → Worker → PostgreSQL

**Step 1**: Make a request with a unique key
```bash
curl -X POST http://localhost:5000/context/trace-me \
  -H "Content-Type: text/plain" \
  -d "Following this message through the system"
```

**Step 2**: Watch the logs in real-time
- Look for the correlation ID in Worker logs
- See how it flows through the entire system

**What to observe**:
- API logs: Event published
- Worker logs: Event received with same correlation ID
- PostgreSQL: Data persisted with timestamp

**Key Question**: Can you find the correlation ID? What is it used for?

---

### **Exercise 2: Trigger Retry Logic**

**Goal**: See MassTransit's automatic retry in action

**Option A**: Break PostgreSQL temporarily
```bash
# Stop PostgreSQL
docker stop serialmemoryserver-postgres-1

# Make a request (will fail)
curl -X POST http://localhost:5000/context/retry-test -d "This will retry!"

# Watch Worker logs - you'll see 5 retry attempts with exponential backoff:
# Attempt 1: immediate
# Attempt 2: +1s
# Attempt 3: +6s  (exponential)
# Attempt 4: +21s
# Attempt 5: +30s (capped)

# After 5 failures, message goes to error queue

# Restart PostgreSQL
docker start serialmemoryserver-postgres-1
```

**What to observe**:
- Retry delays increasing exponentially
- Error logging on each retry
- Final failure after 5 attempts

**Key Question**: Why exponential backoff? Why not retry immediately?

---

### **Exercise 3: Message Flow Visualization**

**Goal**: Understand the message journey

```
┌─────────────────────────────────────────────────────────────────┐
│ 1. CLIENT                                                       │
│    curl POST /context/my-key                                    │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│ 2. API (SerialMemory.Api/Program.cs:101-113)                  │
│    ├─ Store in Redis                                           │
│    ├─ MassTransitEventPublisher.PublishContextUpdatedAsync()   │
│    │   └─ Creates ContextUpdated event                         │
│    │       • MessageId: <guid>                                 │
│    │       • CorrelationId: <guid>                             │
│    │       • Timestamp: <utc>                                  │
│    └─ IPublishEndpoint.Publish()                               │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│ 3. RABBITMQ                                                     │
│    ├─ Exchange: ContextUpdated (auto-created by MassTransit)  │
│    ├─ Queue: ContextUpdated (bound automatically)              │
│    └─ Routing: Fanout to all consumers                         │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│ 4. WORKER (ContextUpdatedConsumer.cs:27)                       │
│    ├─ ConsumeContext<ContextUpdated> received                  │
│    ├─ Log: CorrelationId, MessageId, Key                       │
│    ├─ Read from Redis                                          │
│    ├─ PersistToPostgreSqlAsync()                               │
│    └─ SUCCESS or RETRY (if exception)                          │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│ 5. POSTGRESQL                                                   │
│    INSERT INTO context_snapshots                                │
│    ON CONFLICT (key) DO UPDATE                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

### **Exercise 4: Inspect RabbitMQ Management UI**

**Goal**: See the queues and messages in RabbitMQ

**Step 1**: Open RabbitMQ Management
```
URL: http://localhost:15672
Username: guest
Password: guest
```

**Step 2**: Navigate to "Queues" tab

**What you'll see**:
- `ContextUpdated` queue (created by MassTransit)
- `ContextDeleted` queue
- `ContextUpdated_error` queue (for failed messages)
- `ContextUpdated_skipped` queue

**Step 3**: Send a message and watch the queue depth change

**Key Questions**:
- How many messages are in the queue?
- What happens if the Worker is stopped?
- Where do failed messages go?

---

### **Exercise 5: Break Things and Watch Recovery**

**Goal**: Test resilience patterns

**Scenario A: Worker Crash**
```bash
# Stop the Worker
# (Find the process and kill it, or Ctrl+C in the terminal)

# Send 10 messages
for i in {1..10}; do
  curl -X POST http://localhost:5000/context/test-$i -d "Message $i"
done

# Messages accumulate in RabbitMQ queue

# Restart Worker → All messages processed!
```

**Scenario B: Slow Consumer**
```bash
# Add a delay in ContextUpdatedConsumer.cs:
await Task.Delay(5000); // Simulate slow processing

# Send many messages quickly
# Watch rate limiting and backpressure in action
```

**Scenario C: Circuit Breaker**
```bash
# Break PostgreSQL
# Send 20 messages
# After 15 failures, circuit breaker opens
# No more attempts for 5 minutes (ResetInterval)
```

---

### **Exercise 6: Message Anatomy**

**Goal**: Understand message structure

Look at `SerialMemory.Contracts/Events/ContextUpdated.cs`:

```csharp
public record ContextUpdated
{
    public Guid MessageId { get; init; }          // ← Unique per message (deduplication)
    public Guid CorrelationId { get; init; }      // ← Links related messages (tracing)
    public DateTime Timestamp { get; init; }       // ← When event occurred
    public required string Key { get; init; }      // ← Business data
    public string? Value { get; init; }            // ← Business data
    public string? Source { get; init; }           // ← Audit trail
    public Dictionary<string, string>? Metadata { get; init; }  // ← Extensibility
}
```

**Why these fields?**
- **MessageId**: Prevents duplicate processing (idempotency)
- **CorrelationId**: Traces across services (distributed tracing)
- **Timestamp**: Event sourcing, ordering, debugging
- **Source**: Audit trail (who/what triggered this?)
- **Metadata**: Extensibility without breaking contracts

---

### **Exercise 7: Debug Flow with Breakpoints**

**In your IDE**:

**Set breakpoints**:
1. `SerialMemory.Api/Program.cs:109` - Before publishing
2. `SerialMemory.Infrastructure/MassTransitEventPublisher.cs:44` - Publish call
3. `SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs:27` - Consumer entry
4. `SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs:76` - PostgreSQL write

**Run in debug mode**:
```bash
# Stop the running services
# Start API in debug (F5 in Visual Studio/Rider)
# Start Worker in debug (F5 in another window)
```

**Make a request and step through**:
```bash
curl -X POST http://localhost:5000/context/debug-me -d "Step through me!"
```

**Watch variables**:
- `context.Message.CorrelationId` - Same in API and Worker?
- `message.Key` - Correct value?
- `message.Timestamp` - When was it created?

---

## 🎓 Key Concepts to Understand

### **1. Pub/Sub vs Request/Response**

**Old way (HTTP)**:
```
API → HTTP POST → Worker
      ↓
   Wait for response (blocks)
   What if Worker is down? ❌
```

**New way (Pub/Sub)**:
```
API → Publish event → RabbitMQ → Worker
      ↓
   Return immediately ✅
   Worker can be down, messages queue up ✅
```

### **2. At-Least-Once Delivery**

MassTransit guarantees messages are delivered **at least once**.

**What this means**:
- If Worker crashes mid-processing, message is re-delivered
- Consumer must be **idempotent** (safe to process twice)
- Use MessageId for deduplication if needed

### **3. Eventual Consistency**

```
API returns 200 OK
  ↓
But PostgreSQL write happens later (asynchronously)
  ↓
System is "eventually consistent"
```

**Trade-off**: Speed vs immediate consistency

---

## 📊 Monitoring and Observability

### **Check Metrics**

**Worker metrics** (Prometheus format):
```bash
curl http://localhost:8081/metrics
```

**What to look for**:
- `rabbit_consumed_total` - Total messages consumed
- `rabbit_published_total` - Total messages published

### **Check Logs**

**Correlation ID search**:
```bash
# In API logs, find a CorrelationId
# Search Worker logs for same CorrelationId
# Proves message flowed through the system
```

---

## 🔧 Configuration Tuning

### **Retry Policy**

In `SerialMemory.Worker/Program.cs:40-45`:

```csharp
cfg.UseMessageRetry(r =>
{
    r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
    // ↑ Retries | ↑ Initial delay | ↑ Max delay | ↑ Delay increment
});
```

**Experiment**: Change to immediate retry:
```csharp
r.Immediate(3); // Retry 3 times with no delay
```

### **Circuit Breaker**

In `SerialMemory.Worker/Program.cs:47-54`:

```csharp
cfg.UseCircuitBreaker(cb =>
{
    cb.TripThreshold = 15;     // Open after 15 failures
    cb.ActiveThreshold = 10;   // Need 10 successes to close
    cb.ResetInterval = TimeSpan.FromMinutes(5);  // Try again after 5 min
});
```

**Experiment**: Make it more aggressive:
```csharp
cb.TripThreshold = 3;  // Open after just 3 failures
```

---

## 🎯 Interview Prep Questions

After exploring, answer these:

1. **What's the difference between MessageId and CorrelationId?**
2. **Why use exponential backoff instead of immediate retry?**
3. **What happens if a message fails after 5 retries?**
4. **How does the circuit breaker prevent cascading failures?**
5. **What's the trade-off of async/eventual consistency?**
6. **How would you make the consumer idempotent?**
7. **What happens if RabbitMQ goes down?**
8. **How do you trace a request across microservices?**

---

## 🚀 Next Steps

1. ✅ **Understand the flow** (do Exercise 1-3)
2. ✅ **Break things** (do Exercise 5)
3. ✅ **Debug with breakpoints** (Exercise 7)
4. ✅ **Answer interview questions** above
5. 🎯 **Ready for Phase 2?** (SignalR, CQRS, or Load Testing)

---

## 📚 Further Reading

- [MassTransit Documentation](https://masstransit.io/)
- [Circuit Breaker Pattern](https://martinfowler.com/bliki/CircuitBreaker.html)
- [Saga Pattern](https://microservices.io/patterns/data/saga.html)
- [Event-Driven Architecture](https://martinfowler.com/articles/201701-event-driven.html)
