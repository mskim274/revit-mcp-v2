# Authoring a New Command

End-to-end checklist for adding a tool that goes from `Claude → MCP server
→ WebSocket → CAD plugin → Revit/AutoCAD API`.

> See [`WIRE_PROTOCOL.md`](./WIRE_PROTOCOL.md) for the message format.

## Three files, no glue code

The plugin uses reflection to auto-discover commands. Adding a new tool
means writing exactly three things:

1. A C# class implementing `IRevitCommand` / `ICadCommand`
2. A TypeScript tool registration in `server/src/tools/<category>.ts`
3. (Optional) A test case appended to `scripts/verify-server-shim.mjs`

## 1. C# command class

File: `commandset/Commands/<Category>/<Name>Command.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitMCP.CommandSet.Interfaces;

namespace RevitMCP.CommandSet.Commands.<Category>
{
    public class <Name>Command : IRevitCommand
    {
        // Wire name — matches the MCP tool name without the "revit_"/"cad_" prefix.
        public string Name => "<wire_name>";

        // Used for grouping in logs / dispatcher introspection.
        public string Category => "<Category>";

        public Task<CommandResult> ExecuteAsync(
            Document doc,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // For CREATE/MODIFY: wrap in Transaction with the "MCP: …" prefix
                // so the user can find it in undo history.
                using (var tx = new Transaction(doc, "MCP: <human-readable>"))
                {
                    tx.Start();
                    // … Revit API calls …
                    tx.Commit();
                }

                return Task.FromResult(CommandResult.Ok(new Dictionary<string, object>
                {
                    ["result"] = "value",
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CommandResult.Fail(
                    $"Failed: {ex.Message}",
                    suggestion: "Helpful next step the LLM can take."));
            }
        }
    }
}
```

### Rules

- **Transactions**: every API call that mutates the document MUST be wrapped
  in `Transaction`. Read-only queries do not need one (Revit) — note that
  AutoCAD requires read transactions too (`StartTransaction()` + `GetObject(... ForRead)`).
- **No threading tricks**: the dispatcher already marshalled you onto the
  main thread. Just write straight-line code.
- **Cancellation**: in any loop > 100 iterations, call
  `cancellationToken.ThrowIfCancellationRequested()` periodically.
- **Errors**: prefer `CommandResult.Fail(message, suggestion)` over throwing.
  Throwing is fine for unexpected bugs — the dispatcher will catch and
  attach a generic suggestion.
- **Verification (Tier 1)**: for CREATE/MODIFY commands, after `tx.Commit()`,
  re-query the element and compare against the request. Add a `verification`
  block to the success payload. See `CreateWallCommand.cs` for the pattern.

## 2. TypeScript tool registration

File: `server/src/tools/<category>.ts`

```typescript
server.registerTool(
    "<product>_<wire_name>",     // e.g. "revit_query_elements"
    {
        title: "Human Readable Title",
        description: `What this tool does, when to use it, what to expect back.

Aimed at the LLM, not the user — be explicit about parameter semantics
and the shape of the returned data.`,
        inputSchema: {
            param_name: z.string().describe("What this means and how to use it"),
            limit: z.number().int().min(1).max(200).optional()
                .describe("Max items per page. Default 50."),
        },
        annotations: {
            readOnlyHint: false,         // true for queries only
            destructiveHint: true,        // true if it modifies/deletes
            idempotentHint: false,
            openWorldHint: false,         // CAD model is closed-world
        },
    },
    async (params) => {
        return sendAndFormat(wsClient, "<wire_name>", {
            param_name: params.param_name,
            limit: params.limit,
        });
    }
);
```

### Rules

- **Tool name** prefix: `revit_` for Revit MCP, `cad_` (or `autocad_`) for
  the AutoCAD MCP. The wire `command` value never has the prefix.
- **`description` is for the LLM, not docs.** It decides whether to call
  this tool. Be explicit about when to use vs. when to use a different tool.
- **Annotations** matter for client UX (Claude Desktop shows different
  warnings for `destructiveHint: true`).
- **Always go through `sendAndFormat`** — don't reinvent the response
  formatting. That helper enforces the 25 KB / 500 KB overflow spill.

## 3. Register in `index.ts`

The `register*Tools()` helper at the bottom of each `tools/<category>.ts`
file already calls `server.registerTool(...)` for every tool in that
category. Just make sure your new tool is inside that helper. If you're
adding a new category, also call its register function from `index.ts`.

## 4. Test it

### Live test (with Revit/AutoCAD running):

```bash
node scripts/test-ws.js <wire_name> '{"param":"value"}'
```

This bypasses the MCP server entirely — useful for iterating on the C#
side without restarting Claude Desktop.

### Regression test:

After the command is stable, append a case to
`scripts/verify-server-shim.mjs` so future refactors don't break it:

```javascript
const CASES = [
  // …existing…
  ["NN_<name>", "<wire_name>", { param: "value" }],
];
```

Then capture a baseline:

```bash
node scripts/test-ws.js <wire_name> '{"param":"value"}' \
  > "작업자료/$(date +%Y-%m-%d)/snapshots/baseline-pre-refactor/NN_<name>.json"
```

Subsequent runs of `verify-server-shim.mjs` will diff against this.

## Common pitfalls

See [`CLAUDE.md`](../CLAUDE.md) "Known Pitfalls" section. Highlights:

- The `Commands.View` namespace shadows `Autodesk.Revit.DB.View` — use
  `global::Autodesk.Revit.DB.View` inside `Commands/View/`.
- Structural framing has `LevelId == InvalidElementId` — filter by the
  "참조 레벨" parameter, not by `level_filter`.
- AutoCAD requires read transactions even for queries — Revit does not.
- `IsolateElementsTemporary()` is Revit 2024+ only.
