# fvc-mcp — MCP server for Fast Video Cataloger

Exposes a Fast Video Cataloger catalog to an AI assistant over the
[Model Context Protocol](https://modelcontextprotocol.io/). It is a thin client over the
catalog server's REST API — it holds no state and knows nothing about the catalog format,
so anything the REST API gains is one method away from being a tool here.

User-facing setup instructions live in the documentation at
`CatalogerDocumentation/docs-mkdocs/docs/server/serving-media/mcp-server.md`.

## Layout

| File | Purpose |
|------|---------|
| `Program.cs` | Host setup, configuration, stdio transport. |
| `FvcClient.cs` | HTTP client over the REST API. Unwraps the `{success, data}` envelope and turns failures into messages an assistant can act on. |
| `CatalogTools.cs` | The tools themselves. One method per tool. |

## Configuration

Read from the environment, which is how MCP hosts pass settings. `--url` and `--key` on the
command line override them, for trying it out by hand.

- `FVC_SERVER_URL` — default `http://localhost:8754`
- `FVC_API_KEY` — created with `POST /api/v1/apikeys`; optional against a catalog with no accounts
- `FVC_MCP_LOG_LEVEL` — default `Warning`

## Things to know before changing it

- **stdout belongs to the protocol.** Logging is pinned to stderr in `Program.cs`. A stray
  `Console.WriteLine` anywhere in this project corrupts the JSON-RPC stream and the assistant
  silently loses the connection.
- **Only `McpException` messages reach the caller.** `FvcException` derives from it for exactly
  that reason — throwing anything else gets the model a bare "An error occurred invoking ...",
  which it cannot act on. Keep the messages free of anything secret.
- **Tool descriptions are the interface.** The model has nothing else to go on when choosing a
  tool. Say what the tool is *for* and when to reach for it, not just what it returns.
- **Mark read-only tools `ReadOnly = true`.** Hosts use the annotation to decide what may run
  without asking the user.

## Adding a tool

Add a method to `CatalogTools`, attribute it, and describe every parameter:

```csharp
[McpServerTool(Name = "get_video_tags", ReadOnly = true, Title = "Get a video's tags")]
[Description("List the keywords a video is tagged with.")]
public Task<string> GetVideoTags(
    [Description("The video's catalog id.")] long videoId,
    CancellationToken ct = default)
{
    return _client.GetAsync($"/api/v1/videos/{videoId}/tags", ct);
}
```

Registration is by type in `Program.cs` (`WithTools<CatalogTools>()`), so nothing else needs
updating. Verify with the probe script — the tool must appear in `tools/list` with the schema
you expect, since a wrong schema fails at call time rather than at build time.

## Testing

There is no automated test project. To exercise it, drive the protocol over stdio with stdin
held open — piping a file gives the transport EOF before it writes any response, so it looks
like the server produced nothing.

Send `initialize`, then `notifications/initialized`, then `tools/list`, then a `tools/call`,
reading one response line after each request.
