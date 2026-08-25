// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Autodesk.AutoCAD.DatabaseServices;

namespace AutoGAD
{
    /// <summary>
    /// Persistent notes the agent keeps between sessions, in %APPDATA%\AutoGAD\memory\:
    ///
    ///   user.json              facts about this user - conventions, preferences, project meta
    ///   drawings\&lt;id&gt;.json     facts about one drawing
    ///
    /// Drawings are keyed by the database FingerprintGuid, which AutoCAD stamps when the drawing is
    /// created and preserves across saves, renames and moves - so the notes follow the drawing rather
    /// than its path. Nothing is ever written into the DWG itself.
    /// </summary>
    public class Memory
    {
        public const int MaxEntries = 200;          // oldest are dropped past this
        public const int MaxTextLength = 2000;

        /// <summary>One remembered fact.</summary>
        public class Entry
        {
            public string Id;
            public string Text;
            public string Category;                 // free-form, e.g. "convention", "project", "correction"
            public DateTime Created;

            public JsonObject ToJson() => new JsonObject
            {
                ["id"] = Id,
                ["text"] = Text,
                ["category"] = Category,
                ["created"] = Created.ToString("o")
            };

            public static Entry FromJson(JsonObject o)
            {
                if (o == null) return null;
                var e = new Entry
                {
                    Id = Val(o, "id"),
                    Text = Val(o, "text"),
                    Category = Val(o, "category")
                };
                if (string.IsNullOrEmpty(e.Text)) return null;
                DateTime.TryParse(Val(o, "created"), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out e.Created);
                if (string.IsNullOrEmpty(e.Id)) e.Id = NewId();
                return e;
            }

            private static string Val(JsonObject o, string k)
            {
                try { return o[k]?.GetValue<string>(); } catch { return null; }
            }
        }

        public string Scope;                        // "user" or "drawing"
        public string Title;                        // drawing file name, for display
        public readonly List<Entry> Entries = new List<Entry>();

        private string _path;

        // ---------------- locations ----------------

        public static string Dir => Path.Combine(Config.Dir, "memory");
        public static string DrawingsDir => Path.Combine(Dir, "drawings");
        public static string UserPath => Path.Combine(Dir, "user.json");

        /// <summary>
        /// Stable per-drawing key. FingerprintGuid survives save/rename/move; unsaved drawings and
        /// any database without one fall back to a hash of the file path.
        /// </summary>
        public static string DrawingKey(Database db, string fileName)
        {
            string raw = null;
            try
            {
                // AutoCAD exposes this as a string, e.g. "{2A1F...}"; empty on a never-saved drawing.
                string fp = db.FingerprintGuid;
                if (!string.IsNullOrWhiteSpace(fp) && fp.Trim('{', '}', ' ') != Guid.Empty.ToString())
                    raw = "fp:" + fp.Trim('{', '}', ' ').ToLowerInvariant();
            }
            catch { }

            if (raw == null)
                raw = "path:" + (fileName ?? "unsaved").ToLowerInvariant();

            // Hash so the filename never lands on disk as a path component.
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)))
                                   .Replace("-", "").Substring(0, 32).ToLowerInvariant();
        }

        // ---------------- load / save ----------------

        public static Memory LoadUser()
        {
            var m = Load(UserPath);
            m.Scope = "user";
            m.Title = Environment.UserName;
            return m;
        }

        public static Memory LoadDrawing(Database db, string fileName)
        {
            string key = DrawingKey(db, fileName);
            var m = Load(Path.Combine(DrawingsDir, key + ".json"));
            m.Scope = "drawing";
            m.Title = string.IsNullOrEmpty(fileName) ? "(unsaved drawing)" : Path.GetFileName(fileName);
            return m;
        }

        private static Memory Load(string path)
        {
            var m = new Memory { _path = path };
            try
            {
                if (!File.Exists(path)) return m;
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (root?["entries"] is JsonArray arr)
                {
                    foreach (var node in arr)
                    {
                        var e = Entry.FromJson(node as JsonObject);
                        if (e != null) m.Entries.Add(e);
                    }
                }
            }
            catch { /* a corrupt memory file must never block the agent */ }
            return m;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                // Keep the newest entries if we somehow blow past the cap.
                if (Entries.Count > MaxEntries)
                    Entries.RemoveRange(0, Entries.Count - MaxEntries);

                var arr = new JsonArray();
                foreach (var e in Entries) arr.Add(e.ToJson());

                var root = new JsonObject
                {
                    ["scope"] = Scope,
                    ["title"] = Title,
                    ["updated"] = DateTime.Now.ToString("o"),
                    ["entries"] = arr
                };
                File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best effort: losing a note must not break the answer */ }
        }

        // ---------------- mutations ----------------

        /// <summary>Add a fact. Near-duplicates replace the existing entry instead of piling up.</summary>
        public Entry Add(string text, string category)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) throw new ArgumentException("Memory text is empty.");
            if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength);

            var existing = Entries.FirstOrDefault(
                e => string.Equals(e.Text, text, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Category = category ?? existing.Category;
                Save();
                return existing;
            }

            var entry = new Entry
            {
                Id = NewId(),
                Text = text,
                Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                Created = DateTime.Now
            };
            Entries.Add(entry);
            Save();
            return entry;
        }

        /// <summary>Remove by id. Returns the removed text, or null when the id was unknown.</summary>
        public string Remove(string id)
        {
            var e = Entries.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            Entries.Remove(e);
            Save();
            return e.Text;
        }

        public void Clear()
        {
            Entries.Clear();
            Save();
        }

        // ---------------- rendering ----------------

        /// <summary>Render for the system prompt. Returns null when there is nothing worth sending.</summary>
        public string ToPrompt()
        {
            if (Entries.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var e in Entries)
            {
                sb.Append("- [").Append(e.Id).Append("] ");
                if (!string.IsNullOrEmpty(e.Category)) sb.Append('(').Append(e.Category).Append(") ");
                sb.AppendLine(e.Text);
            }
            return sb.ToString();
        }

        /// <summary>Human-readable listing for the AUTOGADMEM command.</summary>
        public string ToDisplay()
        {
            if (Entries.Count == 0) return "  (nothing remembered yet)";
            var sb = new StringBuilder();
            foreach (var e in Entries)
            {
                sb.Append("  [").Append(e.Id).Append("] ");
                if (!string.IsNullOrEmpty(e.Category)) sb.Append('(').Append(e.Category).Append(") ");
                sb.Append(e.Text);
                sb.Append("   -- ").Append(e.Created.ToString("yyyy-MM-dd"));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 6);
    }
}
