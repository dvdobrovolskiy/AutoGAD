// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AutoGAD
{
    /// <summary>
    /// One LLM turn. The agent speaks the Anthropic Messages shape everywhere (system blocks,
    /// tool_use / tool_result blocks, usage.input_tokens...); a client either sends that as-is
    /// (<see cref="ClaudeClient"/>) or translates it (<see cref="OpenAiClient"/>).
    /// </summary>
    public interface ILlmClient
    {
        Task<JsonObject> CreateMessageAsync(JsonArray system, JsonArray messages, JsonArray tools);
    }

    public static class LlmClient
    {
        public static ILlmClient Create(Config cfg) =>
            cfg.IsOpenAi ? (ILlmClient)new OpenAiClient(cfg) : new ClaudeClient(cfg);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>Cheap key check (lists models, costs no tokens). Returns null when the key works, else a message.</summary>
        public static async Task<string> ValidateKeyAsync(string provider, string key, string baseUrl)
        {
            bool openai = Config.NormalizeProvider(provider) == Config.ProviderOpenAi;
            string url = openai ? (baseUrl ?? Config.DefaultOpenAiBaseUrl).Trim().TrimEnd('/') + "/models"
                                : "https://api.anthropic.com/v1/models?limit=1";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (openai) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                else
                {
                    req.Headers.Add("x-api-key", key);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                }
                using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode) return null;
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                        return "Rejected by the API (401): this key is not valid.";
                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        return "Rejected by the API (403): the key exists but lacks permission.";
                    return "Could not verify the key — the API replied " + (int)resp.StatusCode + " " + resp.ReasonPhrase + ".";
                }
            }
        }
    }

    /// <summary>
    /// OpenAI-compatible Chat Completions client (OpenAI, OpenRouter, any server exposing
    /// POST {baseUrl}/chat/completions). Translates the agent's Anthropic-shaped history and tools
    /// into the OpenAI shape and the response back, so the agent loop is provider-agnostic.
    /// </summary>
    public class OpenAiClient : ILlmClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private readonly Config _cfg;
        private readonly bool _openRouter;
        private bool _noReasoning;      // server rejected reasoning_effort → stop sending it
        private bool _useMaxTokens;     // "max_tokens" (compatible servers) vs "max_completion_tokens" (OpenAI)

        public OpenAiClient(Config cfg)
        {
            _cfg = cfg;
            string host = (cfg.OpenAiBaseUrl ?? "").ToLowerInvariant();
            _openRouter = host.Contains("openrouter.ai");
            _useMaxTokens = !host.Contains("api.openai.com");
        }

        public async Task<JsonObject> CreateMessageAsync(JsonArray system, JsonArray messages, JsonArray tools)
        {
            string url = _cfg.OpenAiBaseUrl.TrimEnd('/') + "/chat/completions";
            for (int attempt = 0; ; attempt++)
            {
                var body = BuildBody(system, messages, tools);
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey);
                    if (_openRouter) req.Headers.TryAddWithoutValidation("X-Title", "AutoGAD");
                    req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                    using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                    {
                        string txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                            return Translate(JsonNode.Parse(txt).AsObject());

                        string msg = ErrorMessage(txt);
                        if ((int)resp.StatusCode == 400 && attempt < 2)
                        {
                            // Compatible servers differ in which parameters they accept; adapt and retry once each.
                            if (!_noReasoning && msg.IndexOf("reasoning", StringComparison.OrdinalIgnoreCase) >= 0)
                            { _noReasoning = true; continue; }
                            if (msg.IndexOf("max_tokens", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("max_completion_tokens", StringComparison.OrdinalIgnoreCase) >= 0)
                            { _useMaxTokens = !_useMaxTokens; continue; }
                        }
                        throw new Exception($"{(_openRouter ? "OpenRouter" : "OpenAI-compatible")} API {(int)resp.StatusCode}: {msg}");
                    }
                }
            }
        }

        // ---------------------------------------------------------------- request

        private JsonObject BuildBody(JsonArray system, JsonArray messages, JsonArray tools)
        {
            var msgs = new JsonArray();

            var sys = new StringBuilder();
            foreach (var b in system)
            {
                string t = Str(b as JsonObject, "text");
                if (!string.IsNullOrEmpty(t)) { if (sys.Length > 0) sys.Append("\n\n"); sys.Append(t); }
            }
            if (sys.Length > 0) msgs.Add(new JsonObject { ["role"] = "system", ["content"] = sys.ToString() });

            foreach (var m in messages) TranslateMessage(m as JsonObject, msgs);

            var body = new JsonObject
            {
                ["model"] = _cfg.Model,
                ["messages"] = msgs,
                [_useMaxTokens ? "max_tokens" : "max_completion_tokens"] = _cfg.MaxTokens
            };
            if (tools != null && tools.Count > 0)
            {
                var fns = new JsonArray();
                foreach (var t in tools)
                {
                    var tool = t as JsonObject;
                    if (tool == null) continue;
                    fns.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = Str(tool, "name"),
                            ["description"] = Str(tool, "description") ?? "",
                            ["parameters"] = tool["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
                        }
                    });
                }
                body["tools"] = fns;
            }
            if (!_noReasoning) body["reasoning_effort"] = MapEffort(_cfg.Effort);
            if (_openRouter) body["usage"] = new JsonObject { ["include"] = true };   // returns usage.cost in USD
            return body;
        }

        /// <summary>Anthropic effort levels → OpenAI reasoning_effort (low / medium / high).</summary>
        private static string MapEffort(string effort)
        {
            switch (effort)
            {
                case "low": return "low";
                case "medium": return "medium";
                default: return "high";     // high, xhigh, max
            }
        }

        private static void TranslateMessage(JsonObject m, JsonArray outMsgs)
        {
            if (m == null) return;
            string role = Str(m, "role");
            var content = m["content"];

            if (role == "user")
            {
                if (content is JsonValue v)
                {
                    outMsgs.Add(new JsonObject { ["role"] = "user", ["content"] = v.GetValue<string>() });
                    return;
                }
                var parts = new JsonArray();      // text/image parts that follow the tool results
                foreach (var node in content as JsonArray ?? new JsonArray())
                {
                    var block = node as JsonObject;
                    switch (Str(block, "type"))
                    {
                        case "tool_result":
                            {
                                var text = new StringBuilder();
                                var images = new JsonArray();
                                var rc = block["content"];
                                if (rc is JsonValue rv) text.Append(rv.GetValue<string>());
                                else foreach (var rn in rc as JsonArray ?? new JsonArray())
                                    {
                                        var rb = rn as JsonObject;
                                        if (Str(rb, "type") == "text") text.Append(Str(rb, "text"));
                                        else if (Str(rb, "type") == "image") images.Add(ImagePart(rb));
                                    }
                                if (images.Count > 0) text.Append(" [image attached in the next message]");
                                outMsgs.Add(new JsonObject
                                {
                                    ["role"] = "tool",
                                    ["tool_call_id"] = Str(block, "tool_use_id"),
                                    ["content"] = text.ToString()
                                });
                                foreach (var img in images) parts.Add(img.DeepClone());
                                break;
                            }
                        case "text":
                            parts.Add(new JsonObject { ["type"] = "text", ["text"] = Str(block, "text") ?? "" });
                            break;
                        case "image":
                            parts.Add(ImagePart(block));
                            break;
                    }
                }
                if (parts.Count == 1 && Str(parts[0] as JsonObject, "type") == "text")
                    outMsgs.Add(new JsonObject { ["role"] = "user", ["content"] = Str(parts[0] as JsonObject, "text") });
                else if (parts.Count > 0)
                    outMsgs.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
            }
            else if (role == "assistant")
            {
                var text = new StringBuilder();
                var calls = new JsonArray();
                foreach (var node in content as JsonArray ?? new JsonArray())
                {
                    var block = node as JsonObject;
                    string type = Str(block, "type");
                    if (type == "text") text.Append(Str(block, "text"));
                    else if (type == "tool_use")
                        calls.Add(new JsonObject
                        {
                            ["id"] = Str(block, "id"),
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = Str(block, "name"),
                                ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString()
                            }
                        });
                    // thinking / fallback blocks have no OpenAI equivalent and are dropped
                }
                var msg = new JsonObject { ["role"] = "assistant" };
                if (text.Length > 0 || calls.Count == 0) msg["content"] = text.ToString();
                else msg["content"] = null;
                if (calls.Count > 0) msg["tool_calls"] = calls;
                outMsgs.Add(msg);
            }
        }

        private static JsonObject ImagePart(JsonObject block)
        {
            var src = block?["source"] as JsonObject;
            string url = Str(src, "type") == "url"
                ? Str(src, "url")
                : "data:" + (Str(src, "media_type") ?? "image/png") + ";base64," + Str(src, "data");
            return new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } };
        }

        // ---------------------------------------------------------------- response

        private static JsonObject Translate(JsonObject resp)
        {
            var choice = (resp["choices"] as JsonArray)?[0] as JsonObject;
            var msg = choice?["message"] as JsonObject;
            var content = new JsonArray();

            string text = null;
            if (msg?["content"] is JsonValue cv) text = cv.GetValue<string>();
            else if (msg?["content"] is JsonArray ca)
            {
                var sb = new StringBuilder();
                foreach (var p in ca) if (Str(p as JsonObject, "type") == "text") sb.Append(Str(p as JsonObject, "text"));
                text = sb.ToString();
            }
            string refusal = Str(msg, "refusal");
            if (!string.IsNullOrEmpty(refusal)) text = (text ?? "") + refusal;
            if (!string.IsNullOrEmpty(text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

            foreach (var node in msg?["tool_calls"] as JsonArray ?? new JsonArray())
            {
                var tc = node as JsonObject;
                var fn = tc?["function"] as JsonObject;
                string args = Str(fn, "arguments");
                JsonObject input;
                try { input = string.IsNullOrWhiteSpace(args) ? new JsonObject() : JsonNode.Parse(args) as JsonObject ?? new JsonObject(); }
                catch { input = new JsonObject { ["_unparsed_arguments"] = args }; }
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = Str(tc, "id") ?? "call_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    ["name"] = Str(fn, "name"),
                    ["input"] = input
                });
            }

            string finish = Str(choice, "finish_reason");
            string stop = finish == "tool_calls" ? "tool_use"
                        : finish == "length" ? "max_tokens"
                        : finish == "content_filter" ? "refusal"
                        : !string.IsNullOrEmpty(refusal) ? "refusal"
                        : "end_turn";

            var u = resp["usage"] as JsonObject;
            long prompt = Long(u, "prompt_tokens"), completion = Long(u, "completion_tokens");
            long cached = Long(u?["prompt_tokens_details"] as JsonObject, "cached_tokens");
            var usage = new JsonObject
            {
                ["input_tokens"] = Math.Max(0, prompt - cached),
                ["output_tokens"] = completion,
                ["cache_read_input_tokens"] = cached,
                ["cache_creation_input_tokens"] = 0
            };
            try { if (u?["cost"] != null) usage["cost_usd"] = u["cost"].GetValue<double>(); } catch { }   // OpenRouter

            var result = new JsonObject
            {
                ["content"] = content,
                ["stop_reason"] = stop,
                ["model"] = Str(resp, "model"),
                ["usage"] = usage
            };
            if (stop == "refusal")
                result["stop_details"] = new JsonObject { ["type"] = "refusal", ["explanation"] = refusal ?? "content filter" };
            return result;
        }

        // ---------------------------------------------------------------- helpers

        private static string ErrorMessage(string body)
        {
            try
            {
                var j = JsonNode.Parse(body) as JsonObject;
                var err = j?["error"];
                string m = err is JsonObject eo ? Str(eo, "message") : err?.ToString();
                if (!string.IsNullOrEmpty(m)) return m;
            }
            catch { }
            return body;
        }

        private static string Str(JsonObject o, string k)
        {
            try { return o?[k] == null ? null : o[k].GetValue<string>(); } catch { return o?[k]?.ToString(); }
        }

        private static long Long(JsonObject o, string k)
        {
            try { return o?[k] == null ? 0 : o[k].GetValue<long>(); } catch { return 0; }
        }
    }
}
