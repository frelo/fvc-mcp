using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FvcMcpServer
{
    /// <summary>
    /// Thrown when the catalog server answers with an error. The message is written for
    /// an assistant to read and act on, not for a log file.
    ///
    /// It derives from McpException because that is the exception type whose message the
    /// SDK propagates to the caller - anything else is reported as a bare
    /// "An error occurred invoking ...", which tells the model nothing it can act on.
    /// The messages here name the server and the setting to fix, and contain nothing secret.
    /// </summary>
    public class FvcException : ModelContextProtocol.McpException
    {
        public FvcException(string message) : base(message) { }
    }

    /// <summary>
    /// Thin client over the Fast Video Cataloger REST API.
    /// Every response is wrapped in {success, data, error, totalCount}; this unwraps it
    /// so the tools only ever deal with the payload.
    /// </summary>
    public class FvcClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public FvcClient(string baseUrl, string apiKey)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');

            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(100);

            if (!string.IsNullOrEmpty(apiKey))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        public string BaseUrl => _baseUrl;

        /// <summary>
        /// Builds a query string from the parameters that were actually supplied.
        /// Null and empty values are dropped so the server applies its own defaults.
        /// </summary>
        public static string Query(params (string name, object value)[] parameters)
        {
            var parts = new List<string>();

            foreach (var (name, value) in parameters)
            {
                if (value == null)
                    continue;

                string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(text))
                    continue;

                parts.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(text));
            }

            return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, _baseUrl + path);

            if (body != null)
            {
                string json = JsonSerializer.Serialize(body);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FvcException($"The catalog server at {_baseUrl} did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                throw new FvcException(
                    $"Could not reach the catalog server at {_baseUrl} ({ex.Message}). " +
                    "This connector talks to the Fast Video Cataloger server, not to the Fast Video " +
                    "Cataloger application, so having the program open is not enough: a catalog has to " +
                    "be shared from the Server page on the Start screen: run Setup the first time, " +
                    "click Start Server after that. " +
                    "If it is already shared, check that FVC_SERVER_URL points at it.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new FvcException(
                    "The catalog server rejected the credentials. Set FVC_API_KEY to a valid API key. " +
                    "Create one in Fast Video Cataloger under Start > Server > Manage Users > Create key, " +
                    "or over the REST API with POST /api/v1/apikeys. A Viewer key is enough to read the catalog.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new FvcException(
                    "This API key does not have permission for that. Writing to the catalog needs a key " +
                    "with the Editor role; managing keys needs Admin.");
            }

            return response;
        }

        /// <summary>
        /// Calls the API and returns the "data" payload as JSON text, ready to hand back
        /// to the model. Errors reported inside the envelope are raised as exceptions.
        /// </summary>
        public async Task<string> CallAsync(HttpMethod method, string path, object body, CancellationToken ct)
        {
            using var response = await SendAsync(method, path, body, ct);
            string text = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new FvcException(ExtractError(text) ?? "Not found in this catalog.");

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new FvcException(ExtractError(text) ?? "The server has no catalog open.");

            if (!response.IsSuccessStatusCode)
                throw new FvcException(ExtractError(text) ?? $"The catalog server returned {(int)response.StatusCode}.");

            if (string.IsNullOrWhiteSpace(text))
                return "{}";

            // Unwrap the {success, data, ...} envelope when there is one.
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("success", out var success) &&
                        success.ValueKind == JsonValueKind.False)
                    {
                        throw new FvcException(ExtractError(text) ?? "The catalog server reported a failure.");
                    }

                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        string payload = data.GetRawText();

                        // Carry the total through - it tells the model whether results were truncated.
                        if (doc.RootElement.TryGetProperty("totalCount", out var total) &&
                            total.ValueKind == JsonValueKind.Number)
                        {
                            return $"{{\"totalCount\":{total.GetRawText()},\"data\":{payload}}}";
                        }

                        return payload;
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON - hand back whatever the server said.
            }

            return text;
        }

        /// <summary>Fetches raw bytes, for the endpoints that serve images.</summary>
        public async Task<(byte[] bytes, string mimeType)> GetBytesAsync(string path, CancellationToken ct)
        {
            using var response = await SendAsync(HttpMethod.Get, path, null, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new FvcException("Not found in this catalog.");

            if (!response.IsSuccessStatusCode)
                throw new FvcException($"The catalog server returned {(int)response.StatusCode}.");

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            string mimeType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            return (bytes, mimeType);
        }

        public Task<string> GetAsync(string path, CancellationToken ct) =>
            CallAsync(HttpMethod.Get, path, null, ct);

        public Task<string> PostAsync(string path, object body, CancellationToken ct) =>
            CallAsync(HttpMethod.Post, path, body, ct);

        public Task<string> PutAsync(string path, object body, CancellationToken ct) =>
            CallAsync(HttpMethod.Put, path, body, ct);

        public Task<string> DeleteAsync(string path, CancellationToken ct) =>
            CallAsync(HttpMethod.Delete, path, null, ct);

        private static string ExtractError(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
