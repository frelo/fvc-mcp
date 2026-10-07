using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FvcMcpServer
{
    /// <summary>
    /// The tools an assistant can call against a Fast Video Cataloger catalog.
    ///
    /// These cover searching and organising an existing catalog. Building the catalog -
    /// capturing single frames, driving the player, running a specific model over one
    /// thumbnail - stays with the C# scripting API inside the desktop application.
    /// </summary>
    [McpServerToolType]
    public class CatalogTools
    {
        /// <summary>
        /// Server-wide guidance sent to the assistant during the initialization handshake, for
        /// the things no single tool description can carry: which tool to reach for, the order
        /// they go in, and the four behaviours that silently produce an empty or misleading
        /// answer. It matters most when several connectors are configured at once and the
        /// assistant has to work out that a question about footage belongs here.
        ///
        /// The first paragraph is deliberately self-contained: hosts are documented as reading
        /// only the opening ~512 characters, so nothing below it may be load-bearing.
        /// </summary>
        public const string ServerInstructions = """
            These tools search and organise the user's own video catalog in Fast Video Cataloger: video files on their own machine, not cloud storage and not the file system. Answer any question about their footage, videos, clips or what is in them from these tools rather than from guesswork. Searches return catalog ids; pass those ids to the get_ tools for detail. To judge what a shot actually shows, call get_scene_image and look at the frame - titles and file names are often wrong or missing.

            Things that change the results:
            - Call list_keywords before a keyword search. A keyword that is not in this catalog matches nothing rather than reporting an error, so a narrowed search can come back empty for that reason alone.
            - search_transcripts matches words exactly as they were transcribed. There is no spelling correction, and only videos that have already been transcribed are searched at all.
            - Scenes are thumbnails sampled through a video at a fixed interval, not detected shot changes. A scene list shows how the video was sampled, not where the cuts are.
            - Tagging, bins and indexing need an API key with the Editor role; a Viewer key is refused. There is no tool to delete a bin or to take a video out of one, so confirm with the user before creating bins.

            Before helping the user report a problem with Fast Video Cataloger, call catalog_stats. It names the build serving this catalog: productVersion, buildChannel and buildDate. When buildChannel is anything other than "release" this is a pre-release build, so check whether the behaviour is already listed as fixed for that version before calling it a bug, and say which build it is in anything you write up. A buildChannel of "unknown" means the build predates this field, which makes it an old one.
            """;

        private readonly FvcClient _client;

        public CatalogTools(FvcClient client)
        {
            _client = client;
        }

        // ---------------------------------------------------------------- searching

        [McpServerTool(Name = "search_videos", ReadOnly = true, Title = "Search videos")]
        [Description("Search the user's Fast Video Cataloger catalog - their local library of video files, " +
                     "not cloud storage or the file system. Use this for any question about their videos, " +
                     "footage or clips. Use 'query' for a free text search over titles, descriptions and " +
                     "keywords, or 'keywords' for a comma-separated keyword match. Returns catalog entries " +
                     "including each video's id, title, path and length.")]
        public Task<string> SearchVideos(
            [Description("Free text to search for. Leave empty to browse with the other filters.")] string query = null,
            [Description("Comma-separated keywords, e.g. 'launch,apollo'. By default a video matches if it " +
                         "carries ANY of them; set matchAllKeywords to require every one.")] string keywords = null,
            [Description("Require EVERY keyword rather than any one of them. Use this to narrow a search, " +
                         "e.g. keywords 'beach,sunset' with matchAllKeywords true finds only videos that are " +
                         "both. A keyword that does not exist in the catalog is ignored rather than making " +
                         "the result empty, so check list_keywords if a narrowed search looks too broad.")] bool? matchAllKeywords = null,
            [Description("Comma-separated actor ids, to find the videos a person appears in. Get ids from " +
                         "search_actors. This is the reverse of get_video_actors.")] string actors = null,
            [Description("Only videos rated at least this (1-5).")] int? minRating = null,
            [Description("Maximum number of results. Defaults to 25.")] int? limit = 25,
            [Description("Skip this many results, for paging through a large set.")] int? offset = null,
            CancellationToken ct = default)
        {
            string q = FvcClient.Query(
                ("search", query),
                ("keywords", keywords),
                ("matchAllKeywords", matchAllKeywords),
                ("actors", actors),
                ("minRating", minRating),
                ("limit", limit),
                ("offset", offset));

            return _client.GetAsync("/api/v1/videos" + q, ct);
        }

        [McpServerTool(Name = "search_scenes", ReadOnly = true, Title = "Search scenes")]
        [Description("Search individual scenes inside the user's Fast Video Cataloger videos, rather than " +
                     "whole videos. This is how you find the moment inside a video: each result carries a " +
                     "thumbnail id, the video it belongs to, and its timecode. Pass the thumbnail id to " +
                     "get_scene_image to look at it.")]
        public Task<string> SearchScenes(
            [Description("Comma-separated keywords. A scene matches if it is tagged with ANY of them, not all " +
                         "of them - listing more keywords widens the search rather than narrowing it. Scene " +
                         "keywords come from object detection, so they are concrete things that appear in the " +
                         "frame ('person', 'laptop', 'car'), not themes or descriptions.")] string keywords = null,
            [Description("Free text to match against scene keywords.")] string query = null,
            [Description("Restrict the search to one video by its id.")] long? videoId = null,
            [Description("Maximum number of results. Defaults to 40.")] int? limit = 40,
            CancellationToken ct = default)
        {
            // With nothing to match on the server returns an empty result that serializes
            // to "{}", which reads as a broken tool rather than as "you asked for nothing".
            if (string.IsNullOrWhiteSpace(keywords) && string.IsNullOrWhiteSpace(query) && videoId == null)
            {
                throw new FvcException(
                    "Give search_scenes something to match: keywords, a query, or a videoId. " +
                    "To list every scene of one video, use get_video_scenes.");
            }

            string q = FvcClient.Query(
                ("keywords", keywords),
                ("search", query),
                ("videoId", videoId),
                ("limit", limit));

            return _client.GetAsync("/api/v1/thumbnails/search" + q, ct);
        }

        [McpServerTool(Name = "search_scenes_semantic", ReadOnly = true, Title = "Find scenes by description")]
        [Description("Find scenes by describing what is in the picture - 'a dog running on a beach', " +
                     "'close-up of hands typing', 'city skyline at night'. Nothing has to be tagged: every " +
                     "indexed scene is ranked by how well it matches the words, so use this when the " +
                     "keywords search_scenes needs do not cover what you are after, and use similarTo to " +
                     "find footage that looks like a scene you already have (the source scene itself is " +
                     "left out of those results). Results come best first, each with the thumbnail id, the " +
                     "video, the time (seconds and hh:mm:ss) and a relevance score. Judge by the order, not " +
                     "the number: for a description a good match scores only about 0.15-0.30, while scenes " +
                     "that look like a given one score 0.8 and above, and scores are comparable only within " +
                     "one search. Pass a thumbnail id to get_scene_image to check what a scene really shows. " +
                     "If the answer says the model or the index is missing, tell the user what to do; do " +
                     "not retry.")]
        public async Task<string> SearchScenesSemantic(
            [Description("What the scene should show, in plain words. Ignored when similarTo is given.")] string query = null,
            [Description("A thumbnail id: rank scenes by how much they look like this one instead of by a description.")] long? similarTo = null,
            [Description("Restrict the search to one video by its id.")] long? videoId = null,
            [Description("How many of the best matches to return. Defaults to 20, maximum 200.")] int? limit = 20,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query) && (similarTo ?? 0) <= 0)
                throw new FvcException("Give search_scenes_semantic a description (query) or a thumbnail id (similarTo).");
            int take = Math.Clamp(limit ?? 20, 1, 200);

            string json = await _client.GetAsync("/api/v1/thumbnails/semantic" + FvcClient.Query(
                ("query", query), ("similarTo", similarTo), ("videoId", videoId), ("limit", similarTo != null ? take + 1 : take)), ct);

            // The endpoint answers with the full scene search shape (scenes + the videos they belong
            // to). Hand the model a flat, ranked list instead: one line per scene, the video named,
            // and how many scenes were scored. No "narrow your search" advice: a real assistant run
            // read that as a truncation warning on every catalog-wide search.
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                long scored = 0;
                if (root.TryGetProperty("totalCount", out var tc) && tc.ValueKind == JsonValueKind.Number) scored = tc.GetInt64();
                var data = root.TryGetProperty("data", out var d) ? d : root;

                var titles = new Dictionary<long, string>();
                if (data.TryGetProperty("videoFiles", out var videos) && videos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in videos.EnumerateArray())
                    {
                        long id = v.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
                        string title = v.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                        if (string.IsNullOrWhiteSpace(title) && v.TryGetProperty("filePath", out var fp) && fp.ValueKind == JsonValueKind.String)
                            title = Path.GetFileName(fp.GetString());
                        if (id > 0) titles[id] = title ?? "";
                    }
                }

                var results = new List<object>();
                if (data.TryGetProperty("sceneThumbnails", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in scenes.EnumerateArray())
                    {
                        long thumbId = s.TryGetProperty("id", out var i) ? i.GetInt64() : 0;
                        if (similarTo != null && thumbId == similarTo.Value) continue;   // the source is not a find
                        long vid = s.TryGetProperty("videoFileID", out var vf) ? vf.GetInt64() : 0;
                        // frameTimeS is seconds (the catalog column is misnamed FrameTimeMS but holds seconds).
                        double seconds = s.TryGetProperty("frameTimeS", out var ft) && ft.ValueKind == JsonValueKind.Number ? ft.GetDouble() : 0;
                        double score = s.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0;
                        results.Add(new
                        {
                            thumbnailId = thumbId,
                            videoId = vid,
                            video = titles.TryGetValue(vid, out var title) ? title : null,
                            timeSeconds = Math.Round(seconds, 1),
                            time = TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"),
                            relevance = Math.Round(score, 3),
                        });
                    }
                }
                return JsonSerializer.Serialize(new
                {
                    scenesScored = scored,
                    returned = results.Count,
                    results,
                });
            }
            catch (JsonException)
            {
                return json;
            }
        }

        [McpServerTool(Name = "search_transcripts", ReadOnly = true, Title = "Search spoken words")]
        [Description("Search what is said out loud in the user's Fast Video Cataloger videos. Returns " +
                     "the matching transcript " +
                     "lines themselves - each with the id of the video it belongs to (videoFileID) " +
                     "and its start and end time in seconds, so you can go straight to the moment. " +
                     "Use get_video_transcript to read the lines around a hit. Only videos that have " +
                     "been transcribed are searched.")]
        public async Task<string> SearchTranscripts(
            [Description("The words to look for in the transcripts.")] string query,
            [Description("Maximum number of matching lines to return. Defaults to 25, maximum 200.")] int? limit = 25,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new FvcException("Provide the words to search for.");

            // This endpoint answers with the ids of the matching transcript LINES - not, as the
            // name suggests, video ids. Handing those back raw is useless to a caller: they look
            // like video ids and silently resolve to the wrong videos, or to none at all. So
            // resolve each one into the line it points at before returning.
            string idJson = await _client.GetAsync(
                "/api/v1/subtitles/search" + FvcClient.Query(("search", query)), ct);

            long[] ids;
            try
            {
                using var doc = JsonDocument.Parse(idJson);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                    root = data;

                if (root.ValueKind != JsonValueKind.Array)
                    return "{\"totalCount\":0,\"data\":[]}";

                var found = new List<long>();
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long id))
                        found.Add(id);
                }
                ids = found.ToArray();
            }
            catch (JsonException)
            {
                return idJson;
            }

            int take = Math.Clamp(limit ?? 25, 1, 200);

            var lines = new List<string>();
            foreach (long id in ids)
            {
                if (lines.Count >= take)
                    break;

                // A stale index can name a line that no longer exists. Skipping the miss keeps
                // the rest of the results usable instead of failing the whole search.
                try
                {
                    lines.Add(await _client.GetAsync("/api/v1/subtitles/" + id, ct));
                }
                catch (FvcException)
                {
                }
            }

            return $"{{\"totalCount\":{ids.Length},\"data\":[{string.Join(",", lines)}]}}";
        }

        [McpServerTool(Name = "search_actors", ReadOnly = true, Title = "Search actors")]
        [Description("Search the people recorded in the user's Fast Video Cataloger catalog, by name or " +
                     "by the tags on them. These are the cast of their own footage, not contacts.")]
        public Task<string> SearchActors(
            [Description("Free text to match against actor names.")] string query = null,
            [Description("Comma-separated tags the actor must have.")] string tags = null,
            [Description("Maximum number of people to return. Defaults to 50.")] int? limit = 50,
            CancellationToken ct = default)
        {
            string q = FvcClient.Query(("search", query), ("tags", tags), ("limit", limit));
            return _client.GetAsync("/api/v1/actors" + q, ct);
        }

        // ---------------------------------------------------------------- reading

        [McpServerTool(Name = "get_video", ReadOnly = true, Title = "Get a video")]
        [Description("Get one catalog entry by its id, including title, description, path, length and rating.")]
        public Task<string> GetVideo(
            [Description("The video's catalog id.")] long videoId,
            CancellationToken ct = default)
        {
            return _client.GetAsync($"/api/v1/videos/{videoId}", ct);
        }

        [McpServerTool(Name = "get_video_scenes", ReadOnly = true, Title = "Get a video's scenes")]
        [Description("List the scene thumbnails of one video in timecode order. Use this to see how a " +
                     "video is structured before deciding which moment to look at. Returns totalCount " +
                     "so you can tell whether there are more than you asked for.")]
        public Task<string> GetVideoScenes(
            [Description("The video's catalog id.")] long videoId,
            [Description("Maximum number of scenes. Defaults to 100.")] int? limit = 100,
            [Description("Skip this many scenes, for paging through a long video.")] int? offset = null,
            CancellationToken ct = default)
        {
            string q = FvcClient.Query(("limit", limit), ("offset", offset));
            return _client.GetAsync($"/api/v1/videos/{videoId}/thumbnails" + q, ct);
        }

        [McpServerTool(Name = "get_video_transcript", ReadOnly = true, Title = "Get a transcript")]
        [Description("Get the transcript of a video as timed lines. Empty if the video has not been " +
                     "transcribed. A feature-length transcript is very long, so this returns the first " +
                     "200 lines by default - check totalCount and page with offset if you need the rest.")]
        public Task<string> GetVideoTranscript(
            [Description("The video's catalog id.")] long videoId,
            [Description("Maximum number of transcript lines. Defaults to 200.")] int? limit = 200,
            [Description("Skip this many lines, for reading further into a long transcript.")] int? offset = null,
            CancellationToken ct = default)
        {
            string q = FvcClient.Query(("limit", limit), ("offset", offset));
            return _client.GetAsync($"/api/v1/videos/{videoId}/subtitles" + q, ct);
        }

        [McpServerTool(Name = "list_keywords", ReadOnly = true, Title = "List the catalog's keywords")]
        [Description("List every keyword that exists in the whole Fast Video Cataloger catalog, rather " +
                     "than the keywords on one video. Use it to find out what you can actually search " +
                     "for before guessing - a keyword that is not in the catalog simply returns nothing. " +
                     "This is the vocabulary only, with no counts, and it mixes keywords the user typed " +
                     "with ones written automatically by object detection, so a catalog can list things " +
                     "like 'zebra' or 'toaster' that appear nowhere in the footage. Both scopes can " +
                     "contain either kind. To find out whether a keyword is really used, search for it.")]
        public Task<string> ListKeywords(
            [Description("Which keywords to list: 'videos' for keywords on videos, 'scenes' for keywords " +
                         "on scene thumbnails. Defaults to videos.")] string scope = "videos",
            CancellationToken ct = default)
        {
            string path = string.Equals(scope, "scenes", StringComparison.OrdinalIgnoreCase)
                ? "/api/v1/tags/thumbnail"
                : "/api/v1/tags/video";

            return _client.GetAsync(path, ct);
        }

        [McpServerTool(Name = "get_video_tags", ReadOnly = true, Title = "Get a video's tags")]
        [Description("List the keywords a video is tagged with.")]
        public Task<string> GetVideoTags(
            [Description("The video's catalog id.")] long videoId,
            CancellationToken ct = default)
        {
            return _client.GetAsync($"/api/v1/videos/{videoId}/tags", ct);
        }

        [McpServerTool(Name = "get_video_actors", ReadOnly = true, Title = "Get a video's cast")]
        [Description("List the people recorded as appearing in a video.")]
        public Task<string> GetVideoActors(
            [Description("The video's catalog id.")] long videoId,
            CancellationToken ct = default)
        {
            return _client.GetAsync($"/api/v1/videos/{videoId}/actors", ct);
        }

        [McpServerTool(Name = "get_scene_image", ReadOnly = true, Title = "Look at a scene")]
        [Description("Fetch the actual image of a scene thumbnail so you can see what is in the shot. " +
                     "Get thumbnail ids from search_scenes, search_scenes_semantic or get_video_scenes.")]
        public async Task<ImageContentBlock> GetSceneImage(
            [Description("The thumbnail's id.")] long thumbnailId,
            CancellationToken ct = default)
        {
            var (bytes, mimeType) = await _client.GetBytesAsync($"/api/v1/thumbnails/{thumbnailId}/image", ct);
            return ImageContentBlock.FromBytes(bytes, mimeType);
        }

        [McpServerTool(Name = "catalog_stats", ReadOnly = true, Title = "Catalog size")]
        [Description("How many videos, actors and images the Fast Video Cataloger catalog holds. This is " +
                     "the user's local video library, not cloud storage or the file system. Useful as a " +
                     "first call to confirm the connection works and to judge how broad a search should be.")]
        public Task<string> CatalogStats(CancellationToken ct = default)
        {
            return _client.GetAsync("/api/v1/stats", ct);
        }

        [McpServerTool(Name = "list_bins", ReadOnly = true, Title = "List bins")]
        [Description("List the bins in the user's Fast Video Cataloger catalog. A bin is a user-made " +
                     "collection of videos, like a folder of shortlisted clips.")]
        public Task<string> ListBins(CancellationToken ct = default)
        {
            return _client.GetAsync("/api/v1/bins", ct);
        }

        [McpServerTool(Name = "get_bin_videos", ReadOnly = true, Title = "Get a bin's videos")]
        [Description("List the videos collected in one bin. A bin can hold the whole catalog, so " +
                     "this returns the first 50 by default - check totalCount and page with offset.")]
        public Task<string> GetBinVideos(
            [Description("The bin's id.")] long binId,
            [Description("Maximum number of videos. Defaults to 50.")] int? limit = 50,
            [Description("Skip this many videos, for paging through a large bin.")] int? offset = null,
            CancellationToken ct = default)
        {
            string q = FvcClient.Query(("limit", limit), ("offset", offset));
            return _client.GetAsync($"/api/v1/bins/{binId}/videos" + q, ct);
        }

        // ---------------------------------------------------------------- writing

        [McpServerTool(Name = "tag_video", Destructive = false, Title = "Tag a video")]
        [Description("Add keywords to a video. Existing tags are kept. Needs an API key with the Editor role.")]
        public Task<string> TagVideo(
            [Description("The video's catalog id.")] long videoId,
            [Description("Comma-separated keywords to add, e.g. 'trailer,apollo'.")] string tags,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(tags))
                throw new FvcException("Provide at least one keyword to add.");

            return _client.PutAsync($"/api/v1/videos/{videoId}/tags", new { tags }, ct);
        }

        [McpServerTool(Name = "untag_video", Destructive = true, Title = "Remove a video's keywords")]
        [Description("Remove keywords from a video - the counterpart to tag_video, for undoing a tag " +
                     "that was added in error. Name the keywords exactly as get_video_tags reports " +
                     "them; only those are removed and every other tag on the video is left alone. " +
                     "Other videos keep the keyword, but a keyword that no video uses any more also " +
                     "disappears from the catalog's keyword list. Returns what was removed and what " +
                     "did not match. Needs an API key with the Editor role.")]
        public Task<string> UntagVideo(
            [Description("The video's catalog id.")] long videoId,
            [Description("Comma-separated keywords to remove, named exactly as they appear on the video.")] string tags,
            CancellationToken ct = default)
        {
            return RemoveTagsAsync($"/api/v1/videos/{videoId}/tags",
                                   instanceId => $"/api/v1/videos/{videoId}/tags/{instanceId}",
                                   tags, ct);
        }

        [McpServerTool(Name = "untag_scene", Destructive = true, Title = "Remove a scene's keywords")]
        [Description("Remove keywords from a single scene thumbnail - the counterpart to tag_scene. " +
                     "Only the named keywords are removed from that one scene; other scenes and the " +
                     "video's own tags are untouched. A keyword that no scene uses any more also " +
                     "disappears from the catalog's scene keyword list. Returns what was removed and " +
                     "what did not match. Needs an API key with the Editor role.")]
        public Task<string> UntagScene(
            [Description("The thumbnail's id.")] long thumbnailId,
            [Description("Comma-separated keywords to remove, named exactly as they appear on the scene.")] string tags,
            CancellationToken ct = default)
        {
            return RemoveTagsAsync($"/api/v1/thumbnails/{thumbnailId}/tags",
                                   instanceId => $"/api/v1/thumbnails/{thumbnailId}/tags/{instanceId}",
                                   tags, ct);
        }

        /// <summary>
        /// Shared body of the untag tools. The REST API removes a tag by its instance id, but a
        /// caller only knows the keyword it wrote, so resolve names to instances here rather than
        /// making every caller fetch and match them. Matching is by exact name, case-insensitive:
        /// no wildcards and no "remove everything", so a mistake costs one keyword rather than a
        /// video's whole keyword set.
        /// </summary>
        private async Task<string> RemoveTagsAsync(
            string readPath, Func<long, string> deletePath, string tags, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(tags))
                throw new FvcException("Name at least one keyword to remove.");

            var wanted = new List<string>();
            foreach (string part in tags.Split(','))
            {
                string name = part.Trim();
                if (name.Length > 0)
                    wanted.Add(name);
            }

            if (wanted.Count == 0)
                throw new FvcException("Name at least one keyword to remove.");

            string current = await _client.GetAsync(readPath, ct);

            var matches = new List<(long id, string name)>();
            try
            {
                using var doc = JsonDocument.Parse(current);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                    root = data;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in root.EnumerateArray())
                    {
                        if (!element.TryGetProperty("name", out var nameProp) ||
                            !element.TryGetProperty("id", out var idProp))
                            continue;

                        string name = nameProp.GetString() ?? "";
                        foreach (string want in wanted)
                        {
                            if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase) &&
                                idProp.TryGetInt64(out long instanceId))
                            {
                                matches.Add((instanceId, name));
                                break;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                throw new FvcException("Could not read the current keywords back, so nothing was removed.");
            }

            var removed = new List<string>();
            foreach (var (id, name) in matches)
            {
                await _client.DeleteAsync(deletePath(id), ct);
                removed.Add(name);
            }

            var notFound = new List<string>();
            foreach (string want in wanted)
            {
                bool hit = false;
                foreach (var (_, name) in matches)
                {
                    if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = true;
                        break;
                    }
                }
                if (!hit)
                    notFound.Add(want);
            }

            return JsonSerializer.Serialize(new { removed, notFound });
        }

        [McpServerTool(Name = "tag_scene", Destructive = false, Title = "Tag a scene")]
        [Description("Add keywords to a single scene thumbnail rather than the whole video. " +
                     "Needs an API key with the Editor role.")]
        public Task<string> TagScene(
            [Description("The thumbnail's id.")] long thumbnailId,
            [Description("Comma-separated keywords to add.")] string tags,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(tags))
                throw new FvcException("Provide at least one keyword to add.");

            return _client.PostAsync($"/api/v1/thumbnails/{thumbnailId}/tags", new { tags }, ct);
        }

        [McpServerTool(Name = "create_bin", Destructive = false, Title = "Create a bin")]
        [Description("Create a new bin to collect videos in. Returns the new bin including its id. " +
                     "Needs an API key with the Editor role.")]
        public Task<string> CreateBin(
            [Description("Name for the bin, e.g. 'trailer candidates'.")] string label,
            [Description("Id of a parent bin, to nest this one inside it.")] long? parentId = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new FvcException("Provide a name for the bin.");

            return _client.PostAsync("/api/v1/bins", new { label, parentId }, ct);
        }

        [McpServerTool(Name = "add_video_to_bin", Destructive = false, Title = "Add a video to a bin")]
        [Description("Put a video into a bin. Needs an API key with the Editor role.")]
        public Task<string> AddVideoToBin(
            [Description("The bin's id.")] long binId,
            [Description("The video's catalog id.")] long videoId,
            CancellationToken ct = default)
        {
            return _client.PostAsync($"/api/v1/bins/{binId}/videos", new { videoId }, ct);
        }

        [McpServerTool(Name = "index_video", Destructive = false, Title = "Index a video")]
        [Description("Ask the server to index a video that is in the catalog but has no scene " +
                     "thumbnails yet. Extracts thumbnails and duration, plus whatever else the server " +
                     "is configured for such as transcription. This takes minutes and runs in the " +
                     "background - poll get_video_scenes to follow it. Needs the Editor role.")]
        public Task<string> IndexVideo(
            [Description("The video's catalog id.")] long videoId,
            CancellationToken ct = default)
        {
            return _client.PostAsync($"/api/v1/videos/{videoId}/index", null, ct);
        }

        [McpServerTool(Name = "list_videos_needing_indexing", ReadOnly = true, Title = "List unindexed videos")]
        [Description("List catalog entries that have no scene thumbnails or no known length yet. " +
                     "Pass each id to index_video to work through them. A newly added folder can " +
                     "leave thousands pending, so this returns the first 50 by default - check " +
                     "totalCount and page with offset to walk the rest.")]
        public Task<string> ListVideosNeedingIndexing(
            [Description("Maximum number of entries. Defaults to 50.")] int? limit = 50,
            [Description("Skip this many entries, for working through a long backlog.")] int? offset = null,
            CancellationToken ct = default)
        {
            string q = FvcClient.Query(("limit", limit), ("offset", offset));
            return _client.GetAsync("/api/v1/videos/pending-indexing" + q, ct);
        }
    }
}
