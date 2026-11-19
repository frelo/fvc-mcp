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
            - Tagging, editing a video's details, cast, bins and indexing need an API key with the Editor role; a Viewer key is refused. There is no tool to delete a bin, so confirm with the user before creating bins. delete_keyword, rename_keyword and delete_actor change the whole catalog and cannot be undone: say what they touch and get the user's go-ahead first.

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
            [Description("Comma-separated colour labels the user gave videos; a video matches any of them. " +
                         "One of orange, apricot, yellow, lime, olive, green, teal, navy, blue, purple, " +
                         "violet, pink, tan, beige, brown, chocolate, or 'none' for videos without a colour. " +
                         "Not combined with 'query'.")] string color = null,
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
                ("color", color),
                ("limit", limit),
                ("offset", offset));

            return _client.GetAsync("/api/v1/videos" + q, ct);
        }

        [McpServerTool(Name = "search_scenes", ReadOnly = true, Title = "Search scenes")]
        [Description("Search individual scenes inside the user's Fast Video Cataloger videos, rather than " +
                     "whole videos. This is how you find the moment inside a video: each result carries a " +
                     "thumbnail id, the video it belongs to, and its timecode, and textOnScreen with the " +
                     "words read on screen in it when there are any. Pass the thumbnail id to " +
                     "get_scene_image to look at it.")]
        public Task<string> SearchScenes(
            [Description("Comma-separated keywords. A scene matches if it is tagged with ANY of them, not all " +
                         "of them - listing more keywords widens the search rather than narrowing it. Scene " +
                         "keywords come from object detection, so they are concrete things that appear in the " +
                         "frame ('person', 'laptop', 'car'), not themes or descriptions.")] string keywords = null,
            [Description("Free text to match against scene keywords.")] string query = null,
            [Description("Words shown on screen in the scene: titles, captions, slides, signs, lower thirds. " +
                         "Matched as whole words (a trailing * matches a prefix) against the text read in each " +
                         "frame by OCR. Only videos indexed with Text on screen turned on, or read afterwards, " +
                         "have any; OCR misreads some words, so try a shorter or different word before " +
                         "concluding the text is not there.")] string text = null,
            [Description("Restrict the search to one video by its id.")] long? videoId = null,
            [Description("Maximum number of results. Defaults to 40.")] int? limit = 40,
            CancellationToken ct = default)
        {
            // With nothing to match on the server returns an empty result that serializes
            // to "{}", which reads as a broken tool rather than as "you asked for nothing".
            // A videoId alone is the same: it narrows a search, it does not list the video.
            if (string.IsNullOrWhiteSpace(keywords) && string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(text))
            {
                throw new FvcException(
                    "Give search_scenes something to match: keywords, a query or text, with videoId to " +
                    "stay within one video. To list every scene of one video, use get_video_scenes.");
            }

            string q = FvcClient.Query(
                ("keywords", keywords),
                ("search", query),
                ("text", text),
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
                        var result = new Dictionary<string, object>
                        {
                            ["thumbnailId"] = thumbId,
                            ["videoId"] = vid,
                            ["video"] = titles.TryGetValue(vid, out var title) ? title : null,
                            ["timeSeconds"] = Math.Round(seconds, 1),
                            ["time"] = TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"),
                            ["relevance"] = Math.Round(score, 3),
                        };
                        // Only on scenes with text, so the list stays one short line per scene
                        if (s.TryGetProperty("textOnScreen", out var tos) && tos.ValueKind == JsonValueKind.String)
                            result["textOnScreen"] = tos.GetString();
                        results.Add(result);
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
                     "been transcribed are searched. A line's speaker, when set, says who said it, as in " +
                     "get_video_transcript. Give 'actor' to find what one person said.")]
        public async Task<string> SearchTranscripts(
            [Description("The words to look for in the transcripts.")] string query,
            [Description("An actor id from search_actors: only the lines said by a speaker named as that " +
                         "actor. Speakers not named as an actor are not found this way.")] long? actor = null,
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
                "/api/v1/subtitles/search" + FvcClient.Query(("search", query), ("actorId", actor > 0 ? actor.ToString() : null)), ct);

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
                     "video is structured before deciding which moment to look at. A scene whose text on " +
                     "screen has been read carries it as textOnScreen (titles, captions, slides; OCR, so " +
                     "some words are misread). Returns totalCount so you can tell whether there are more " +
                     "than you asked for.")]
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
                     "200 lines by default - check totalCount and page with offset if you need the rest. " +
                     "When the video has more than one voice, each line's speaker says who speaks it: an " +
                     "actor's name, a name the user gave, or 'Speaker 1', 'Speaker 2' for voices told apart " +
                     "but not named. Voices are told apart by sound alone and can be wrong on short lines. " +
                     "speaker is null when the video has one voice or was transcribed before version 10.6.")]
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
                     "contain either kind. To find out whether a keyword is really used, call keyword_usage.")]
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

        [McpServerTool(Name = "remove_video_from_bin", Destructive = true, Title = "Take a video out of a bin")]
        [Description("Take a video out of a bin - the counterpart to add_video_to_bin. The video stays in " +
                     "the catalog and in any other bin it is in. Nothing happens if it was not in that " +
                     "bin. Needs an API key with the Editor role.")]
        public Task<string> RemoveVideoFromBin(
            [Description("The bin's id.")] long binId,
            [Description("The video's catalog id.")] long videoId,
            CancellationToken ct = default)
        {
            return _client.DeleteAsync($"/api/v1/bins/{binId}/videos/{videoId}", ct);
        }

        [McpServerTool(Name = "update_video", Destructive = true, Title = "Edit a video's details")]
        [Description("Change a video's title, description, link or rating. Only the fields you pass are " +
                     "changed; leave a field out to keep it. Pass an empty string to clear the " +
                     "description or the link. The new text replaces the old, so to add to a " +
                     "description read it with get_video first and pass the whole text. Returns the " +
                     "video as it is after the change. Needs an API key with the Editor role.")]
        public async Task<string> UpdateVideo(
            [Description("The video's catalog id.")] long videoId,
            [Description("New title. Cannot be empty.")] string title = null,
            [Description("New description. An empty string clears it.")] string description = null,
            [Description("New link, e.g. a web page about the video. An empty string clears it.")] string link = null,
            [Description("New rating, 0 to 5. 0 removes the rating.")] int? rating = null,
            CancellationToken ct = default)
        {
            if (title == null && description == null && link == null && rating == null)
                throw new FvcException("Pass at least one of title, description, link or rating.");

            if (title != null && title.Trim().Length == 0)
                throw new FvcException("A title cannot be empty. Leave title out to keep the current one.");

            if (rating is < 0 or > 5)
                throw new FvcException("A rating goes from 0 to 5.");

            // The update endpoint answers OK for an id that matches no video, so check first.
            await _client.GetAsync($"/api/v1/videos/{videoId}", ct);

            await _client.PutAsync($"/api/v1/videos/{videoId}", new { title, description, link, rating }, ct);

            return await _client.GetAsync($"/api/v1/videos/{videoId}", ct);
        }

        [McpServerTool(Name = "add_actor_to_video", Destructive = false, Title = "Add a person to a video's cast")]
        [Description("Record that a person appears in a video. Get actor ids from search_actors; the " +
                     "person must already exist in the catalog. Nothing changes if they are already in " +
                     "the cast. Needs an API key with the Editor role.")]
        public async Task<string> AddActorToVideo(
            [Description("The video's catalog id.")] long videoId,
            [Description("The actor's id, from search_actors.")] long actorId,
            CancellationToken ct = default)
        {
            // Check both ends exist: the server inserts whatever ids it is given, and it adds a
            // second cast row for a person who is already in the cast.
            await _client.GetAsync($"/api/v1/videos/{videoId}", ct);
            await _client.GetAsync($"/api/v1/actors/{actorId}", ct);

            if (await IsInCastAsync(videoId, actorId, ct))
                return JsonSerializer.Serialize(new { added = false, reason = "Already in the cast." });

            await _client.PostAsync($"/api/v1/videos/{videoId}/cast/{actorId}", null, ct);
            return JsonSerializer.Serialize(new { added = true });
        }

        [McpServerTool(Name = "remove_actor_from_video", Destructive = true, Title = "Remove a person from a video's cast")]
        [Description("Remove a person from one video's cast - the counterpart to add_actor_to_video. The " +
                     "person stays in the catalog and in the cast of every other video. Needs an API key " +
                     "with the Editor role.")]
        public async Task<string> RemoveActorFromVideo(
            [Description("The video's catalog id.")] long videoId,
            [Description("The actor's id, as get_video_actors reports it.")] long actorId,
            CancellationToken ct = default)
        {
            if (!await IsInCastAsync(videoId, actorId, ct))
                return JsonSerializer.Serialize(new { removed = false, reason = "Not in this video's cast." });

            await _client.DeleteAsync($"/api/v1/videos/{videoId}/cast/{actorId}", ct);
            return JsonSerializer.Serialize(new { removed = true });
        }

        private async Task<bool> IsInCastAsync(long videoId, long actorId, CancellationToken ct)
        {
            string cast = await _client.GetAsync($"/api/v1/videos/{videoId}/actors", ct);
            try
            {
                using var doc = JsonDocument.Parse(cast);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (var actor in doc.RootElement.EnumerateArray())
                {
                    if (actor.TryGetProperty("id", out var id) && id.TryGetInt64(out long value) && value == actorId)
                        return true;
                }
            }
            catch (JsonException)
            {
                throw new FvcException("Could not read the video's cast, so nothing was changed.");
            }
            return false;
        }

        // ---------------------------------------------------------------- keywords and people

        [McpServerTool(Name = "keyword_usage", ReadOnly = true, Title = "Count a keyword's use")]
        [Description("How many videos and how many scenes carry one keyword. Use it to tell a keyword " +
                     "that is really used from one that only sits in the list, and before rename_keyword " +
                     "or delete_keyword so the user knows what the change touches. Name the keyword " +
                     "exactly as list_keywords reports it.")]
        public async Task<string> KeywordUsage(
            [Description("The keyword, exactly as list_keywords reports it.")] string keyword,
            CancellationToken ct = default)
        {
            var (id, name) = await FindKeywordAsync(keyword, ct);
            var (videos, scenes) = await KeywordUseAsync(id, ct);
            return JsonSerializer.Serialize(new { keyword = name, videos, scenes });
        }

        [McpServerTool(Name = "rename_keyword", Destructive = true, Title = "Rename a keyword")]
        [Description("Rename a keyword everywhere it is used, on videos and on scenes alike. Refused when " +
                     "another keyword already has the new name, so two keywords are never merged by " +
                     "accident. Tell the user how many videos and scenes it is on (keyword_usage) and " +
                     "confirm with them first. Needs an API key with the Editor role.")]
        public async Task<string> RenameKeyword(
            [Description("The keyword to rename, exactly as list_keywords reports it.")] string keyword,
            [Description("The new name.")] string newName,
            CancellationToken ct = default)
        {
            string to = newName?.Trim();
            if (string.IsNullOrEmpty(to))
                throw new FvcException("Give the new name.");
            if (to.Contains(','))
                throw new FvcException("A keyword name cannot contain a comma.");

            var (id, name) = await FindKeywordAsync(keyword, ct);
            foreach (string path in new[] { "/api/v1/tags/video", "/api/v1/tags/thumbnail" })
            {
                foreach (var (otherId, otherName) in await ReadNamedAsync(path, "tagID", ct))
                {
                    if (otherId != id && string.Equals(otherName, to, StringComparison.OrdinalIgnoreCase))
                        throw new FvcException($"The catalog already has a keyword '{otherName}', so '{name}' was not renamed.");
                }
            }

            await _client.PutAsync($"/api/v1/tags/{id}", new { name = to }, ct);
            return JsonSerializer.Serialize(new { renamed = name, to });
        }

        [McpServerTool(Name = "delete_keyword", Destructive = true, Title = "Delete a keyword")]
        [Description("Delete one keyword from the catalog: it is taken off every video, every scene or " +
                     "both, and cannot be undone. One keyword at a time, named exactly; there are no " +
                     "wildcards. Before calling this, tell the user how many videos and scenes carry " +
                     "it (keyword_usage) and get their go-ahead. To take a keyword off one video, use " +
                     "untag_video instead. Returns how many videos and scenes lost it. Needs an API key " +
                     "with the Editor role.")]
        public async Task<string> DeleteKeyword(
            [Description("The keyword, exactly as list_keywords reports it.")] string keyword,
            [Description("Where to delete it from: 'videos', 'scenes' or 'both'. Defaults to videos. A " +
                         "keyword deleted from one stays in the catalog while the other still uses it.")] string scope = "videos",
            CancellationToken ct = default)
        {
            string where = string.IsNullOrWhiteSpace(scope) ? "videos" : scope.Trim().ToLowerInvariant();
            if (where is not ("videos" or "scenes" or "both"))
                throw new FvcException("scope is one of 'videos', 'scenes' or 'both'.");
            bool videos = where != "scenes";
            bool scenes = where != "videos";

            var (id, name) = await FindKeywordAsync(keyword, ct);
            var (videoUses, sceneUses) = await KeywordUseAsync(id, ct);

            if (videos)
                await _client.DeleteAsync($"/api/v1/tags/{id}", ct);
            if (scenes)
                await _client.DeleteAsync($"/api/v1/tags/thumbnail/{id}", ct);

            return JsonSerializer.Serialize(new
            {
                deleted = name,
                videosUntagged = videos ? videoUses : 0,
                scenesUntagged = scenes ? sceneUses : 0,
            });
        }

        [McpServerTool(Name = "list_keyword_groups", ReadOnly = true, Title = "List keyword groups")]
        [Description("List the keyword groups the user sorted their video keywords into, such as " +
                     "'Places' or 'Clients', each with the keywords in it.")]
        public async Task<string> ListKeywordGroups(CancellationToken ct = default)
        {
            var groups = new List<object>();
            foreach (var (id, name) in await ReadNamedAsync("/api/v1/tags/groups", "groupID", ct))
            {
                var keywords = new List<string>();
                foreach (var (_, keywordName) in await ReadNamedAsync($"/api/v1/tags/groups/{id}/video", "tagID", ct))
                    keywords.Add(keywordName);
                groups.Add(new { groupId = id, name, keywords });
            }
            return JsonSerializer.Serialize(groups);
        }

        [McpServerTool(Name = "create_keyword_group", Destructive = false, Title = "Create a keyword group")]
        [Description("Create a keyword group to sort video keywords into. Nothing changes if a group " +
                     "with that name exists; its id is returned either way. Needs an API key with the " +
                     "Editor role.")]
        public async Task<string> CreateKeywordGroup(
            [Description("Name for the group, e.g. 'Places'.")] string name,
            CancellationToken ct = default)
        {
            string wanted = name?.Trim();
            if (string.IsNullOrEmpty(wanted))
                throw new FvcException("Provide a name for the group.");

            foreach (var (id, existing) in await ReadNamedAsync("/api/v1/tags/groups", "groupID", ct))
            {
                if (string.Equals(existing, wanted, StringComparison.OrdinalIgnoreCase))
                    return JsonSerializer.Serialize(new { created = false, groupId = id, name = existing });
            }

            string idJson = await _client.PostAsync("/api/v1/tags/groups", new { name = wanted }, ct);
            long.TryParse(idJson.Trim(), out long groupId);
            return JsonSerializer.Serialize(new { created = true, groupId, name = wanted });
        }

        [McpServerTool(Name = "add_keyword_to_group", Destructive = false, Title = "Put a keyword in a group")]
        [Description("Put a video keyword into a keyword group. Get group ids from list_keyword_groups. " +
                     "Needs an API key with the Editor role.")]
        public async Task<string> AddKeywordToGroup(
            [Description("The group's id, from list_keyword_groups.")] long groupId,
            [Description("The keyword, exactly as list_keywords reports it.")] string keyword,
            CancellationToken ct = default)
        {
            var (id, name) = await FindKeywordAsync(keyword, ct);
            await _client.PostAsync($"/api/v1/tags/groups/{groupId}/tags/{id}", null, ct);
            return JsonSerializer.Serialize(new { added = name, groupId });
        }

        [McpServerTool(Name = "remove_keyword_from_group", Destructive = true, Title = "Take a keyword out of a group")]
        [Description("Take a keyword out of a keyword group - the counterpart to add_keyword_to_group. The " +
                     "keyword stays in the catalog and on its videos. Needs an API key with the Editor role.")]
        public async Task<string> RemoveKeywordFromGroup(
            [Description("The group's id, from list_keyword_groups.")] long groupId,
            [Description("The keyword, exactly as list_keywords reports it.")] string keyword,
            CancellationToken ct = default)
        {
            var (id, name) = await FindKeywordAsync(keyword, ct);
            await _client.DeleteAsync($"/api/v1/tags/groups/{groupId}/tags/{id}", ct);
            return JsonSerializer.Serialize(new { removed = name, groupId });
        }

        [McpServerTool(Name = "rename_keyword_group", Destructive = true, Title = "Rename a keyword group")]
        [Description("Rename a keyword group. Its keywords stay in it. Refused when another group already " +
                     "has the new name. Needs an API key with the Editor role.")]
        public async Task<string> RenameKeywordGroup(
            [Description("The group's id, from list_keyword_groups.")] long groupId,
            [Description("The new name.")] string newName,
            CancellationToken ct = default)
        {
            string to = newName?.Trim();
            if (string.IsNullOrEmpty(to))
                throw new FvcException("Give the new name.");

            var groups = await ReadNamedAsync("/api/v1/tags/groups", "groupID", ct);
            string name = GroupName(groups, groupId);
            foreach (var (id, existing) in groups)
            {
                if (id != groupId && string.Equals(existing, to, StringComparison.OrdinalIgnoreCase))
                    throw new FvcException($"The catalog already has a keyword group '{existing}', so '{name}' was not renamed.");
            }

            await _client.PutAsync($"/api/v1/tags/groups/{groupId}", new { name = to }, ct);
            return JsonSerializer.Serialize(new { renamed = name, to, groupId });
        }

        [McpServerTool(Name = "delete_keyword_group", Destructive = true, Title = "Delete a keyword group")]
        [Description("Delete a keyword group. Only the grouping goes: its keywords stay in the catalog and " +
                     "on their videos and scenes. Pass the group's name as well as the id: the delete is " +
                     "refused when the two do not match. Tell the user which keywords are in it " +
                     "(list_keyword_groups) and confirm first. Needs an API key with the Editor role.")]
        public async Task<string> DeleteKeywordGroup(
            [Description("The group's id, from list_keyword_groups.")] long groupId,
            [Description("The group's name as list_keyword_groups reports it, to confirm the id.")] string name,
            CancellationToken ct = default)
        {
            string actual = GroupName(await ReadNamedAsync("/api/v1/tags/groups", "groupID", ct), groupId);
            if (!string.Equals(actual, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new FvcException($"Keyword group {groupId} is '{actual}', not '{name}', so nothing was deleted.");

            int keywords = (await ReadNamedAsync($"/api/v1/tags/groups/{groupId}/video", "tagID", ct)).Count;
            await _client.DeleteAsync($"/api/v1/tags/groups/{groupId}", ct);
            return JsonSerializer.Serialize(new { deleted = actual, groupId, keywordsUngrouped = keywords });
        }

        static string GroupName(List<(long id, string name)> groups, long groupId)
        {
            foreach (var (id, name) in groups)
                if (id == groupId)
                    return name;
            throw new FvcException($"There is no keyword group {groupId}. Check list_keyword_groups for the id.");
        }

        [McpServerTool(Name = "create_actor", Destructive = false, Title = "Add a person to the catalog")]
        [Description("Add a person to the catalog, so they can be put in a video's cast with " +
                     "add_actor_to_video. Nothing is added when someone with exactly that name exists; " +
                     "their id is returned instead. Returns the person with their id. Needs an API key " +
                     "with the Editor role.")]
        public async Task<string> CreateActor(
            [Description("First name, or the whole name for a person known by one.")] string firstName,
            [Description("Last name.")] string lastName = null,
            [Description("A few lines about the person.")] string bio = null,
            [Description("A link, e.g. a web page about the person.")] string link = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(firstName))
                throw new FvcException("Provide at least a first name.");

            string full = FullName(firstName, lastName);
            string found = await _client.GetAsync("/api/v1/actors" + FvcClient.Query(("search", full), ("limit", 50)), ct);
            foreach (var actor in ActorSummaries(found))
            {
                if (string.Equals((string)actor["name"], full, StringComparison.OrdinalIgnoreCase))
                    return JsonSerializer.Serialize(new { created = false, reason = "Already in the catalog.", actor });
            }

            string created = await _client.PostAsync("/api/v1/actors",
                new { firstName = firstName.Trim(), lastName = lastName?.Trim(), bio, link }, ct);
            return JsonSerializer.Serialize(new { created = true, actor = ActorSummary(created) });
        }

        [McpServerTool(Name = "update_actor", Destructive = true, Title = "Edit a person's details")]
        [Description("Change a person's name, bio or link. Only the fields you pass are changed, and a " +
                     "field cannot be cleared this way. The new text replaces the old, so to add to a bio " +
                     "read it first and pass the whole text. Returns the person as they are after the " +
                     "change. Needs an API key with the Editor role.")]
        public async Task<string> UpdateActor(
            [Description("The actor's id, from search_actors.")] long actorId,
            [Description("New first name.")] string firstName = null,
            [Description("New last name.")] string lastName = null,
            [Description("New bio.")] string bio = null,
            [Description("New link.")] string link = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName) &&
                string.IsNullOrWhiteSpace(bio) && string.IsNullOrWhiteSpace(link))
                throw new FvcException("Pass at least one of firstName, lastName, bio or link, not empty.");

            await _client.PutAsync($"/api/v1/actors/{actorId}", new { firstName, lastName, bio, link }, ct);
            return JsonSerializer.Serialize(ActorSummary(await _client.GetAsync($"/api/v1/actors/{actorId}", ct)));
        }

        [McpServerTool(Name = "delete_actor", Destructive = true, Title = "Delete a person")]
        [Description("Delete a person from the catalog. This cannot be undone: they leave the cast of " +
                     "every video, their learned faces are deleted, and faces found as them become " +
                     "unnamed people again. Their videos stay. Pass their name as well as the id: the " +
                     "delete is refused when the two do not match, so a mistyped id deletes no one. " +
                     "Before calling this, tell the user how many videos they are in (search_videos " +
                     "with actors) and get their go-ahead. To take someone out of one video, use " +
                     "remove_actor_from_video instead. Needs an API key with the Editor role.")]
        public async Task<string> DeleteActor(
            [Description("The actor's id, from search_actors.")] long actorId,
            [Description("The person's full name as search_actors reports it, to confirm the id.")] string name,
            CancellationToken ct = default)
        {
            var actor = ActorSummary(await _client.GetAsync($"/api/v1/actors/{actorId}", ct));
            string actual = (string)actor["name"];
            if (!string.Equals(actual, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new FvcException($"Actor {actorId} is '{actual}', not '{name}', so no one was deleted.");

            long videos = 0;
            string inVideos = await _client.GetAsync("/api/v1/videos" + FvcClient.Query(("actors", actorId), ("limit", 1)), ct);
            try
            {
                using var doc = JsonDocument.Parse(inVideos);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("totalCount", out var tc))
                    tc.TryGetInt64(out videos);
            }
            catch (JsonException)
            {
            }

            await _client.DeleteAsync($"/api/v1/actors/{actorId}", ct);
            return JsonSerializer.Serialize(new { deleted = actual, removedFromVideos = videos });
        }

        [McpServerTool(Name = "set_video_path", Destructive = true, Title = "Point a video at its file")]
        [Description("Change where a catalog entry looks for its video file, for a file that was moved or " +
                     "renamed outside Fast Video Cataloger. Only the catalog changes: no file is moved, " +
                     "and the path is not checked, so give the full path as the server's computer sees " +
                     "it. Everything else about the video is kept. Returns the video after the change. " +
                     "Needs an API key with the Editor role.")]
        public async Task<string> SetVideoPath(
            [Description("The video's catalog id.")] long videoId,
            [Description("The file's full path, e.g. 'D:\\Footage\\2024\\interview.mp4'.")] string path,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new FvcException("Provide the file's full path.");

            await _client.GetAsync($"/api/v1/videos/{videoId}", ct);
            await _client.PutAsync($"/api/v1/videos/{videoId}/path", new { newPath = path.Trim() }, ct);
            return await _client.GetAsync($"/api/v1/videos/{videoId}", ct);
        }

        /// <summary>
        /// A keyword by name, from the video keywords and then the scene ones (both lists are one
        /// table, so the id is the same). An exact match wins over one that differs in case only.
        /// One name, no wildcards: a delete or rename must never reach a keyword nobody named.
        /// </summary>
        private async Task<(long id, string name)> FindKeywordAsync(string keyword, CancellationToken ct)
        {
            string want = keyword?.Trim();
            if (string.IsNullOrEmpty(want))
                throw new FvcException("Name the keyword.");
            if (want.Contains(','))
                throw new FvcException("Name one keyword at a time, exactly as list_keywords reports it.");

            var all = new List<(long id, string name)>();
            all.AddRange(await ReadNamedAsync("/api/v1/tags/video", "tagID", ct));
            all.AddRange(await ReadNamedAsync("/api/v1/tags/thumbnail", "tagID", ct));
            foreach (var tag in all)
                if (tag.name == want)
                    return tag;
            foreach (var tag in all)
                if (string.Equals(tag.name, want, StringComparison.OrdinalIgnoreCase))
                    return tag;
            throw new FvcException($"'{want}' is not a keyword in this catalog. Check list_keywords for the exact name.");
        }

        private async Task<(long videos, long scenes)> KeywordUseAsync(long id, CancellationToken ct)
        {
            long.TryParse((await _client.GetAsync($"/api/v1/tags/{id}/usage", ct)).Trim(), out long videos);
            long.TryParse((await _client.GetAsync($"/api/v1/tags/thumbnail/{id}/usage", ct)).Trim(), out long scenes);
            return (videos, scenes);
        }

        /// <summary>The id and name of each row of a list the server returns.</summary>
        private async Task<List<(long id, string name)>> ReadNamedAsync(string path, string idField, CancellationToken ct)
        {
            string json = await _client.GetAsync(path, ct);
            var rows = new List<(long, string)>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                    root = data;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in root.EnumerateArray())
                    {
                        if (element.TryGetProperty(idField, out var idProp) && idProp.TryGetInt64(out long id) &&
                            element.TryGetProperty("name", out var nameProp))
                            rows.Add((id, nameProp.GetString() ?? ""));
                    }
                }
            }
            catch (JsonException)
            {
                throw new FvcException("Could not read the catalog's answer, so nothing was changed.");
            }
            return rows;
        }

        static string FullName(string first, string last) => ((first ?? "").Trim() + " " + (last ?? "").Trim()).Trim();

        // An actor without the portrait bytes, which would fill the answer with base64.
        static Dictionary<string, object> ActorSummary(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ActorSummary(doc.RootElement);
        }

        static Dictionary<string, object> ActorSummary(JsonElement actor)
        {
            string Text(string field) => actor.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            long id = actor.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out long value) ? value : 0;
            return new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = FullName(Text("firstName"), Text("lastName")),
                ["firstName"] = Text("firstName"),
                ["lastName"] = Text("lastName"),
                ["bio"] = Text("bio"),
                ["link"] = Text("link"),
            };
        }

        static List<Dictionary<string, object>> ActorSummaries(string json)
        {
            var list = new List<Dictionary<string, object>>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                root = data;
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var actor in root.EnumerateArray())
                    list.Add(ActorSummary(actor));
            return list;
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
