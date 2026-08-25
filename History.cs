// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Autodesk.AutoCAD.ApplicationServices;

namespace AutoGAD
{
    /// <summary>
    /// Persistent chat transcripts, one JSON file per drawing in %APPDATA%\AutoGAD\history\&lt;key&gt;.json.
    ///
    /// What is stored is the *displayed* transcript (user / assistant / system lines with timestamps) -
    /// not the raw API history with tool results, which would be huge and is rebuilt fresh from the live
    /// drawing anyway. The tail is shown again when the palette opens and a short excerpt is given to
    /// Claude as "recent conversation" context.
    ///
    /// Recycling so it never grows unbounded:
    ///   - per file:   at most MaxEntries entries / MaxBytes chars (oldest dropped, each text capped)
    ///   - per folder: files untouched for MaxAgeDays are deleted, and if the folder exceeds
    ///                 MaxTotalBytes the oldest files go first (run once per AutoCAD session).
    /// </summary>
    public class History
    {
        public const int MaxEntries = 400;
        public const int MaxBytes = 400 * 1024;
        public const int MaxText = 20000;
        public const int MaxAgeDays = 180;
        public const long MaxTotalBytes = 25L * 1024 * 1024;

        public const int ShowOnOpen = 40;        // transcript lines restored into the panel
        public const int PromptEntries = 16;     // lines handed to Claude as context
        public const int PromptText = 600;       // chars per line in that excerpt

        public class Entry
        {
            public string Role;   // "user" | "assistant" | "system"
            public string Text;
            public string Ts;     // ISO local time, seconds precision
        }

        public static string Dir => Path.Combine(Config.Dir, "history");

        public string Key { get; }
        public string Title { get; }
        public string Path_ { get; }
        public readonly List<Entry> Entries = new List<Entry>();

        public History(Document doc)
        {
            if (doc?.Database != null)
            {
                Key = Memory.DrawingKey(doc.Database, doc.Name);
                Title = string.IsNullOrEmpty(doc.Name) ? "(unsaved drawing)" : Path.GetFileName(doc.Name);
            }
            else
            {
                Key = "nodoc";
                Title = "(no drawing)";
            }
            Path_ = Path.Combine(Dir, Key + ".json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(Path_)) return;
                var root = JsonNode.Parse(File.ReadAllText(Path_)) as JsonObject;
                if (!(root?["entries"] is JsonArray arr)) return;
                foreach (var n in arr)
                {
                    var o = n as JsonObject;
                    string role = Str(o, "role"), text = Str(o, "text");
                    if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(text)) continue;
                    Entries.Add(new Entry { Role = role, Text = text, Ts = Str(o, "ts") ?? "" });
                }
            }
            catch { Entries.Clear(); }
        }

        private static string Str(JsonObject o, string k)
        {
            try { return o?[k]?.GetValue<string>(); } catch { return null; }
        }

        public void Append(string role, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > MaxText) text = text.Substring(0, MaxText) + "\n…(truncated)";
            Entries.Add(new Entry { Role = role, Text = text, Ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") });
            Trim();
            Save();
        }

        private void Trim()
        {
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            // char cap: drop from the front until under the limit
            while (Entries.Count > 2 && Entries.Sum(e => e.Text.Length) > MaxBytes)
                Entries.RemoveAt(0);
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var arr = new JsonArray();
                foreach (var e in Entries)
                    arr.Add(new JsonObject { ["role"] = e.Role, ["text"] = e.Text, ["ts"] = e.Ts });
                var root = new JsonObject
                {
                    ["key"] = Key, ["title"] = Title,
                    ["updated"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["entries"] = arr
                };
                string tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
                File.Move(tmp, Path_, true);
            }
            catch { /* best effort */ }
        }

        public void Clear()
        {
            Entries.Clear();
            try { if (File.Exists(Path_)) File.Delete(Path_); } catch { }
        }

        public IEnumerable<Entry> Tail(int n = ShowOnOpen) =>
            Entries.Skip(Math.Max(0, Entries.Count - n));

        /// <summary>Short excerpt of earlier sessions for the system prompt, or null.</summary>
        public string ToPrompt()
        {
            var ents = Entries.Where(e => e.Role == "user" || e.Role == "assistant").ToList();
            if (ents.Count == 0) return null;
            ents = ents.Skip(Math.Max(0, ents.Count - PromptEntries)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("# Recent conversation about this drawing (earlier sessions, for continuity - " +
                          "the drawing may have changed since)");
            foreach (var e in ents)
            {
                string t = e.Text.Replace("\r", "").Replace("\n", " ");
                if (t.Length > PromptText) t = t.Substring(0, PromptText) + "…";
                string ts = e.Ts.Length >= 16 ? e.Ts.Substring(0, 16) : e.Ts;
                sb.Append(ts).Append(' ').Append(e.Role == "user" ? "User" : "You").Append(": ").AppendLine(t);
            }
            return sb.ToString();
        }

        private static bool _cleaned;

        /// <summary>Folder-level recycling; cheap, runs once per session.</summary>
        public static void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;
            try
            {
                if (!Directory.Exists(Dir)) return;
                var files = new List<FileInfo>();
                var now = DateTime.Now;
                foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json"))
                {
                    if ((now - f.LastWriteTime).TotalDays > MaxAgeDays) { try { f.Delete(); } catch { } continue; }
                    files.Add(f);
                }
                long total = files.Sum(f => f.Length);
                foreach (var f in files.OrderBy(f => f.LastWriteTime))     // oldest first
                {
                    if (total <= MaxTotalBytes) break;
                    try { f.Delete(); total -= f.Length; } catch { }
                }
            }
            catch { }
        }
    }
}
