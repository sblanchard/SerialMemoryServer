# 🎯 Debugging Cheatsheet - Quick Reference

## 📍 **8 BREAKPOINTS TO SET**

```
┌─────────────────────────────────────────────────────────────────┐
│ 🔴 1. SerialMemory.Api/Program.cs:101                          │
│     → API receives request                                      │
│     Watch: key, req.Body                                        │
├─────────────────────────────────────────────────────────────────┤
│ 🔴 2. SerialMemory.Api/Program.cs:109                          │
│     → Before publishing event                                   │
│     Watch: key, body                                            │
├─────────────────────────────────────────────────────────────────┤
│ 🔴 3. SerialMemory.Infrastructure/MassTransitEventPublisher.cs:35 │
│     → Creating event object                                     │
│     Watch: @event.CorrelationId ⭐ WRITE THIS DOWN!            │
├─────────────────────────────────────────────────────────────────┤
│ 🔴 4. SerialMemory.Infrastructure/MassTransitEventPublisher.cs:44 │
│     → Publishing to RabbitMQ                                    │
│     Note: API returns immediately after this!                   │
├─────────────────────────────────────────────────────────────────┤
│ 🔴 5. SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs:27 │
│     → Worker receives message                                   │
│     Watch: context.Message.CorrelationId (matches #3!)          │
├─────────────────────────────────────────────────────────────────┤
│ 🔴 6. SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs:41 │
│     → After reading from Redis                                  │
│     Watch: value.ToString()                                     │
├─────────────────────────────────────────────────────────────────┤
│ 🔴 7. SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs:76 │
│     → Before PostgreSQL write                                   │
│     Watch: key, value, sql                                      │
├─────────────────────────────────────────────────────────────────┤
│ 🔴 8. SerialMemory.Worker/Consumers/ContextUpdatedConsumer.cs:48 │
│     → Success!                                                  │
│     Execution complete                                          │
└─────────────────────────────────────────────────────────────────┘
```

## ⌨️ **KEYBOARD SHORTCUTS**

| Key  | Action              | Description                           |
|------|---------------------|---------------------------------------|
| F5   | Continue            | Run until next breakpoint             |
| F10  | Step Over           | Execute current line, stay in method  |
| F11  | Step Into           | Go into method call                   |
| F9   | Toggle Breakpoint   | Add/remove breakpoint                 |

## 🧪 **TEST REQUEST**

```bash
curl -X POST http://localhost:5000/context/debug-flow \
  -H "Content-Type: text/plain" \
  -d "Step through me!"
```

## 👀 **WHAT TO WATCH**

### At Breakpoint 3 (Creating Event):
```csharp
@event.CorrelationId  // ← Copy this GUID!
@event.MessageId
@event.Key
@event.Value
@event.Timestamp
```

### At Breakpoint 5 (Worker Receives):
```csharp
context.Message.CorrelationId  // ← Should match Breakpoint 3!
context.Message.Key
context.Message.Value
context.MessageId
context.SentTime
```

## 🎯 **KEY OBSERVATIONS**

1. **Correlation ID Propagation**: Same GUID from API to Worker
2. **Async Processing**: API returns before Worker finishes
3. **Type Safety**: Strongly-typed `ContextUpdated` message
4. **Automatic Serialization**: No manual JSON handling

## 🔬 **EXPERIMENTS**

### See Retry Logic:
```csharp
// At Breakpoint 7, add:
throw new Exception("Test retry!");
// Watch it retry 5 times with exponential backoff
```

### Simulate Slow Processing:
```csharp
// At Breakpoint 6, add:
await Task.Delay(5000);
// Watch the 5-second delay
```

### Inspect Full Message:
```csharp
// Debug Console:
JsonSerializer.Serialize(context.Message, new JsonSerializerOptions { WriteIndented = true })
```

## 📊 **CHECKLIST**

- [ ] Set all 8 breakpoints
- [ ] Start API in debug mode (F5)
- [ ] Start Worker in debug mode (F5 in separate window)
- [ ] Make test request with curl
- [ ] Breakpoint 1-4 hit in API
- [ ] Breakpoint 5-8 hit in Worker
- [ ] Note CorrelationId matches
- [ ] Verify data in PostgreSQL

## 🚀 **READY?**

1. Open `SerialMemory.Api` in your IDE
2. Set breakpoints 1-4
3. Press F5 to start debugging
4. Open `SerialMemory.Worker` in another IDE window
5. Set breakpoints 5-8
6. Press F5 to start debugging
7. Run the curl command
8. Watch the magic happen! ✨
