// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

namespace AutoGAD
{
    /// <summary>
    /// Shared helpers for the agent: a compact per-document context (cached prompt prefix)
    /// and read helpers the tools use to query the live drawing database.
    /// </summary>
    public static class CadJson
    {
        private static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Serialize(object o) => JsonSerializer.Serialize(o, Pretty);

        // ---- compact context: enough to reason about the drawing, not the full entity dump ----

        public static string BuildContext(Database db)
        {
            var root = new Dictionary<string, object>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                root["drawing"] = new Dictionary<string, object>
                {
                    ["filename"] = Safe(() => db.Filename),
                    ["fingerprintGuid"] = Safe(() => db.FingerprintGuid.ToString()),
                    ["tdupdate"] = Safe(() => db.Tdupdate.ToString()),
                };
                try
                {
                    var si = db.SummaryInfo;
                    var custom = new Dictionary<string, object>();
                    var e = si.CustomProperties;
                    while (e.MoveNext()) custom[Convert.ToString(e.Key)] = Convert.ToString(e.Value);
                    root["summary"] = new Dictionary<string, object>
                    {
                        ["title"] = si.Title, ["author"] = si.Author, ["custom"] = custom
                    };
                }
                catch { }

                root["units"] = new Dictionary<string, object>
                {
                    ["insUnits"] = Safe(() => db.Insunits.ToString()),
                    ["measurement"] = Safe(() => db.Measurement.ToString()),
                };
                root["extents"] = new Dictionary<string, object>
                {
                    ["min"] = P(Safe(() => db.Extmin)),
                    ["max"] = P(Safe(() => db.Extmax)),
                };

                // layers
                var layers = new List<object>();
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    layers.Add(new Dictionary<string, object>
                    {
                        ["name"] = l.Name, ["color"] = ColorString(l.Color),
                        ["off"] = l.IsOff, ["frozen"] = l.IsFrozen, ["locked"] = l.IsLocked
                    });
                }
                root["layers"] = layers;

                // layouts
                var layouts = new List<object>();
                var ld = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry de in ld)
                {
                    var lay = (Layout)tr.GetObject(de.Value, OpenMode.ForRead);
                    layouts.Add(new Dictionary<string, object> { ["name"] = lay.LayoutName, ["tab"] = lay.TabOrder });
                }
                root["layouts"] = layouts;

                // model-space entity-type counts + block-reference inventory (by effective name)
                var typeCounts = new Dictionary<string, int>();
                var blockCounts = new Dictionary<string, int>();
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    Bump(typeCounts, ent.GetType().Name);
                    if (ent is BlockReference br)
                        Bump(blockCounts, EffectiveBlockName(br, tr) ?? "?");
                }
                root["entityCounts"] = typeCounts;
                root["blockInventory"] = blockCounts;

                // tables (the schedules) — full cell text, deduped by content
                root["tables"] = DumpTables(ms, tr);

                tr.Commit();
            }
            return Serialize(root);
        }

        public static List<object> DumpTables(BlockTableRecord ms, Transaction tr)
        {
            var tables = new List<object>();
            var seen = new HashSet<string>();
            foreach (ObjectId id in ms)
            {
                var tb = tr.GetObject(id, OpenMode.ForRead) as Table;
                if (tb == null) continue;
                var rows = new List<object>();
                var key = new StringBuilder();
                for (int r = 0; r < tb.Rows.Count; r++)
                {
                    var row = new List<object>();
                    for (int c = 0; c < tb.Columns.Count; c++)
                    {
                        string cell = null;
                        try { cell = StripMText(tb.Cells[r, c].TextString); } catch { }
                        row.Add(cell);
                        key.Append(cell).Append('|');
                    }
                    rows.Add(row);
                }
                if (seen.Add(key.ToString())) // skip duplicated copies of the same table
                    tables.Add(new Dictionary<string, object> { ["rows"] = tb.Rows.Count, ["cols"] = tb.Columns.Count, ["cells"] = rows });
            }
            return tables;
        }

        // ---- per-entity dump used by the query_entities tool ----

        public static Dictionary<string, object> DumpEntity(Entity ent, Transaction tr)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = ent.GetType().Name,
                ["handle"] = ent.Handle.ToString(),
                ["layer"] = ent.Layer,
                ["color"] = ColorString(ent.Color),
            };
            try { var ex = ent.GeometricExtents; d["bbox"] = new { min = P(ex.MinPoint), max = P(ex.MaxPoint) }; }
            catch { }
            switch (ent)
            {
                case Line line: d["start"] = P(line.StartPoint); d["end"] = P(line.EndPoint); d["length"] = line.Length; break;
                case Circle c: d["center"] = P(c.Center); d["radius"] = c.Radius; break;
                case Arc a: d["center"] = P(a.Center); d["radius"] = a.Radius; break;
                case DBText t: d["text"] = t.TextString; d["position"] = P(t.Position); d["height"] = t.Height; break;
                case MText m: d["text"] = StripMText(m.Contents); d["position"] = P(m.Location); d["height"] = m.TextHeight; break;
                case BlockReference br:
                    d["blockName"] = EffectiveBlockName(br, tr);
                    d["position"] = P(br.Position); d["rotation"] = br.Rotation;
                    d["attributes"] = DumpAttributes(br, tr);
                    break;
                case Dimension dim: d["measurement"] = Safe(() => dim.Measurement); d["dimText"] = Safe(() => dim.DimensionText); break;
                case Hatch h: d["patternName"] = Safe(() => h.PatternName); d["area"] = Safe(() => h.Area); break;
            }
            return d;
        }

        private static List<object> DumpAttributes(BlockReference br, Transaction tr)
        {
            var list = new List<object>();
            foreach (ObjectId aid in br.AttributeCollection)
            {
                var att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (att != null) list.Add(new Dictionary<string, object> { ["tag"] = att.Tag, ["value"] = att.TextString });
            }
            return list;
        }

        public static string EffectiveBlockName(BlockReference br, Transaction tr)
        {
            try
            {
                var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var rec = tr.GetObject(id, OpenMode.ForRead) as SymbolTableRecord;
                return rec != null ? rec.Name : null;
            }
            catch { return null; }
        }

        // ---- small helpers ----

        public static double[] P(Point3d p) => new[] { p.X, p.Y, p.Z };
        public static double[] P(object o) => o is Point3d p ? new[] { p.X, p.Y, p.Z } : null;

        public static void Bump(Dictionary<string, int> d, string k) { d.TryGetValue(k, out int n); d[k] = n + 1; }

        public static string ColorString(Color c)
        {
            try { if (c == null) return null; return c.IsByAci ? c.ColorIndex.ToString() : $"RGB({c.Red},{c.Green},{c.Blue})"; }
            catch { return null; }
        }

        public static object Safe(Func<object> f) { try { return f(); } catch { return null; } }

        private static readonly Regex RxFont = new Regex(@"\\[Ff][^;]*;");
        private static readonly Regex RxFmt = new Regex(@"\\[A-Za-z][^;\\]*;");
        private static readonly Regex RxLone = new Regex(@"\\[A-Za-z]");
        public static string StripMText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = RxFont.Replace(s, "");
            s = RxFmt.Replace(s, "");
            s = s.Replace("\\P", "\n").Replace("\\~", " ").Replace("{", "").Replace("}", "");
            s = RxLone.Replace(s, "");
            return s.Trim();
        }
    }
}
