```
                    ┌──────────────────────┐
                    │      User / App      │
                    └──────────┬───────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────┐
│                  Agent Runtime                      │
│                                                     │
│   Conversation                                      │
│        │                                            │
│        ▼                                            │
│   Context Builder                                   │
│        │                                            │
│        ▼                                            │
│   LLM / Reasoning Engine                            │
│        │                                            │
│        ▼                                            │
│   Tool Selection / Execution Loop                   │
│        │                                            │
│   ┌────┴──────────────┐                             │
│   │ Policy / Guardrail│                             │
│   └────┬──────────────┘                             │
│        │                                            │
│   Tool Registry                                     │
└────────┼────────────────────────────────────────────┘
         │
         │ standard protocol
         ▼
┌───────────────────────┐
│ Application Adapter   │
│                       │
│ tools(MCP)            │
│ context               │
│ policies              │
│ identity              │
└──────────┬────────────┘
           │
           ▼
       Business App
```

