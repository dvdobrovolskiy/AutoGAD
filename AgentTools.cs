// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoGAD
{
    /// <summary>Defines the agent's tools and executes them against the live drawing.</summary>
    public static class AgentTools
    {
        /// <summary>JSON tool schemas sent to Claude.</summary>
        public static JsonArray Schemas()
        {
            return new JsonArray
            {
                Tool("query_entities",
                    "Query entities in the drawing. Returns matching entities with geometry/properties. " +
                    "Filter by entity type (class name substring, e.g. 'Line','Circle','BlockReference','MText','Dimension','Hatch') and/or layer name. Use this to count or inspect real geometry.",
                    Props(
                        ("type", "string", "Entity class-name filter (substring, case-insensitive). Omit for all types."),
                        ("layer", "string", "Layer name filter (exact). Omit for all layers."),
                        ("limit", "integer", "Max entities to return (default 100)."))),

                Tool("get_block_inventory",
                    "Counts of block references in model space, grouped by effective (real) block name. Use for fixture/equipment inventory.",
                    Props()),

                Tool("get_layers",
                    "Full layer table: name, color, on/frozen/locked state.",
                    Props()),

                Tool("get_tables",
                    "All AutoCAD tables (schedules/BOM) in model space, as text cell grids. Use for specifications and fixture/equipment schedules.",
                    Props()),

                Tool("get_context",
                    "Refresh and return the compact drawing context (meta, units, extents, layers, layouts, entity counts, block inventory, tables).",
                    Props()),

                // ---- write tools (each prompts the user for confirmation unless auto-approve is on) ----
                Tool("run_command",
                    "Run an AutoCAD command line string in the active drawing (e.g. 'ZOOM E', 'REGEN'). Queued into AutoCAD's command context. The user is asked to confirm before it runs.",
                    Props(("command", "string", "The command-line text to execute, e.g. 'ZOOM\\nE'.")), new[] { "command" }),

                Tool("set_layer_state",
                    "Change a layer's state. action one of: on, off, freeze, thaw, lock, unlock. Optional color (ACI 1-255). User confirms first.",
                    Props(
                        ("name", "string", "Layer name."),
                        ("action", "string", "on | off | freeze | thaw | lock | unlock"),
                        ("color", "integer", "Optional ACI color index 1-255.")), new[] { "name", "action" }),

                Tool("create_text",
                    "Create a single-line text (DBText) in model space. User confirms first.",
                    Props(
                        ("text", "string", "The text string."),
                        ("x", "number", "Insertion X."),
                        ("y", "number", "Insertion Y."),
                        ("height", "number", "Text height (drawing units)."),
                        ("layer", "string", "Optional target layer (must exist; defaults to current).")),
                    new[] { "text", "x", "y", "height" }),

                Tool("replace_text",
                    "Programmatically find-and-replace a substring across the WHOLE drawing — MText, DBText, " +
                    "block attribute values, attribute definitions, MLeader text and table cells — in one pass, " +
                    "no dialog. This is the correct way to change text everywhere (e.g. a project code). " +
                    "The user confirms the count before it applies. Use this INSTEAD of the FIND command.",
                    Props(
                        ("find", "string", "Exact substring to find (case-sensitive)."),
                        ("replace", "string", "Replacement substring.")),
                    new[] { "find", "replace" }),

                Tool("set_custom_property",
                    "Set (or clear) a drawing custom property / metadata field (DWGPROPS Custom tab) directly, " +
                    "no dialog. e.g. name='ProjectCode'. Use this INSTEAD of the DWGPROPS command.",
                    Props(
                        ("name", "string", "Custom property name, e.g. ProjectCode."),
                        ("value", "string", "New value. Empty string removes the property.")),
                    new[] { "name", "value" }),

                Tool("set_text",
                    "Set the full text of one entity by its handle (DBText, MText, or a block AttributeReference). " +
                    "Use for surgical single edits; use replace_text for global changes.",
                    Props(
                        ("handle", "string", "Entity handle (hex, as returned by query_entities)."),
                        ("text", "string", "New text/contents.")),
                    new[] { "handle", "text" }),

                // ---- memory (no confirmation dialog: these write notes, never the drawing) ----
                Tool("remember",
                    "Save a durable note that will be given back to you in later sessions. Two scopes:\n" +
                    "  scope='drawing' — facts about THIS drawing: what it is, project/object code, which layers " +
                    "or blocks mean what, where the schedules live, quirks, decisions already made.\n" +
                    "  scope='user' — facts about THIS engineer that hold across drawings: standards and " +
                    "conventions they follow, preferred voltages/cable types/calculation methods, naming habits, " +
                    "how they like answers presented, ongoing projects.\n" +
                    "Save a note when you learn something durable and non-obvious that would save work next time — " +
                    "especially when the user corrects you or states a preference. Do NOT save the answer to a " +
                    "one-off question, anything you can re-read from the drawing at any time, or anything the user " +
                    "asked you to keep private. Write each note as one self-contained sentence that will still make " +
                    "sense months from now with no conversation around it.",
                    Props(
                        ("scope", "string", "'drawing' (this file only) or 'user' (all drawings)."),
                        ("text", "string", "The fact, as one self-contained sentence."),
                        ("category", "string", "Optional short tag, e.g. 'convention', 'project', 'correction', 'layout'.")),
                    new[] { "scope", "text" }),

                Tool("forget",
                    "Delete a remembered note by its id (the [id] shown in the memory listing). " +
                    "Use when a note is wrong or out of date - if it is merely incomplete, save a corrected one instead.",
                    Props(
                        ("scope", "string", "'drawing' or 'user' - which store the id belongs to."),
                        ("id", "string", "The note id to delete.")),
                    new[] { "scope", "id" }),
            };
        }

        /// <summary>Execute a tool. Read tools run inline; write tools confirm then run in command context.</summary>
        /// <param name="userMem">Cross-drawing notes for this user.</param>
        /// <param name="drawingMem">Notes for the active drawing.</param>
        public static async Task<string> ExecuteAsync(string name, JsonObject input, Document doc,
                                                      Memory userMem = null, Memory drawingMem = null)
        {
            switch (name)
            {
                case "query_entities": return QueryEntities(doc, input);
                case "get_block_inventory": return BlockInventory(doc);
                case "get_layers": return GetLayers(doc);
                case "get_tables": return GetTables(doc);
                case "get_context": return CadJson.BuildContext(doc.Database);

                case "run_command": return await RunCommand(doc, input);
                case "set_layer_state": return await SetLayerState(doc, input);
                case "create_text": return await CreateText(doc, input);
                case "replace_text": return await ReplaceText(doc, input);
                case "set_custom_property": return await SetCustomProperty(doc, input);
                case "set_text": return await SetText(doc, input);

                case "remember": return Remember(input, userMem, drawingMem);
                case "forget": return Forget(input, userMem, drawingMem);

                default: return "Error: unknown tool '" + name + "'";
            }
        }

        // ---------------- memory ----------------

        /// <summary>Resolve the 'scope' argument to one of the two stores.</summary>
        private static Memory PickStore(JsonObject input, Memory userMem, Memory drawingMem, out string error)
        {
            error = null;
            string scope = (Str(input, "scope") ?? "").Trim().ToLowerInvariant();

            if (scope == "user")
            {
                if (userMem == null) { error = "Error: user memory is unavailable."; return null; }
                return userMem;
            }
            if (scope == "drawing")
            {
                if (drawingMem == null) { error = "Error: drawing memory is unavailable (no active drawing)."; return null; }
                return drawingMem;
            }
            error = "Error: scope must be 'drawing' or 'user'.";
            return null;
        }

        private static string Remember(JsonObject input, Memory userMem, Memory drawingMem)
        {
            var store = PickStore(input, userMem, drawingMem, out string error);
            if (store == null) return error;

            string text = Str(input, "text");
            if (string.IsNullOrWhiteSpace(text)) return "Error: 'text' is required.";

            try
            {
                var entry = store.Add(text, Str(input, "category"));
                return "Saved to " + store.Scope + " memory as [" + entry.Id + "]. " +
                       "It will be in your context automatically next time.";
            }
            catch (Exception ex) { return "Error: could not save the note: " + ex.Message; }
        }

        private static string Forget(JsonObject input, Memory userMem, Memory drawingMem)
        {
            var store = PickStore(input, userMem, drawingMem, out string error);
            if (store == null) return error;

            string id = Str(input, "id");
            if (string.IsNullOrWhiteSpace(id)) return "Error: 'id' is required.";

            string removed = store.Remove(id.Trim());
            return removed == null
                ? "No note with id '" + id + "' in " + store.Scope + " memory."
                : "Deleted from " + store.Scope + " memory: " + removed;
        }

        // ---------------- reads ----------------

        private static string QueryEntities(Document doc, JsonObject input)
        {
            string typeF = Str(input, "type");
            string layerF = Str(input, "layer");
            int limit = Int(input, "limit", 100);
            var results = new List<object>();
            int scanned = 0, matched = 0;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    scanned++;
                    if (typeF != null && ent.GetType().Name.IndexOf(typeF, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (layerF != null && !string.Equals(ent.Layer, layerF, StringComparison.OrdinalIgnoreCase)) continue;
                    matched++;
                    if (results.Count < limit) results.Add(CadJson.DumpEntity(ent, tr));
                }
                tr.Commit();
            }
            return CadJson.Serialize(new Dictionary<string, object>
            {
                ["scanned"] = scanned,
                ["matched"] = matched,
                ["returned"] = results.Count,
                ["truncated"] = matched > results.Count,
                ["entities"] = results
            });
        }

        private static string BlockInventory(Document doc)
        {
            var counts = new Dictionary<string, int>();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br != null) CadJson.Bump(counts, CadJson.EffectiveBlockName(br, tr) ?? "?");
                }
                tr.Commit();
            }
            return CadJson.Serialize(counts.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => (object)k.Value));
        }

        private static string GetLayers(Document doc)
        {
            var list = new List<object>();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    list.Add(new Dictionary<string, object>
                    {
                        ["name"] = l.Name, ["color"] = CadJson.ColorString(l.Color),
                        ["off"] = l.IsOff, ["frozen"] = l.IsFrozen, ["locked"] = l.IsLocked,
                        ["lineWeight"] = l.LineWeight.ToString()
                    });
                }
                tr.Commit();
            }
            return CadJson.Serialize(list);
        }

        private static string GetTables(Document doc)
        {
            object tables;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                tables = CadJson.DumpTables(ms, tr);
                tr.Commit();
            }
            return CadJson.Serialize(tables);
        }

        // ---------------- gated writes ----------------

        /// <summary>Gate a write: auto-approve (setting or "allow all this session") or a confirm dialog.</summary>
        private static bool Confirm(string title, string detail) => Approval.Confirm(title, detail);

        private static async Task<string> RunCommand(Document doc, JsonObject input)
        {
            string cmd = Str(input, "command");
            if (string.IsNullOrWhiteSpace(cmd)) return "Error: no command.";
            if (!Confirm("Run AutoCAD command", cmd)) return "User declined to run this command. Ask them what they'd prefer instead.";
            doc.SendStringToExecute(cmd.EndsWith("\n") ? cmd : cmd + "\n", true, false, true);
            await Task.Yield();
            return "Command queued: " + cmd;
        }

        private static async Task<string> SetLayerState(Document doc, JsonObject input)
        {
            string lname = Str(input, "name");
            string action = (Str(input, "action") ?? "").ToLowerInvariant();
            int color = Int(input, "color", -1);
            if (string.IsNullOrWhiteSpace(lname)) return "Error: no layer name.";
            if (!Confirm($"Change layer '{lname}'", $"{lname}: {action}" + (color > 0 ? $", color {color}" : ""))) return "User declined the change.";

            string result = "ok";
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(async (o) =>
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(lname)) { result = "Error: layer not found."; tr.Commit(); return; }
                    var l = (LayerTableRecord)tr.GetObject(lt[lname], OpenMode.ForWrite);
                    switch (action)
                    {
                        case "on": l.IsOff = false; break;
                        case "off": l.IsOff = true; break;
                        case "freeze": l.IsFrozen = true; break;
                        case "thaw": l.IsFrozen = false; break;
                        case "lock": l.IsLocked = true; break;
                        case "unlock": l.IsLocked = false; break;
                        default: result = "Error: unknown action '" + action + "'"; break;
                    }
                    if (color >= 1 && color <= 255)
                        l.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)color);
                    tr.Commit();
                }
                await Task.Yield();
            }, null);
            return result;
        }

        private static async Task<string> CreateText(Document doc, JsonObject input)
        {
            string text = Str(input, "text");
            double x = Dbl(input, "x", 0), y = Dbl(input, "y", 0), h = Dbl(input, "height", 2.5);
            string layer = Str(input, "layer");
            if (string.IsNullOrEmpty(text)) return "Error: no text.";
            if (!Confirm("Create text", $"\"{text}\"\n  at ({x}, {y}), height {h}" + (layer != null ? $", layer '{layer}'" : ""))) return "User declined the change.";

            string result = "created";
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(async (o) =>
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite);
                    var t = new DBText { TextString = text, Position = new Point3d(x, y, 0), Height = h };
                    if (!string.IsNullOrEmpty(layer))
                    {
                        var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(layer)) t.Layer = layer;
                    }
                    ms.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                    result = "Created text, handle " + t.Handle;
                    tr.Commit();
                }
                await Task.Yield();
            }, null);
            return result;
        }

        private static async Task<string> ReplaceText(Document doc, JsonObject input)
        {
            string find = Str(input, "find");
            string repl = Str(input, "replace") ?? "";
            if (string.IsNullOrEmpty(find)) return "Error: 'find' is empty.";

            // dry-run count first
            int entities, occurrences;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                WalkText(doc.Database, tr, find, repl, false, out entities, out occurrences);
                tr.Commit();
            }
            if (occurrences == 0) return $"No occurrences of \"{find}\" found.";
            if (!Confirm("Replace text across the drawing", $"\"{find}\"  ->  \"{repl}\"\n{occurrences} occurrence(s) in {entities} object(s) across the whole drawing."))
                return "User declined the change.";

            int e2 = 0, o2 = 0;
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(async (o) =>
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    WalkText(doc.Database, tr, find, repl, true, out e2, out o2);
                    tr.Commit();
                }
                doc.Editor.Regen();
                await Task.Yield();
            }, null);
            return $"Replaced {o2} occurrence(s) in {e2} object(s).";
        }

        /// <summary>Find/replace across every text-bearing object in every block record. Counts or writes.</summary>
        private static void WalkText(Database db, Transaction tr, string find, string repl, bool write,
            out int entities, out int occurrences)
        {
            entities = 0; occurrences = 0;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    int hits;

                    if (obj is DBText t && (hits = Count(t.TextString, find)) > 0)
                    {
                        entities++; occurrences += hits;
                        if (write) ((DBText)tr.GetObject(id, OpenMode.ForWrite)).TextString = t.TextString.Replace(find, repl);
                    }
                    else if (obj is MText m && (hits = Count(m.Contents, find)) > 0)
                    {
                        entities++; occurrences += hits;
                        if (write) ((MText)tr.GetObject(id, OpenMode.ForWrite)).Contents = m.Contents.Replace(find, repl);
                    }
                    else if (obj is AttributeDefinition ad && (hits = Count(ad.TextString, find)) > 0)
                    {
                        entities++; occurrences += hits;
                        if (write) ((AttributeDefinition)tr.GetObject(id, OpenMode.ForWrite)).TextString = ad.TextString.Replace(find, repl);
                    }
                    else if (obj is MLeader ml && ml.MText != null && (hits = Count(ml.MText.Contents, find)) > 0)
                    {
                        entities++; occurrences += hits;
                        if (write)
                        {
                            var mlw = (MLeader)tr.GetObject(id, OpenMode.ForWrite);
                            var mt = mlw.MText;
                            mt.Contents = mt.Contents.Replace(find, repl);
                            mlw.MText = mt;
                        }
                    }
                    else if (obj is Table tb)
                    {
                        for (int r = 0; r < tb.Rows.Count; r++)
                            for (int c = 0; c < tb.Columns.Count; c++)
                            {
                                string cell = null;
                                try { cell = tb.Cells[r, c].TextString; } catch { }
                                int h = Count(cell, find);
                                if (h > 0)
                                {
                                    occurrences += h; entities++;
                                    if (write) { try { tb.UpgradeOpen(); tb.Cells[r, c].TextString = cell.Replace(find, repl); } catch { } }
                                }
                            }
                    }
                    else if (obj is BlockReference br)
                    {
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            var ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                            if (ar == null) continue;
                            int h = Count(ar.TextString, find);
                            if (h > 0)
                            {
                                entities++; occurrences += h;
                                if (write) ((AttributeReference)tr.GetObject(aid, OpenMode.ForWrite)).TextString = ar.TextString.Replace(find, repl);
                            }
                        }
                    }
                }
            }
        }

        private static int Count(string s, string find)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0, i = 0;
            while ((i = s.IndexOf(find, i, StringComparison.Ordinal)) >= 0) { n++; i += find.Length; }
            return n;
        }

        private static async Task<string> SetCustomProperty(Document doc, JsonObject input)
        {
            string name = Str(input, "name");
            string value = Str(input, "value") ?? "";
            if (string.IsNullOrWhiteSpace(name)) return "Error: no property name.";
            if (!Confirm("Set drawing metadata", $"{name} = '{value}'" + (value.Length == 0 ? "  (remove the property)" : ""))) return "User declined the change.";

            string result = "ok";
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(async (o) =>
            {
                try
                {
                    var db = doc.Database;
                    var b = new DatabaseSummaryInfoBuilder(db.SummaryInfo);
                    var table = b.CustomPropertyTable;
                    if (value.Length == 0) { if (table.Contains(name)) table.Remove(name); }
                    else table[name] = value;
                    db.SummaryInfo = b.ToDatabaseSummaryInfo();
                    result = $"Metadata '{name}' set.";
                }
                catch (Exception ex) { result = "Error: " + ex.Message; }
                await Task.Yield();
            }, null);
            return result;
        }

        private static async Task<string> SetText(Document doc, JsonObject input)
        {
            string handle = Str(input, "handle");
            string text = Str(input, "text") ?? "";
            if (string.IsNullOrWhiteSpace(handle)) return "Error: no handle.";
            if (!Confirm($"Set text of entity {handle}", text)) return "User declined the change.";

            string result = "ok";
            await AcApp.DocumentManager.ExecuteInCommandContextAsync(async (o) =>
            {
                try
                {
                    var db = doc.Database;
                    long h = Convert.ToInt64(handle, 16);
                    ObjectId id = db.GetObjectId(false, new Handle(h), 0);
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var obj = tr.GetObject(id, OpenMode.ForWrite);
                        if (obj is DBText t) t.TextString = text;
                        else if (obj is MText m) m.Contents = text;
                        else if (obj is AttributeReference ar) ar.TextString = text;
                        else { result = "Error: " + obj.GetType().Name + " is not editable text."; tr.Commit(); return; }
                        tr.Commit();
                    }
                    doc.Editor.Regen();
                    result = "Text updated.";
                }
                catch (Exception ex) { result = "Error: " + ex.Message; }
                await Task.Yield();
            }, null);
            return result;
        }

        // ---------------- schema + input helpers ----------------

        private static JsonObject Tool(string name, string desc, JsonObject props, string[] required = null)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            var req = new JsonArray();
            if (required != null) foreach (var r in required) req.Add(r);
            schema["required"] = req;
            return new JsonObject { ["name"] = name, ["description"] = desc, ["input_schema"] = schema };
        }

        private static JsonObject Props(params (string name, string type, string desc)[] fields)
        {
            var o = new JsonObject();
            foreach (var f in fields)
                o[f.name] = new JsonObject { ["type"] = f.type, ["description"] = f.desc };
            return o;
        }

        private static string Str(JsonObject o, string k)
        {
            var n = o?[k];
            try { return n == null ? null : n.GetValue<string>(); } catch { return n?.ToString(); }
        }
        private static int Int(JsonObject o, string k, int d)
        { try { var n = o?[k]; return n == null ? d : n.GetValue<int>(); } catch { return d; } }
        private static double Dbl(JsonObject o, string k, double d)
        { try { var n = o?[k]; return n == null ? d : n.GetValue<double>(); } catch { return d; } }
    }
}
