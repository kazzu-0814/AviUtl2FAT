# FAT IPC v1

FAT defines two versioned JSON Lines boundaries.

| Boundary | Protocol | Current version |
| --- | --- | --- |
| FAT App to C# Worker | `fat-app-ipc` | 1 |
| C# Worker to Python Engine | `fat-python` | 1 |

Every message includes `protocol`, `version`, `id`, `type`, and optional `payload` or `error`. Protocol mismatches are rejected before any recognition or provider execution begins.

The Python Engine commands are `engine.status`, `speech.recognize`, `ai.generate`, `ai.rewrite`, and `ai.shorten`. v0.7.1 implements `speech.recognize` through the ATT-compatible `recognize.py` adapter, plus the non-destructive `passthrough` AI provider.
