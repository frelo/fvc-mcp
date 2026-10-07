using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FvcMcpServer
{
    /// <summary>
    /// An MCP server that gives an AI assistant access to a Fast Video Cataloger catalog
    /// over the server's REST API.
    ///
    /// It speaks JSON-RPC over stdin/stdout, so NOTHING may be written to stdout except the
    /// protocol itself - all logging goes to stderr.
    ///
    /// Configuration comes from the environment, which is how MCP hosts pass settings:
    ///   FVC_SERVER_URL  address of the catalog server (default http://localhost:8754)
    ///   FVC_API_KEY     API key created with POST /api/v1/apikeys; optional for a
    ///                   catalog that has no user accounts configured
    /// </summary>
    internal class Program
    {
        private const string DefaultServerUrl = "http://localhost:8754";

        static async Task<int> Main(string[] args)
        {
            string serverUrl = Environment.GetEnvironmentVariable("FVC_SERVER_URL");
            string apiKey = Environment.GetEnvironmentVariable("FVC_API_KEY");

            // Command line wins over the environment, so the server can be tried out by hand.
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--url", StringComparison.OrdinalIgnoreCase))
                    serverUrl = args[i + 1];
                else if (string.Equals(args[i], "--key", StringComparison.OrdinalIgnoreCase))
                    apiKey = args[i + 1];
            }

            if (string.IsNullOrWhiteSpace(serverUrl))
                serverUrl = DefaultServerUrl;

            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
            {
                Console.Error.WriteLine($"'{serverUrl}' is not a valid server address. " +
                                        $"Set FVC_SERVER_URL to something like {DefaultServerUrl}.");
                return 1;
            }

            var builder = Host.CreateApplicationBuilder(args);

            // stdout belongs to the protocol, so every log line goes to stderr instead.
            // Warning by default keeps the host's log readable; set FVC_MCP_LOG_LEVEL
            // (Trace, Debug, Information, ...) when diagnosing a connection problem.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            if (!Enum.TryParse(Environment.GetEnvironmentVariable("FVC_MCP_LOG_LEVEL"), true, out LogLevel logLevel))
                logLevel = LogLevel.Warning;

            builder.Logging.SetMinimumLevel(logLevel);

            builder.Services.AddSingleton(_ => new FvcClient(serverUrl, apiKey));

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "fast-video-cataloger",
                        Version = "1.0.0"
                    };

                    // Sent with the handshake. Without it an assistant has only the individual
                    // tool descriptions to go on, which say nothing about which tool to reach
                    // for first or why a search came back empty.
                    options.ServerInstructions = CatalogTools.ServerInstructions;
                })
                .WithStdioServerTransport()
                .WithTools<CatalogTools>();

            await builder.Build().RunAsync();
            return 0;
        }
    }
}
