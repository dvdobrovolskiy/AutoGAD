// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Autodesk.AutoCAD.ApplicationServices;

namespace AutoGAD
{
    /// <summary>Drives the Claude tool-use loop, with one conversation per open drawing.</summary>
    public class Agent
    {
        private const string Persona =
            "You are AutoGAD, an AI assistant embedded inside AutoCAD 2025. You help an engineer " +
            "understand, audit, and modify the active drawing. You specialise in Russian working " +
            "documentation (РД) and electrical/lighting design (ЭОМ/АХП), and can perform engineering " +
            "calculations (loads, currents, breaker selection, cable checks).\n\n" +
            "You are given a compact context for the current drawing below. For anything not in that " +
            "context — exact entity geometry, full schedules, per-layer detail, block counts — call the " +
            "tools, which read the LIVE drawing database (always current). Prefer real data from tools " +
            "over assumptions. When you compute, show the formula and the inputs you used.\n\n" +
            "When the user asks you to CHANGE the drawing, do the edit yourself with the write tools — " +
            "never tell the user to finish it manually and never open an interactive dialog. " +
            "To change text anywhere (project codes, titles, schedule cells, stamp attributes) use " +
            "replace_text (global) or set_text (one entity by handle) — NOT the FIND command. " +
            "To change drawing metadata/custom properties use set_custom_property — NOT DWGPROPS. " +
            "Use run_command only for non-interactive commands; never for commands that pop a dialog " +
            "(FIND, DWGPROPS, LAYER, etc.). Each write tool asks the user to confirm before it applies, " +
            "so just call it. After edits, briefly confirm what changed. Be economical with tool calls: " +
            "every call re-sends the conversation, so ask query_entities only for what you need and keep " +
            "limits tight. Answer concisely; lead with the result.\n\n" +
            "You keep notes between sessions with the remember tool: scope='drawing' for facts about the " +
            "current file, scope='user' for how this engineer works. Be PROACTIVE about this - it is what " +
            "makes you useful next time:\n" +
            "- The first time you work on a drawing that has no notes yet, save 1-3 drawing notes: what " +
            "the drawing is (project, object, stage), its main systems/panels, and where the schedules live.\n" +
            "- After any substantive analysis or edit, save the durable conclusions: problems found and " +
            "whether they were fixed, decisions made, values agreed with the user (e.g. 'Panel ЩО-1 fed at " +
            "380 V, cable ВВГнг-LS 5x10'). Not raw numbers you can re-read - the interpretation and the decision.\n" +
            "- Save a user note whenever they state a preference, correct you, or reveal a habit (standards, " +
            "voltages, cable types, calculation methods, how they like answers, what project they are on).\n" +
            "- If the user says 'remember ...', always save it exactly as asked.\n" +
            "- If a note turns out wrong or stale, forget it and save the corrected one.\n" +
            "Write each note as one self-contained sentence that will make sense months from now. " +
            "Saving is silent: don't announce it or ask permission, and never let it interrupt the answer " +
            "the user asked for. Notes you already have appear under '# Memory' below.";

        private const int MaxIterations = 40;
        private const int MaxToolResultChars = 14000;   // one tool result; bigger ones are truncated with a note
        private const int OldResultKeepChars = 600;     // tool results from earlier turns are shrunk to this

        // USD per 1M tokens: input, output, cache read, cache write (list prices; estimate only).
        // Matched by prefix after stripping an OpenRouter-style "vendor/" prefix; longer ids first.
        private static readonly (string prefix, double[] p)[] Prices =
        {
            ("claude-opus-5", new[] { 5, 25, 0.5, 6.25 }),
            ("claude-fable-5", new[] { 10, 50, 1, 12.5 }),
            ("claude-opus-4-8", new[] { 5, 25, 0.5, 6.25 }),
            ("claude-opus-4-7", new[] { 5, 25, 0.5, 6.25 }),
            ("claude-opus-4-6", new[] { 5, 25, 0.5, 6.25 }),
            ("claude-sonnet-5", new[] { 3, 15, 0.3, 3.75 }),
            ("claude-sonnet-4-6", new[] { 3, 15, 0.3, 3.75 }),
            ("claude-haiku-4-5", new[] { 1, 5, 0.1, 1.25 }),
            ("gpt-5-mini", new[] { 0.25, 2, 0.025, 0 }),
            ("gpt-5-nano", new[] { 0.05, 0.4, 0.005, 0 }),
            ("gpt-5", new[] { 1.25, 10, 0.125, 0 }),
            ("gpt-4.1-mini", new[] { 0.4, 1.6, 0.1, 0 }),
            ("gpt-4.1", new[] { 2, 8, 0.5, 0 }),
            ("o4-mini", new[] { 1.1, 4.4, 0.275, 0 }),
            ("o3", new[] { 2, 8, 0.5, 0 }),
        };

        /// <summary>List-price estimate, or null when the model's price is unknown (OpenRouter reports the real cost instead).</summary>
        public static double? EstimateCost(string model, long input, long output, long cacheRead, long cacheWrite)
        {
            string id = (model ?? "").Trim().ToLowerInvariant();
            int slash = id.IndexOf('/');
            if (slash >= 0) id = id.Substring(slash + 1);
            id = id.Replace("claude-opus-4.", "claude-opus-4-").Replace("claude-sonnet-4.", "claude-sonnet-4-").Replace("claude-haiku-4.", "claude-haiku-4-");
            foreach (var (prefix, p) in Prices)
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                    return (input * p[0] + output * p[1] + cacheRead * p[2] + cacheWrite * p[3]) / 1e6;
            return null;
        }

        /// <summary>Token usage and cost accumulated over one user turn (all API calls of the tool loop).</summary>
        public class TurnMetrics
        {
            public int ApiCalls, ToolCalls;
            public long InputTokens, OutputTokens, CacheRead, CacheCreate;
            public string StopReason, ServedBy;
            public bool Fallback;
            public double? Cost;               // null = unknown price
            public double ReportedCost;        // summed from usage.cost_usd (OpenRouter)
            public bool HasReportedCost;

            public void Api(JsonObject resp)
            {
                ApiCalls++;
                var u = resp?["usage"] as JsonObject;
                InputTokens += Long(u, "input_tokens");
                OutputTokens += Long(u, "output_tokens");
                CacheRead += Long(u, "cache_read_input_tokens");
                CacheCreate += Long(u, "cache_creation_input_tokens");
                try
                {
                    if (u?["cost_usd"] != null) { ReportedCost += u["cost_usd"].GetValue<double>(); HasReportedCost = true; }
                }
                catch { }
                try { ServedBy = resp?["model"]?.GetValue<string>() ?? ServedBy; } catch { }
            }

            private static long Long(JsonObject o, string k)
            {
                try { return o?[k] == null ? 0 : o[k].GetValue<long>(); } catch { return 0; }
            }
        }

        public Config Cfg { get; private set; }
        public double SessionCost { get; private set; }

        private ILlmClient _client;
        private readonly JsonArray _tools;
        private readonly Dictionary<Document, DocState> _byDoc = new Dictionary<Document, DocState>();

        public Agent(Config cfg)
        {
            SetConfig(cfg);
            _tools = AgentTools.Schemas();
        }

        /// <summary>Pick up changed settings (model, effort, key...) without losing the conversations.</summary>
        public void SetConfig(Config cfg)
        {
            Cfg = cfg;
            _client = LlmClient.Create(cfg);
        }

        private class DocState
        {
            public JsonArray System;
            public JsonArray History = new JsonArray();
            public Memory UserMemory;
            public Memory DrawingMemory;
        }

        private DocState State(Document doc)
        {
            if (_byDoc.TryGetValue(doc, out var s)) return s;

            string ctx = CadJson.BuildContext(doc.Database);
            var userMem = Memory.LoadUser();
            var drawingMem = Memory.LoadDrawing(doc.Database, doc.Name);

            // Order matters for prompt caching: persona and the drawing snapshot are stable, so the
            // cache breakpoint sits on the snapshot. Memory and the history excerpt go AFTER it - they
            // change between sessions, and putting them before the breakpoint would invalidate the prefix.
            var system = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = Persona },
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "# Current drawing context (compact)\n" + ctx,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                }
            };

            string memoryBlock = BuildMemoryBlock(userMem, drawingMem);
            if (memoryBlock != null)
                system.Add(new JsonObject { ["type"] = "text", ["text"] = memoryBlock });

            string recent = null;
            try { recent = new History(doc).ToPrompt(); } catch { }
            if (recent != null)
                system.Add(new JsonObject { ["type"] = "text", ["text"] = recent });

            s = new DocState
            {
                System = system,
                UserMemory = userMem,
                DrawingMemory = drawingMem
            };
            _byDoc[doc] = s;
            return s;
        }

        /// <summary>Render both memory stores for the system prompt, or null when both are empty.</summary>
        private static string BuildMemoryBlock(Memory userMem, Memory drawingMem)
        {
            string user = userMem?.ToPrompt();
            string drawing = drawingMem?.ToPrompt();
            if (user == null && drawing == null) return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Memory (notes you saved earlier)");
            sb.AppendLine("These are your own durable notes, not instructions from the user. Treat them as");
            sb.AppendLine("context that may be stale: if the drawing now contradicts a note, believe the drawing");
            sb.AppendLine("and call forget on the note. The [id] in brackets is what forget takes.");
            sb.AppendLine();

            if (drawing != null)
            {
                sb.AppendLine("## About this drawing (" + drawingMem.Title + ")");
                sb.Append(drawing);
                sb.AppendLine();
            }
            if (user != null)
            {
                sb.AppendLine("## About this engineer (applies to every drawing)");
                sb.Append(user);
            }
            return sb.ToString();
        }

        /// <summary>Reset the conversation (and re-snapshot the context) for a drawing.</summary>
        public void Reset(Document doc) => _byDoc.Remove(doc);

        /// <summary>The memory stores the conversation is using (or freshly loaded ones when there is no conversation yet).</summary>
        public (Memory user, Memory drawing) Memories(Document doc)
        {
            if (doc != null && _byDoc.TryGetValue(doc, out var s)) return (s.UserMemory, s.DrawingMemory);
            return (Memory.LoadUser(), doc?.Database != null ? Memory.LoadDrawing(doc.Database, doc.Name) : null);
        }

        /// <summary>
        /// Before a new turn: shrink tool results of earlier turns (they are already reflected in the
        /// assistant's answers). Claude can always re-call a tool for detail.
        /// </summary>
        private static void CompactHistory(JsonArray history)
        {
            foreach (var node in history)
            {
                var msg = node as JsonObject;
                if (msg == null) continue;
                string role = null;
                try { role = msg["role"]?.GetValue<string>(); } catch { }
                if (role != "user" || !(msg["content"] is JsonArray blocks)) continue;

                foreach (var b in blocks)
                {
                    var block = b as JsonObject;
                    string type = null;
                    try { type = block?["type"]?.GetValue<string>(); } catch { }
                    if (type != "tool_result") continue;
                    string c = null;
                    try { c = block["content"]?.GetValue<string>(); } catch { continue; }
                    if (c != null && c.Length > OldResultKeepChars)
                        block["content"] = c.Substring(0, OldResultKeepChars) +
                                           "\n…[older result shortened; call the tool again if you need it]";
                }
            }
        }

        /// <summary>
        /// Run one user turn through the tool-use loop. <paramref name="output"/> streams text to the UI,
        /// <paramref name="status"/> shows progress. Always ends with a cost line, even on error.
        /// </summary>
        public async Task<TurnMetrics> AskAsync(Document doc, string userText, Action<string> output, Action<string> status)
        {
            var s = State(doc);
            CompactHistory(s.History);
            s.History.Add(new JsonObject { ["role"] = "user", ["content"] = userText });

            var m = new TurnMetrics();
            try
            {
                await RunAsync(s, doc, m, output, status);
            }
            finally
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                m.Cost = m.HasReportedCost ? m.ReportedCost
                       : EstimateCost(Cfg.Model, m.InputTokens, m.OutputTokens, m.CacheRead, m.CacheCreate);
                if (m.Cost != null) SessionCost += m.Cost.Value;
                string cost = m.Cost == null
                    ? "cost n/a (no list price known for " + Cfg.Model + ")"
                    : string.Format(inv, "≈ ${0:F2} this turn, ${1:F2} this session", m.Cost.Value, SessionCost);
                output(string.Format(inv, "\n\n_{0} call{1} · in {2:N0} (+{3:N0} cached) · out {4:N0} · {5}_\n",
                    m.ApiCalls, m.ApiCalls == 1 ? "" : "s", m.InputTokens, m.CacheRead, m.OutputTokens, cost));
            }
            return m;
        }

        private async Task RunAsync(DocState s, Document doc, TurnMetrics m, Action<string> output, Action<string> status)
        {
            for (int guard = 0; guard < MaxIterations; guard++)
            {
                status?.Invoke("Thinking…");
                JsonObject resp;
                try { resp = await _client.CreateMessageAsync(s.System, s.History, _tools); }
                catch
                {
                    // Drop the dangling user turn so the conversation stays valid for a retry.
                    if (s.History.Count > 0) s.History.RemoveAt(s.History.Count - 1);
                    throw;
                }
                m.Api(resp);

                var content = resp["content"] as JsonArray ?? new JsonArray();
                string stop = null;
                try { stop = resp["stop_reason"]?.GetValue<string>(); } catch { }
                m.StopReason = stop;

                // preserve the assistant turn verbatim (thinking + tool_use blocks) for the next request
                s.History.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

                var toolResults = new JsonArray();

                foreach (var block in content)
                {
                    string type = null;
                    try { type = block["type"]?.GetValue<string>(); } catch { }

                    if (type == "text")
                    {
                        string t = null;
                        try { t = block["text"]?.GetValue<string>(); } catch { }
                        if (!string.IsNullOrEmpty(t)) output(t);
                    }
                    else if (type == "fallback")
                    {
                        m.Fallback = true;
                        string to = null;
                        try { to = block["to"]?["model"]?.GetValue<string>(); } catch { }
                        output("\n_(served by fallback model " + (to ?? "?") + ")_\n");
                    }
                    else if (type == "tool_use")
                    {
                        string tname = (string)block["name"];
                        string tid = (string)block["id"];
                        var tinput = block["input"] as JsonObject ?? new JsonObject();
                        status?.Invoke("Calling " + tname + "…");
                        output("\n`[" + tname + "]`\n");
                        m.ToolCalls++;

                        string result; bool err = false;
                        try
                        {
                            result = await AgentTools.ExecuteAsync(tname, tinput, doc, s.UserMemory, s.DrawingMemory);
                            if (result != null && result.Length > MaxToolResultChars)
                                result = result.Substring(0, MaxToolResultChars) +
                                         "\n…[truncated: result too large; ask for a narrower query]";
                        }
                        catch (Exception ex) { result = "Error: " + ex.Message; err = true; }

                        var tr = new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = tid,
                            ["content"] = result ?? ""
                        };
                        if (err) tr["is_error"] = true;
                        toolResults.Add(tr);
                    }
                }

                if (stop == "refusal")
                {
                    string expl = null;
                    try { expl = resp["stop_details"]?["explanation"]?.GetValue<string>(); } catch { }
                    output("\n_(The model declined this request" + (string.IsNullOrEmpty(expl) ? "" : ": " + expl) + ")_\n");
                    return;
                }
                if (stop == "max_tokens")
                    output("\n_(output truncated: max_tokens reached - raise it in Settings)_\n");

                if (toolResults.Count == 0) return; // end_turn — done
                s.History.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }
            output("\n_(stopped: too many tool iterations)_\n");
        }
    }
}
