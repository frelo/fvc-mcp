# Fast Video Cataloger MCP server

Search, view and tag your local video library from an AI assistant.

[Fast Video Cataloger](https://videocataloger.com/?utm_source=github&utm_medium=referral&utm_campaign=fvc-mcp)
keeps a searchable catalog of your own video files on Windows: scene thumbnails, keywords,
transcripts and faces. This MCP server lets an assistant such as Claude work with that catalog.
You ask for footage in your own words; the assistant searches, looks at the actual shots and does
the filing for you.

- Find videos and moments by what was said, with timecodes
- Find scenes by describing the picture ("a drone shot over a city at night"), nothing tagged first
- Fetch scene thumbnails so the assistant can see and judge a shot
- With an Editor key: add or remove keywords, build bins, index new videos

It runs against the Fast Video Cataloger server on your own computer. No video is uploaded; the
assistant receives the titles, transcript lines and scene images it asks for.

More: [MCP server for your video library](https://videocataloger.com/mcp-server-video-library/?utm_source=github&utm_medium=referral&utm_campaign=fvc-mcp)

## Requirements

- Windows 10 or 11 (x64)
- [Fast Video Cataloger](https://videocataloger.com/download/?utm_source=github&utm_medium=referral&utm_campaign=fvc-mcp)
  10.4 or later. The free 30-day trial works. `fvc-mcp.exe` is also installed with the program,
  together with the .NET 10 runtime it needs.
- Your catalog shared through the Fast Video Cataloger server: in the program, open the start
  page's **Server** section and click **Setup**. The assistant connects to the server, not to the
  desktop program, so it works with the program closed.
- An API key: **Manage Users** in the same Server section. `Viewer` to search and read, `Editor`
  to also tag and build bins.

## Install

### Claude Desktop: one click

Download `fvc-mcp-<version>.mcpb` from [Releases](https://github.com/frelo/fvc-mcp/releases) and
open it. Claude Desktop asks for the server address (leave `http://localhost:8754` when the server
runs on this computer) and your API key.

### Claude Desktop: config file

Use the copy installed with Fast Video Cataloger. In `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "fast-video-cataloger": {
      "command": "C:\\Program Files\\FastVideoCataloger\\fvc-mcp.exe",
      "env": {
        "FVC_SERVER_URL": "http://localhost:8754",
        "FVC_API_KEY": "fvc_your_key_here"
      }
    }
  }
}
```

### Claude Code

```powershell
claude mcp add fast-video-cataloger --env FVC_SERVER_URL=http://localhost:8754 --env FVC_API_KEY=fvc_your_key_here "--" "C:\Program Files\FastVideoCataloger\fvc-mcp.exe"
```

Keep the quotes around `"--"` in PowerShell.

### Gemini CLI

```powershell
gemini mcp add fast-video-cataloger "C:\Program Files\FastVideoCataloger\fvc-mcp.exe" -s user -e FVC_SERVER_URL=http://localhost:8754 -e FVC_API_KEY=fvc_your_key_here
```

Full setup, things to try and troubleshooting:
[Connect an AI Assistant (MCP)](https://videocataloger.com/docs/server/serving-media/mcp-server/?utm_source=github&utm_medium=referral&utm_campaign=fvc-mcp).

## Configuration

| Variable | Meaning |
|---|---|
| `FVC_SERVER_URL` | Address of your catalog server. Default `http://localhost:8754`. |
| `FVC_API_KEY` | The API key. |
| `FVC_MCP_LOG_LEVEL` | `Warning` by default; `Information` or `Debug` when a connection will not come up. Logs go to stderr. |

## Tools (23)

| Tool | What it does |
|---|---|
| `search_videos` | Find videos by free text or by keyword. |
| `search_scenes` | Find individual scenes, with their timecodes. |
| `search_scenes_semantic` | Find scenes by describing the picture, or scenes that look like a given one. Needs the visual search index and, for descriptions, the Visual search model on the server. |
| `search_transcripts` | Find spoken words, with their video and timecode. |
| `search_actors` | Find people by name or tag. |
| `list_keywords` | Every keyword in the catalog, so the assistant knows what it can search for. |
| `get_video` | One catalog entry in full. |
| `get_video_scenes` | A video's scene thumbnails in timecode order. |
| `get_video_transcript` | The transcript as timed lines. |
| `get_video_tags`, `get_video_actors` | Keywords and cast. |
| `get_scene_image` | The thumbnail image itself, so the assistant can see the shot. |
| `catalog_stats`, `list_bins`, `get_bin_videos` | Catalog size, the build serving it, and existing bins. |
| `tag_video`, `tag_scene` | Add keywords to a video or a single scene. Editor key. |
| `untag_video`, `untag_scene` | Remove named keywords again. Editor key. |
| `create_bin`, `add_video_to_bin` | Build a collection. Editor key. |
| `index_video`, `list_videos_needing_indexing` | Index catalog entries that have no thumbnails yet. Editor key. |

It cannot delete videos or bins, take a video out of a bin, play video or drive the player.

## Source

`src/FvcMcpServer` is the source of the `fvc-mcp.exe` shipped with Fast Video Cataloger, copied
from each release. It is a thin client over the Fast Video Cataloger server's REST API: it holds
no state and every tool is one or two REST calls. Build with the .NET 10 SDK:

```powershell
dotnet publish src/FvcMcpServer -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Released builds are code-signed by VideoStorm Sweden AB. Issues and pull requests are welcome; the
code here follows the version in Fast Video Cataloger, so accepted changes ship with the next
release.

## License

MIT for the source in this repository, see [LICENSE](LICENSE). Bundled packages:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Fast Video Cataloger itself is commercial
software with a free 30-day trial.
