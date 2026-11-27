using System.Reflection;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// The OpenAPI document for the model registration API — the one part of the dashboard a machine
/// is meant to call. It lives on the dashboard port rather than in the proxy's own explorer
/// (<c>/openapi/v1/openapi.json</c>) because it describes dashboard routes, is authenticated by
/// dashboard roles or a service account's API key, and is only readable by callers holding
/// <c>ViewRegistration</c>. Hand-written, like the proxy and MCP documents, so it stays an exact
/// description of what the endpoints actually accept.
/// </summary>
internal static class RegistrationApiSpec
{
    /// <summary>Where the document is served. Also the path the auth gate classifies.</summary>
    public const string Path = "/api/mappings/openapi.json";

    /// <summary>The document with the running build's version stamped in.</summary>
    public static string Build()
    {
        string version = typeof(RegistrationApiSpec).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(RegistrationApiSpec).Assembly.GetName().Version?.ToString()
            ?? "1.0.0";

        // The informational version carries the source-revision suffix after '+'.
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
            version = version[..plus];

        return Document.Replace("{{VERSION}}", version, StringComparison.Ordinal);
    }

    private const string Document = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Kaeo LLM Proxy - Model Registration API",
            "description": "The machine-facing part of the dashboard. A service account's API key (or a dashboard session) registers, updates, and unregisters the model mappings clients see, and inspects or triggers model auto-discovery. Reading needs the Registration Read Only capability; anything that changes state needs Registration Read-Write. A proxy API key reaches these routes and nothing else on the dashboard, even when the key belongs to an Admin user.",
            "version": "{{VERSION}}"
          },
          "servers": [
            { "url": "/", "description": "This web dashboard" }
          ],
          "security": [
            { "bearerAuth": [] },
            { "apiKeyAuth": [] },
            { "dashboardSession": [] }
          ],
          "tags": [
            { "name": "Registration", "description": "Create, read, update, and delete model mappings." },
            { "name": "Discovery", "description": "Model auto-discovery: what the last scan did, and running one now." }
          ],
          "paths": {
            "/api/mappings": {
              "get": {
                "summary": "List model mappings",
                "description": "The mappings this caller may see. An Admin, a Model Admin, or a Models Read Only holder sees every mapping; a user whose only model grant is Own Models Read Only sees just the mappings granted to them. Null properties are omitted from the response.",
                "operationId": "listMappings",
                "tags": ["Registration"],
                "responses": {
                  "200": {
                    "description": "Every visible mapping.",
                    "content": {
                      "application/json": {
                        "schema": { "type": "array", "items": { "$ref": "#/components/schemas/ModelMapping" } }
                      }
                    }
                  },
                  "401": { "$ref": "#/components/responses/Unauthorized" },
                  "403": { "$ref": "#/components/responses/Forbidden" }
                }
              },
              "post": {
                "summary": "Register a model",
                "description": "Adds a mapping and returns it with the id the proxy assigned. ProxyName and ModelName are required; everything else falls back to the same defaults the Models tab uses. Discovery bookkeeping (AutoManaged, AutoMissingSinceUtc, AutoDisabledUtc, AutoDisabledReason) is always cleared on a create.",
                "operationId": "createMapping",
                "tags": ["Registration"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": { "schema": { "$ref": "#/components/schemas/ModelMapping" } }
                  }
                },
                "responses": {
                  "200": {
                    "description": "The mapping as stored, including its new id.",
                    "content": {
                      "application/json": { "schema": { "$ref": "#/components/schemas/ModelMapping" } }
                    }
                  },
                  "401": { "$ref": "#/components/responses/Unauthorized" },
                  "403": { "$ref": "#/components/responses/Forbidden" }
                }
              }
            },
            "/api/mappings/{id}": {
              "put": {
                "summary": "Update a model mapping",
                "description": "Replaces the mapping with the supplied body. The body is a full mapping, not a patch, so send the whole object you want to end up with; the id in the path wins over any id in the body. Turning AutoManaged off, or switching a discovery-disabled model back on by hand, clears the discovery bookkeeping so a later scan neither re-disables the model nor claims credit for disabling it.",
                "operationId": "updateMapping",
                "tags": ["Registration"],
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "description": "Mapping id.", "schema": { "type": "integer", "format": "int32" } }
                ],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": { "schema": { "$ref": "#/components/schemas/ModelMapping" } }
                  }
                },
                "responses": {
                  "200": {
                    "description": "The mapping as stored.",
                    "content": {
                      "application/json": { "schema": { "$ref": "#/components/schemas/ModelMapping" } }
                    }
                  },
                  "401": { "$ref": "#/components/responses/Unauthorized" },
                  "403": { "$ref": "#/components/responses/Forbidden" },
                  "404": { "$ref": "#/components/responses/NotFound" }
                }
              },
              "delete": {
                "summary": "Unregister a model",
                "description": "Removes the mapping. Clients requesting that proxy name get 404 from the proxy from then on.",
                "operationId": "deleteMapping",
                "tags": ["Registration"],
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "description": "Mapping id.", "schema": { "type": "integer", "format": "int32" } }
                ],
                "responses": {
                  "200": { "description": "Deleted. The body is empty." },
                  "401": { "$ref": "#/components/responses/Unauthorized" },
                  "403": { "$ref": "#/components/responses/Forbidden" },
                  "404": { "$ref": "#/components/responses/NotFound" }
                }
              }
            },
            "/api/mappings/discovery": {
              "get": {
                "summary": "Read the discovery state",
                "description": "The auto-discovery settings the running service is using, whether a scan is in flight, when the last one ran, and what it did.",
                "operationId": "getDiscoveryState",
                "tags": ["Discovery"],
                "responses": {
                  "200": {
                    "description": "Current discovery state.",
                    "content": {
                      "application/json": { "schema": { "$ref": "#/components/schemas/DiscoveryState" } }
                    }
                  },
                  "401": { "$ref": "#/components/responses/Unauthorized" },
                  "403": { "$ref": "#/components/responses/Forbidden" }
                }
              }
            },
            "/api/mappings/discover": {
              "post": {
                "summary": "Run a discovery scan now",
                "description": "Runs one scan synchronously so the caller sees the outcome immediately instead of waiting for the next interval. Only a successful model-list read can disable anything, so an upstream that is down leaves every mapping untouched.",
                "operationId": "runDiscovery",
                "tags": ["Discovery"],
                "responses": {
                  "200": {
                    "description": "What the scan did.",
                    "content": {
                      "application/json": { "schema": { "$ref": "#/components/schemas/DiscoveryResult" } }
                    }
                  },
                  "401": { "$ref": "#/components/responses/Unauthorized" },
                  "403": { "$ref": "#/components/responses/Forbidden" },
                  "503": {
                    "description": "The scan could not run. The message names the reason.",
                    "content": {
                      "application/json": { "schema": { "$ref": "#/components/schemas/Error" } }
                    }
                  }
                }
              }
            },
            "/api/mappings/openapi.json": {
              "get": {
                "summary": "This OpenAPI document",
                "description": "Requires the Registration Read Only capability, so a service account can fetch the contract it is expected to follow.",
                "operationId": "getRegistrationSpec",
                "tags": ["Registration"],
                "responses": {
                  "200": {
                    "description": "OpenAPI 3.0 JSON.",
                    "content": { "application/json": {} }
                  },
                  "401": { "$ref": "#/components/responses/Unauthorized" },
                  "403": { "$ref": "#/components/responses/Forbidden" }
                }
              }
            }
          },
          "components": {
            "securitySchemes": {
              "bearerAuth": {
                "type": "http",
                "scheme": "bearer",
                "description": "A proxy API key from the Users tab, sent as an Authorization: Bearer header. This is the normal credential for a service account."
              },
              "apiKeyAuth": {
                "type": "apiKey",
                "in": "header",
                "name": "x-api-key",
                "description": "The same proxy API key, for clients that already send x-api-key to the proxy."
              },
              "dashboardSession": {
                "type": "apiKey",
                "in": "cookie",
                "name": ".KaeoDashboard.Session",
                "description": "A signed-in dashboard session. Works for a person at the dashboard; a service account uses an API key instead, because a key needs no password and can be revoked on its own."
              }
            },
            "responses": {
              "Unauthorized": {
                "description": "No usable dashboard session and no valid API key.",
                "content": {
                  "application/json": { "schema": { "$ref": "#/components/schemas/Error" } }
                }
              },
              "Forbidden": {
                "description": "Authenticated, but the caller's roles do not carry the capability this call needs. The message names the missing role.",
                "content": {
                  "application/json": { "schema": { "$ref": "#/components/schemas/Error" } }
                }
              },
              "NotFound": {
                "description": "No mapping with that id.",
                "content": {
                  "application/json": { "schema": { "$ref": "#/components/schemas/Error" } }
                }
              }
            },
            "schemas": {
              "Error": {
                "type": "object",
                "properties": {
                  "error": { "type": "string", "description": "Human-readable reason." }
                }
              },
              "ModelMapping": {
                "type": "object",
                "additionalProperties": true,
                "description": "One model mapping. The wire names follow the dashboard's own JSON: mostly camelCase, except the two legacy names OllamaName (the client-facing proxy name) and LlamaCppName (the upstream model name). Properties not listed here are accepted and preserved, which is how a client can round-trip a mapping it read from GET /api/mappings.",
                "required": ["OllamaName", "LlamaCppName"],
                "properties": {
                  "id": { "type": "integer", "format": "int32", "description": "Assigned by the proxy. Ignored on create; the path value wins on update." },
                  "OllamaName": { "type": "string", "description": "Proxy name: what clients ask for, and what /api/tags and /v1/models report." },
                  "LlamaCppName": { "type": "string", "description": "Upstream model name, e.g. llama-3-8b." },
                  "isEnabled": { "type": "boolean", "default": true, "description": "When false the mapping is ignored for routing and does not appear on the discovery endpoints." },
                  "hidden": { "type": "boolean", "default": false, "description": "Keeps the mapping out of /api/tags and /v1/models while leaving it reachable by its exact name." },
                  "upstreamUrl": { "type": "string", "description": "Base URL of the upstream server, e.g. http://127.0.0.1:11434." },
                  "upstreamType": {
                    "type": "integer",
                    "format": "int32",
                    "enum": [0, 1, 2, 3],
                    "default": 1,
                    "description": "0 = LlamaCpp (legacy, treated as OpenAI), 1 = OpenAI-compatible, 2 = Ollama-native, 3 = Anthropic Messages."
                  },
                  "credentialName": { "type": "string", "nullable": true, "description": "Name of a credential in the credential store, used for the upstream Authorization header." },
                  "instructionSetName": { "type": "string", "nullable": true, "description": "Name of the instruction set injected into requests for this model." },
                  "capabilities": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Capability tokens advertised for this model, e.g. text, chat, vision, function_calling."
                  },
                  "temperature": { "type": "number", "format": "double", "default": 0.7 },
                  "contextWindowTokens": { "type": "integer", "format": "int32", "description": "Context window used for compaction decisions. 0 means unknown." },
                  "autoManaged": {
                    "type": "boolean",
                    "default": false,
                    "description": "Marks this mapping as a discovery source and template: every model the upstream lists is registered automatically, and one that stays missing past the offline grace period is disabled."
                  },
                  "autoMissingSinceUtc": { "type": "string", "format": "date-time", "nullable": true, "description": "Read-only bookkeeping: when discovery first failed to find this model." },
                  "autoDisabledUtc": { "type": "string", "format": "date-time", "nullable": true, "description": "Read-only bookkeeping: when discovery disabled this model. Cleared by any manual edit." },
                  "autoDisabledReason": { "type": "string", "nullable": true, "description": "Read-only bookkeeping: why discovery disabled this model." }
                }
              },
              "DiscoveryState": {
                "type": "object",
                "properties": {
                  "enabled": { "type": "boolean", "description": "The auto-discovery master switch." },
                  "intervalSeconds": { "type": "integer", "format": "int32", "description": "How often the scan loop runs." },
                  "offlineMinutes": { "type": "integer", "format": "int32", "description": "How long a model must stay missing before discovery disables it." },
                  "isRunning": { "type": "boolean", "description": "True while a scan is in flight." },
                  "lastScanUtc": { "type": "string", "format": "date-time", "nullable": true, "description": "When the last scan finished, or null before the first one." },
                  "lastResult": {
                    "nullable": true,
                    "allOf": [{ "$ref": "#/components/schemas/DiscoveryResult" }],
                    "description": "What the last scan did, or null before the first one."
                  }
                }
              },
              "DiscoveryResult": {
                "type": "object",
                "properties": {
                  "sources": { "type": "integer", "format": "int32", "description": "Upstream endpoints polled." },
                  "failedSources": { "type": "integer", "format": "int32", "description": "Endpoints whose model list could not be read. A failed read never disables anything." },
                  "registered": { "type": "integer", "format": "int32", "description": "Models newly registered by this scan." },
                  "reactivated": { "type": "integer", "format": "int32", "description": "Models re-enabled because they came back." },
                  "disabled": { "type": "integer", "format": "int32", "description": "Models disabled because they stayed missing past the grace period." },
                  "changed": { "type": "boolean", "description": "True when registered, reactivated, or disabled is non-zero." },
                  "messages": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Per-source notes, including the errors behind failed sources."
                  }
                }
              }
            }
          }
        }
        """;
}