using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Services.Mcp;

/// <summary>
/// The module's API explorer: serves a hand-written OpenAPI document describing the module's
/// HTTP endpoints and a Scalar reference page with a document dropdown listing the module's own
/// spec plus the host proxy's spec (fetched server-side when the proxy explorer is enabled).
/// Also implements <see cref="IApiExplorerDocumentsProvider"/> so the proxy's explorer lists
/// this module's document in its own dropdown.
/// </summary>
internal sealed class McpApiExplorer : IApiExplorerDocumentsProvider
{
    private readonly McpServerHost _host;
    private readonly HostInfo _hostInfo;

    // Short-lived client used only to fetch the proxy's OpenAPI document for the dropdown.
    private static readonly HttpClient _specClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public McpApiExplorer(McpServerHost host, HostInfo hostInfo)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _hostInfo = hostInfo ?? throw new ArgumentNullException(nameof(hostInfo));
    }

    /// <summary>Base URL of the running module server, e.g. <c>http://localhost:8388</c>.</summary>
    private string? BaseUrl =>
        _host.IsRunning && _host.EndpointUrl.EndsWith(McpServerHost.McpPath, StringComparison.Ordinal)
            ? _host.EndpointUrl[..^McpServerHost.McpPath.Length]
            : null;

    public IReadOnlyList<ExplorerDocument> GetExplorerDocuments()
    {
        string? baseUrl = BaseUrl;
        if (baseUrl is null)
            return [];

        return [new ExplorerDocument("Kaeo LLM Proxy MCP", $"{baseUrl}{McpServerHost.SpecPath}")];
    }

    public string BuildSpecJson()
    {
        string baseUrl = BaseUrl ?? "http://localhost:8388";
        return OpenApiSpecTemplate.Replace("{{SERVER_URL}}", baseUrl);
    }

    /// <summary>
    /// Builds the Scalar page at render time. The module's own spec is embedded inline; the
    /// proxy's spec is fetched server-side (only when the proxy explorer is enabled) and
    /// embedded too, so the browser never needs cross-origin access. Unreachable documents are
    /// omitted gracefully.
    /// </summary>
    public async Task<string> BuildScalarHtmlAsync(CancellationToken cancellationToken)
    {
        var documents = new List<(string Label, string SpecJson)>
        {
            ("Kaeo LLM Proxy MCP", BuildSpecJson()),
        };

        if (_hostInfo.ApiExplorerEnabled && !string.IsNullOrWhiteSpace(_hostInfo.OpenApiSpecUrl))
        {
            string? proxySpec = await TryFetchJsonAsync(_hostInfo.OpenApiSpecUrl, cancellationToken);
            if (proxySpec is not null)
                documents.Add(("Kaeo LLM Proxy", proxySpec));
        }

        var documentsJson = new StringBuilder("[");
        for (int i = 0; i < documents.Count; i++)
        {
            if (i > 0)
                documentsJson.Append(',');

            // Spec content is validated raw JSON; "</" is escaped so an embedded string value
            // can never terminate the enclosing <script> block early.
            documentsJson
                .Append("{\"label\":")
                .Append(JsonSerializer.Serialize(documents[i].Label))
                .Append(",\"spec\":")
                .Append(documents[i].SpecJson.Replace("</", "<\\/"))
                .Append('}');
        }
        documentsJson.Append(']');

        return ScalarHtmlTemplate.Replace("/*DOCUMENTS*/[]", documentsJson.ToString());
    }

    private static async Task<string?> TryFetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _specClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            // Validate and normalize to compact JSON before embedding into the page.
            using JsonDocument parsed = JsonDocument.Parse(content);
            return parsed.RootElement.GetRawText();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    // ── Templates ───────────────────────────────────────────────────────────

    private const string OpenApiSpecTemplate = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Kaeo LLM Proxy MCP",
            "version": "1.0.0",
            "description": "MCP (Model Context Protocol) server module for Kaeo LLM Proxy. Exposes the tools contributed by the loaded modules - for example web_search/web_fetch (Web Search), ssh_* (SSH), and code_* (Code Vector Store) - over the MCP Streamable HTTP transport at /mcp (also served at the server root for clients that only accept a base URL). Clients speak JSON-RPC 2.0: POST an `initialize` request to open a session (the response carries the `Mcp-Session-Id` header), then send `tools/list`, `tools/call`, and notifications on the same session."
          },
          "servers": [
            { "url": "{{SERVER_URL}}" }
          ],
          "paths": {
            "/mcp": {
              "post": {
                "summary": "Send a JSON-RPC message (MCP Streamable HTTP)",
                "description": "Accepts a single JSON-RPC 2.0 message. Requests expecting a response return an SSE stream (text/event-stream) carrying the response and any interim notifications; notifications return 202 Accepted with an empty body. The first `initialize` request creates a session; include the returned Mcp-Session-Id header on all subsequent messages.",
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": { "type": "object" },
                      "examples": {
                        "initialize": {
                          "summary": "Open a session",
                          "value": {
                            "jsonrpc": "2.0",
                            "id": 1,
                            "method": "initialize",
                            "params": {
                              "protocolVersion": "2025-06-18",
                              "capabilities": {},
                              "clientInfo": { "name": "example-client", "version": "1.0" }
                            }
                          }
                        },
                        "toolsList": {
                          "summary": "List tools",
                          "value": { "jsonrpc": "2.0", "id": 2, "method": "tools/list" }
                        },
                        "toolsCall": {
                          "summary": "Call the web_search tool",
                          "value": {
                            "jsonrpc": "2.0",
                            "id": 3,
                            "method": "tools/call",
                            "params": { "name": "web_search", "arguments": { "query": "llama.cpp releases", "maxResults": 5 } }
                          }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "SSE stream carrying the JSON-RPC response(s) for the posted message.",
                    "content": { "text/event-stream": {} }
                  },
                  "202": { "description": "Accepted — the message was a notification; no response body." },
                  "400": { "description": "Malformed JSON-RPC message or missing session header." },
                  "401": { "description": "Bearer token missing or invalid (when authentication is configured)." },
                  "404": { "description": "Unknown session." }
                }
              },
              "get": {
                "summary": "Open the server-to-client SSE stream",
                "description": "Opens a long-lived Server-Sent Events stream for unsolicited server-to-client messages on an existing session. Requires the Mcp-Session-Id header and an Accept header including text/event-stream.",
                "responses": {
                  "200": { "description": "SSE stream kept open until the session ends.", "content": { "text/event-stream": {} } },
                  "404": { "description": "Unknown session." },
                  "406": { "description": "Accept header does not include text/event-stream." },
                  "409": { "description": "A GET stream is already open for this session." }
                }
              },
              "delete": {
                "summary": "End an MCP session",
                "responses": {
                  "200": { "description": "Session closed." },
                  "404": { "description": "Unknown session." }
                }
              }
            },
            "/health": {
              "get": {
                "summary": "Module server health",
                "responses": {
                  "200": {
                    "description": "The module server is running.",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "status": { "type": "string", "example": "ok" },
                            "server": { "type": "string", "example": "Kaeo LLM Proxy MCP" },
                            "uptimeSeconds": { "type": "integer" },
                            "activeSessions": { "type": "integer" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/openapi/v1/openapi.json": {
              "get": {
                "summary": "This OpenAPI document",
                "responses": { "200": { "description": "OpenAPI 3.0 JSON.", "content": { "application/json": {} } } }
              }
            },
            "/scalar": {
              "get": {
                "summary": "Scalar API explorer for this module",
                "responses": { "200": { "description": "HTML page.", "content": { "text/html": {} } } }
              }
            }
          }
        }
        """;

    private const string ScalarHtmlTemplate = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Kaeo LLM Proxy MCP — API Explorer</title>
            <style>
                body { margin: 0; padding: 0; }
                #kaeo-doc-selector {
                    position: fixed;
                    top: 10px;
                    right: 14px;
                    z-index: 10000;
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                }
                #kaeo-doc-select { padding: 4px 8px; }
            </style>
        </head>
        <body>
            <div id="kaeo-doc-selector" hidden>
                <select id="kaeo-doc-select" aria-label="API document"></select>
            </div>
            <div id="kaeo-api-reference"></div>
            <script>
                var kaeoDocuments = /*DOCUMENTS*/[];
            </script>
            <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference@1"></script>
            <script>
                (function () {
                    var mount = document.getElementById('kaeo-api-reference');
                    var select = document.getElementById('kaeo-doc-select');
                    var selector = document.getElementById('kaeo-doc-selector');
                    var instance = null;

                    function configurationFor(doc) {
                        return { spec: { content: doc.spec } };
                    }

                    function loadDocument(index) {
                        var config = configurationFor(kaeoDocuments[index]);
                        if (instance && typeof instance.updateConfig === 'function') {
                            instance.updateConfig(config);
                            return;
                        }
                        mount.innerHTML = '';
                        instance = Scalar.createApiReference(mount, config);
                    }

                    kaeoDocuments.forEach(function (doc, i) {
                        var option = document.createElement('option');
                        option.value = String(i);
                        option.textContent = doc.label;
                        select.appendChild(option);
                    });

                    if (kaeoDocuments.length > 1) {
                        selector.hidden = false;
                        select.addEventListener('change', function () {
                            loadDocument(Number(select.value));
                        });
                    }

                    if (kaeoDocuments.length > 0) loadDocument(0);
                })();
            </script>
        </body>
        </html>
        """;
}
