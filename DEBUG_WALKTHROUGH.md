# 🐛 Step-by-Step Debugging Walkthrough

## 🎯 Goal
Step through a single request from API → RabbitMQ → Worker → PostgreSQL and understand exactly what happens at each step.

---

## 📍 **BREAKPOINT MAP**

Set these breakpoints in your IDE (Visual Studio, Rider, or VS Code):

### **🔴 Breakpoint 1: API Receives Request**
**File**: `SerialMemory.Api/Program.cs`
**Line**: `101` (start of MapPost handler)

```csharp
app.MapPost("/context/{key}", async (string key, HttpRequest req, IContextStore store,
    MassTransitEventPublisher eventPublisher, IHubContext<ContextHub> hub) =>
{
    // ← SET BREAKPOINT HERE
    using var reader = new StreamReader(req.Body);
```

**What to watch**:
- `key` variable - The context key from URL
- `req.Body` - The request payload

---

### **🔴 Breakpoint 2: Before Publishing Event**
**File**: `SerialMemory.Api/Program.cs`
**Line**: `109` (before MassTransit publish)

```csharp
    // ✨ Publish strongly-typed event with MassTransit
    await eventPublisher.PublishContextUpdatedAsync(key, body, "api");
    // ← SET BREAKPOINT HERE
```

**What to watch**:
- `key` - Should match your request
- `body` - The value you sent

---

### **🔴 Breakpoint 3: Inside Event Publisher**
**File**: `SerialMemory.Infrastructure/MassTransitEventPublisher.cs`
**Line**: `35` (creating the event object)

```csharp
        var @event = new ContextUpdated
        {
            CorrelationId = Guid.NewGuid(), // For distributed tracing
            // ← SET BREAKPOINT HERE
            Key = key,
            Value = value,
            Source = source ?? "api",
            Metadata = metadata
        };
```

**What to watch**:
- `@event.CorrelationId` - **WRITE THIS DOWN!** You'll see it in the Worker
- `@event.MessageId` - Unique message identifier
- `@event.Timestamp` - When the event was created

---

### **🔴 Breakpoint 4: Actual Publish Call**
**File**: `SerialMemory.Infrastructure/MassTransitEventPublisher.cs`
**Line**: `44` (MassTransit publish)

```csharp
        // MassTransit publish - fire and forget with reliability
        await _publishEndpoint.Publish(@event, cancellationToken);
        // ← SET BREAKPOINT HERE (BEFORE the await)
```

**What happens here**:
- Message serialized to JSON
- Sent to RabbitMQ
- API doesn't wait for Worker to process it!

---

### **🔴 Breakpoint 5: Worker Receives Message**
**File**: `SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs`
**Line**: `27` (Consume method entry)

```csharp
    public async Task Consume(ConsumeContext<ContextUpdated> context)
    {
        var message = context.Message;
        // ← SET BREAKPOINT HERE

        _logger.LogInformation(
```

**What to watch**:
- `context.Message` - The entire event object
- `context.Message.CorrelationId` - **SAME as in Breakpoint 3!**
- `context.Message.Key` - Should match your request
- `context.Message.Value` - Your data

---

### **🔴 Breakpoint 6: Reading from Redis**
**File**: `SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs`
**Line**: `41` (after Redis read)

```csharp
            var value = await db.StringGetAsync($"context:{message.Key}");

            if (value.HasValue)
            {
                // ← SET BREAKPOINT HERE
                // Persist to PostgreSQL
```

**What to watch**:
- `value.ToString()` - Data from Redis
- Should match what you sent in the request

---

### **🔴 Breakpoint 7: Before PostgreSQL Write**
**File**: `SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs`
**Line**: `76` (inside PersistToPostgreSqlAsync)

```csharp
    private async Task PersistToPostgreSqlAsync(string key, string value)
    {
        const string sql = """
            INSERT INTO context_snapshots (key, data, updated_at)
            // ← SET BREAKPOINT HERE
```

**What to watch**:
- `key` parameter
- `value` parameter
- The SQL query that will be executed

---

### **🔴 Breakpoint 8: After PostgreSQL Write**
**File**: `SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs`
**Line**: `48` (success log)

```csharp
                _logger.LogInformation(
                    "[Success] Persisted context to PostgreSQL: Key={Key}, ValueLength={Length}",
                    // ← SET BREAKPOINT HERE
                    message.Key,
```

**What to watch**:
- Execution completed successfully!
- Check the logs to confirm

---

## 🚀 **HOW TO DEBUG**

### **Step 1: Start API in Debug Mode**

**Visual Studio / Rider**:
1. Open `SerialMemory.Api` project
2. Set all API breakpoints (Breakpoints 1-4)
3. Press **F5** or click "Debug"
4. Wait for "Now listening on: http://localhost:5000"

**VS Code**:
1. Open folder in VS Code
2. Create `.vscode/launch.json`:
```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Debug API",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/SerialMemory.Api/bin/Debug/net9.0/SerialMemory.Api.dll",
      "args": [],
      "cwd": "${workspaceFolder}/SerialMemory.Api",
      "stopAtEntry": false,
      "env": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    },
    {
      "name": "Debug Worker",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/SerialMemory.Worker/bin/Debug/net9.0/SerialMemory.Worker.dll",
      "args": [],
      "cwd": "${workspaceFolder}/SerialMemory.Worker",
      "stopAtEntry": false
    }
  ]
}
```

---

### **Step 2: Start Worker in Debug Mode**

**In a SECOND IDE window** (or terminal):
1. Open `SerialMemory.Worker` project
2. Set all Worker breakpoints (Breakpoints 5-8)
3. Press **F5** or click "Debug"
4. Wait for "Bus started: rabbitmq://localhost/"

**Important**: Both must be running simultaneously!

---

### **Step 3: Make a Test Request**

In a terminal or Postman:

```bash
curl -X POST http://localhost:5000/context/debug-flow \
  -H "Content-Type: text/plain" \
  -d "Step through me!"
```

---

## 🎬 **THE DEBUGGING EXPERIENCE**

### **🔴 Breakpoint 1 Hits (API)**
```
File: Program.cs:101
Variables:
  key = "debug-flow"
  req.Body = <stream>
```

**Press F10 (Step Over)** to read the body

---

### **🔴 Breakpoint 2 Hits (API)**
```
File: Program.cs:109
Variables:
  key = "debug-flow"
  body = "Step through me!"
```

**Note**: You're about to publish the event
**Press F11 (Step Into)** to go into the publisher

---

### **🔴 Breakpoint 3 Hits (Infrastructure)**
```
File: MassTransitEventPublisher.cs:35
Variables:
  @event.CorrelationId = "12345678-abcd-..." ← WRITE THIS DOWN!
  @event.Key = "debug-flow"
  @event.Value = "Step through me!"
  @event.Timestamp = "2025-11-12T16:30:00Z"
```

**Important**: Hover over `@event.CorrelationId` and COPY IT
**Press F10** to continue

---

### **🔴 Breakpoint 4 Hits (Infrastructure)**
```
File: MassTransitEventPublisher.cs:44
About to execute:
  await _publishEndpoint.Publish(@event, cancellationToken);
```

**What happens when you press F10**:
1. Message is serialized to JSON
2. Sent to RabbitMQ exchange "ContextUpdated"
3. API returns 200 OK immediately (doesn't wait!)

**Press F10** and watch the API return

---

### **🔴 Breakpoint 5 Hits (Worker)**
```
File: ContextUpdatedConsumer.cs:27
Variables:
  context.Message.CorrelationId = "12345678-abcd-..." ← SAME AS BREAKPOINT 3!
  context.Message.Key = "debug-flow"
  context.Message.Value = "Step through me!"
```

**Key Observation**: The CorrelationId traveled through RabbitMQ!

**Press F10** to step through the logging

---

### **🔴 Breakpoint 6 Hits (Worker)**
```
File: ContextUpdatedConsumer.cs:41
Variables:
  value.ToString() = "Step through me!"
  message.Key = "debug-flow"
```

**Hover over** `value` to see the Redis data
**Press F10** to continue

---

### **🔴 Breakpoint 7 Hits (Worker)**
```
File: ContextUpdatedConsumer.cs:76
Variables:
  key = "debug-flow"
  value = "Step through me!"
  sql = "INSERT INTO context_snapshots..."
```

**What's about to happen**: Dapper will execute the SQL
**Press F10** to execute the INSERT

---

### **🔴 Breakpoint 8 Hits (Worker)**
```
File: ContextUpdatedConsumer.cs:48
Success! Data is now in PostgreSQL
```

**Press F5** to continue execution

---

## 🎯 **KEY THINGS TO OBSERVE**

### **1. Correlation ID Propagation**
- Created in API (Breakpoint 3)
- Travels through RabbitMQ automatically
- Appears in Worker (Breakpoint 5)
- **Same GUID throughout the entire flow!**

### **2. Async Fire-and-Forget**
- API publishes (Breakpoint 4)
- Returns 200 OK immediately
- Worker processes later (Breakpoint 5)
- This is **eventual consistency**

### **3. Strongly-Typed Messages**
- `ContextUpdated` is a C# record
- Serialized automatically by MassTransit
- Deserialized in Worker
- Type-safe end-to-end!

### **4. Automatic Retry (If Error)**
If you throw an exception in the Worker:
```csharp
throw new Exception("Simulated failure!");
```

Watch it retry 5 times with exponential backoff!

---

## 🧪 **EXPERIMENTS TO TRY**

### **Experiment 1: See the Message Object**
At Breakpoint 5, in the Debug Console:
```csharp
// View the entire message as JSON
JsonSerializer.Serialize(context.Message, new JsonSerializerOptions { WriteIndented = true })
```

### **Experiment 2: Simulate Slow Processing**
At Breakpoint 6, add:
```csharp
await Task.Delay(5000); // Simulate slow processing
```
Continue debugging and watch the 5-second delay

### **Experiment 3: Trigger Retry**
At Breakpoint 7, before the SQL:
```csharp
throw new Exception("Simulated database failure!");
```
Watch MassTransit retry 5 times!

### **Experiment 4: Inspect MassTransit Context**
At Breakpoint 5:
```csharp
context.MessageId              // Unique message ID
context.SentTime               // When was it sent?
context.Headers                // All message headers
context.ConversationId         // For saga patterns
```

---

## 📊 **DEBUGGING CHECKLIST**

- [ ] API Breakpoint 1: Request received
- [ ] API Breakpoint 2: Before publish
- [ ] Publisher Breakpoint 3: Event created (note CorrelationId)
- [ ] Publisher Breakpoint 4: Sending to RabbitMQ
- [ ] Worker Breakpoint 5: Message received (same CorrelationId!)
- [ ] Worker Breakpoint 6: Read from Redis
- [ ] Worker Breakpoint 7: Before PostgreSQL write
- [ ] Worker Breakpoint 8: Success log
- [ ] Verified in PostgreSQL: Data exists

---

## 🎓 **WHAT YOU LEARNED**

After stepping through, you now understand:

1. ✅ How messages flow through MassTransit
2. ✅ What correlation IDs are and why they matter
3. ✅ How async/fire-and-forget works
4. ✅ The difference between MessageId and CorrelationId
5. ✅ How retry policies work (if you tested it)
6. ✅ Why eventual consistency is a trade-off

---

## 🚀 **NEXT STEPS**

1. **Read the logs** - Correlate the CorrelationId across services
2. **Try the experiments** above
3. **Break things** - Throw exceptions and watch retries
4. **Add logging** - Put more log statements to see the flow
5. **Ready for Phase 2?** - SignalR, CQRS, or Load Testing

---

## 💡 **PRO TIPS**

### **Watch Window Expressions**
Add these to your Watch window:
```
context.Message.CorrelationId
context.Message.Timestamp
DateTime.UtcNow - context.Message.Timestamp  // Processing delay
```

### **Conditional Breakpoints**
Right-click breakpoint → Conditions:
```csharp
message.Key == "debug-flow"
```
Only breaks for specific keys!

### **Tracepoints (Log Without Stopping)**
Right-click breakpoint → Actions:
```
CorrelationId: {context.Message.CorrelationId}
```
Logs without stopping execution!

---

## 📚 **QUESTIONS TO ANSWER WHILE DEBUGGING**

1. What is the delay between Breakpoint 4 (publish) and Breakpoint 5 (receive)?
2. Does the CorrelationId match exactly?
3. What happens if you stop the Worker before Breakpoint 5?
4. How long does the PostgreSQL write take?
5. What's in the `context.Headers` collection?
6. Can you find the MessageId in RabbitMQ Management UI?

---

**Ready to start debugging?** Set your breakpoints and let's go! 🚀
