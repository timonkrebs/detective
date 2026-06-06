# Detective MCP Server

A Model Context Protocol (MCP) server that lets LLMs analyze code architecture and evolution of JavaScript/TypeScript projects using Detective's analysis services.

## Overview

The MCP server is **not** a standalone process. It is mounted into the Detective backend (Express) and exposes the same analysis services that power Detective's REST API. See [`server.ts`](./server.ts) for the tool definitions and [`http-router.ts`](./http-router.ts) for the transport wiring (mounted at `/mcp` in [`../express.ts`](../express.ts)).

Capabilities:

- **Architecture / domain boundaries**: structural and logical coupling, cohesion, module sizes, team alignment
- **Hotspot detection**: files combining high complexity and high change frequency
- **Trend analysis**: how complexity and size evolve over time
- **X-Ray**: deep, per-file metrics
- **Project navigation & config**: inferred folder structure and Detective configuration

### Self-description for LLMs

The server is built so an LLM can pick the right tool on its own:

- The server sends **`instructions`** during `initialize` with the domain glossary and an intent -> tool mapping (e.g. "evaluate domain boundaries" -> `coupling_get` + `changeCoupling_get`).

Note: tool names use underscores (e.g. `coupling_get`), not dots, because MCP clients only allow alphanumeric characters and underscores in tool names.

- Every tool ships a rich **`description`**, a human-friendly **`title`**, and **`annotations`** (`readOnlyHint`, `idempotentHint`, `destructiveHint`).
- Every input field has a **`.describe()`** text.
- Every tool defines an **`outputSchema`**; results are returned both as JSON text and as validated **`structuredContent`**.

## Available Tools

All tools are read-only and idempotent unless noted otherwise. Limit parameters (`limitCommits`, `limitMonths`) are available on history-based tools; `null`/omitted means "entire history".

| Tool                 | Purpose                                                                                                               | Key inputs                                        |
| -------------------- | --------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------- |
| `config_read`        | Read the Detective configuration (scopes/domains, groups, teams, aliases, filter).                                    | –                                                 |
| `config_write`       | Overwrite the configuration file (destructive).                                                                       | `config`                                          |
| `cache_status`       | Check whether the git-log cache is stale.                                                                             | –                                                 |
| `cache_update`       | Rebuild the git-log cache.                                                                                            | –                                                 |
| `modules_get`        | File count per scope/domain (size & balance of the cuts).                                                             | –                                                 |
| `folders_get`        | Folder hierarchy inferred from dependencies.                                                                          | –                                                 |
| `coupling_get`       | Structural coupling matrix (imports between scopes) + cohesion per scope. Primary tool to evaluate domain boundaries. | –                                                 |
| `changeCoupling_get` | Logical/temporal coupling: how often scopes change together.                                                          | `limitCommits`, `limitMonths`                     |
| `teamAlignment_get`  | Lines changed per team (or per person) per module (Conway).                                                           | `byUser`, limits                                  |
| `hotspots_find`      | Hotspot files, sorted by score (`complexity * commits`).                                                              | `module`, `minScore`, `metric`, limits            |
| `hotspots_aggregate` | Hotspot statistics aggregated per module.                                                                             | `minScore`, `metric`, limits                      |
| `trendAnalysis_run`  | Complexity/size trends over recent commits.                                                                           | `maxCommits`, `parallelWorkers`, `fileExtensions` |
| `xray_get`           | Deep per-file metrics.                                                                                                | `file`, `includeSource`                           |
| `xray_schema`        | JSON + UI schema describing the (dynamic) X-Ray metrics.                                                              | –                                                 |

### Output field semantics (highlights)

- `coupling_get`: `matrix[i][j]` = number of imports from scope `i` to scope `j`; the diagonal (`i == j`) is intra-scope coupling. `cohesion[i]` is the cohesion of scope `i` in percent. `dimensions` are the scope names (matrix axes).
- `changeCoupling_get`: `matrix[i][j]` = number of commits that changed module `i` and module `j` together (only the upper triangle is filled). `fileCount` here is the number of commits touching each module. `cohesion` is reserved (`-1`).
- `hotspots_find`: each hotspot has `commits`, `changedLines`, `complexity` and `score = complexity * commits`.
- `teamAlignment_get`: `modules[module].changes[team|user]` = sum of changed lines (added + removed).

## Usage Patterns for LLMs

### "Evaluate the domain boundaries / module cuts"

1. `coupling_get` - structural coupling + cohesion per scope. Good cut = high cohesion within, low coupling between scopes.
2. `changeCoupling_get` - logical coupling; scopes that always change together may be mis-cut.
3. `modules_get` - check size/balance of the scopes.
4. `teamAlignment_get` - Conway check: ideally one team owns one module.

### "What needs refactoring?"

1. `hotspots_aggregate` - which modules contain the most problematic files.
2. `hotspots_find` - concrete file candidates, sorted by score.
3. `xray_get` (+ `xray_schema`) - deep dive into a specific file.

### "How did quality evolve?"

1. `trendAnalysis_run` - complexity/size trends over the last N commits.

## Transports

The same `createMcpServer(options)` is exposed over two transports.

### Streamable HTTP (default)

Stateless Streamable HTTP (no SSE notifications), mounted at `/mcp` whenever the Detective backend runs:

- `POST /mcp` - JSON-RPC requests (`initialize`, `tools/list`, `tools/call`, ...)
- `GET /mcp` and `DELETE /mcp` - return `405 Method Not Allowed`
- `GET /mcp/health` - returns `{ "ok": true, "transport": "streamable-http" }`

### STDIO

Start Detective with `--mcp` to run a single, long-lived MCP server over stdin/stdout. In this mode **no HTTP server is started and no browser is opened**; `stdout` is reserved for the JSON-RPC protocol and all diagnostic logging is routed to `stderr`.

```bash
node dist/apps/backend/main.js --mcp --path /path/to/repo/to/analyze
```

This is the mode to use for MCP clients like Claude Desktop:

```json
{
  "mcpServers": {
    "detective": {
      "command": "node",
      "args": ["/absolute/path/to/dist/apps/backend/main.js", "--mcp", "--path", "/path/to/repo/to/analyze"]
    }
  }
}
```

## Running & Testing

The HTTP endpoint is available whenever the Detective backend runs. The backend listens on port `3334` by default (configurable via `--port`).

```bash
# Build the backend (includes the MCP server)
npm run mcp:build

# Inspect the running HTTP server with the MCP Inspector (backend must be running)
npm run mcp:inspect   # connects to http://localhost:3334/mcp

# Quick STDIO smoke test
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  | node dist/apps/backend/main.js --mcp --path .
```

Example raw request:

```bash
curl -X POST http://localhost:3334/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": { "name": "coupling_get", "arguments": {} }
  }'
```

## Architecture

```
MCP Client (Claude, Inspector, ...)
    | Streamable HTTP (POST /mcp)
Express backend (apps/backend/src/express.ts)
    | createMcpHttpRouter -> createMcpServer(options)
Detective MCP Server (apps/backend/src/mcp/server.ts)
    | calls
Detective analysis services (coupling, hotspot, trend-analysis, x-ray, ...)
    | reads
Git history + TypeScript source
```

Each tool is a thin, type-safe wrapper that validates inputs with Zod, calls an existing Detective service, and returns both JSON text and schema-validated `structuredContent`.
