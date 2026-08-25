// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AutoGAD
{
    /// <summary>Minimal raw-HTTP client for POST /v1/messages (no NuGet deps — robust inside AutoCAD).</summary>
    public class ClaudeClient : ILlmClient
    {
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";
        private const string FallbackBeta = "server-side-fallback-2026-07-01";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private readonly Config _cfg;

        public ClaudeClient(Config cfg) { _cfg = cfg; }

        /// <summary>
        /// One Messages API turn. <paramref name="system"/> is a JSON array of text blocks
        /// (the drawing snapshot carries cache_control). Returns the parsed response object.
        /// </summary>
        public async Task<JsonObject> CreateMessageAsync(JsonArray system, JsonArray messages, JsonArray tools)
        {
            // Clone the caller's arrays: a JsonNode can only have one parent, and these
            // arrays are reused across turns, so assigning them into a fresh body each call
            // would throw "node already has a parent" on the second request.
            var msgs = messages.DeepClone().AsArray();
            AddCacheBreakpoint(msgs);

            var body = new JsonObject
            {
                ["model"] = _cfg.Model,
                ["max_tokens"] = _cfg.MaxTokens,
                ["system"] = system.DeepClone(),
                ["messages"] = msgs,
                ["tools"] = tools.DeepClone(),
                ["thinking"] = new JsonObject { ["type"] = "adaptive" },
                ["output_config"] = new JsonObject { ["effort"] = _cfg.Effort },
            };
            if (_cfg.Fallbacks)
            {
                // Server-side refusal fallback: if a safety classifier declines the request,
                // Anthropic re-runs it on the recommended substitute model instead of refusing.
                body["fallbacks"] = "default";
            }

            using (var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl))
            {
                req.Headers.Add("x-api-key", _cfg.ApiKey);
                req.Headers.Add("anthropic-version", ApiVersion);
                if (_cfg.Fallbacks) req.Headers.Add("anthropic-beta", FallbackBeta);
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                {
                    string txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception($"Claude API {(int)resp.StatusCode}: {ErrorMessage(txt)}");
                    return JsonNode.Parse(txt).AsObject();
                }
            }
        }

        /// <summary>
        /// Put cache_control on the last block of the last message. The system prompt carries one
        /// breakpoint (drawing snapshot); this adds a second on the conversation tail, so each
        /// tool-use iteration re-reads the growing history from cache (10% of the input price).
        /// </summary>
        private static void AddCacheBreakpoint(JsonArray messages)
        {
            if (messages.Count == 0) return;
            var last = messages[messages.Count - 1] as JsonObject;
            if (last == null) return;

            var content = last["content"];
            if (content is JsonValue v)
            {
                string text = v.GetValue<string>();
                last["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text", ["text"] = text,
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                    }
                };
            }
            else if (content is JsonArray arr && arr.Count > 0 && arr[arr.Count - 1] is JsonObject block)
            {
                block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }
        }

        private static string ErrorMessage(string body)
        {
            try
            {
                var j = JsonNode.Parse(body) as JsonObject;
                string m = j?["error"]?["message"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(m)) return m;
            }
            catch { }
            return body;
        }
    }
}
