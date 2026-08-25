// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Colors;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AutoGAD.ExportCommand))]

namespace AutoGAD
{
    /// <summary>
    /// GADEXPORT: walks the active drawing's database and writes a full JSON dump
    /// (drawing meta, units, extents, symbol tables, block defs, every space entity,
    /// attributes, xrefs and layouts) next to the DWG for offline analysis.
    /// </summary>
    public class ExportCommand
    {
        [CommandMethod("GADEXPORT")]
        public void Export()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                var root = new Dictionary<string, object>();
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    root["schema"] = "autogad.export/1";
                    root["drawing"] = DumpDrawing(db);
                    root["units"] = DumpUnits(db);
                    root["extents"] = DumpExtents(db);
                    root["layers"] = DumpLayers(db, tr);
                    root["linetypes"] = DumpLinetypes(db, tr);
                    root["textStyles"] = DumpTextStyles(db, tr);
                    root["dimStyles"] = DumpDimStyles(db, tr);
                    root["layouts"] = DumpLayouts(db, tr);

                    var blockDefs = new List<object>();
                    var spaces = new List<object>();
                    var xrefs = new List<object>();
                    var counts = new Dictionary<string, int>();

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId btrId in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);

                        if (btr.IsFromExternalReference)
                            xrefs.Add(DumpXref(btr));

                        if (btr.IsLayout)
                        {
                            spaces.Add(DumpSpace(btr, tr, counts));
                        }
                        else if (!btr.IsFromExternalReference && !btr.IsAnonymous)
                        {
                            blockDefs.Add(DumpBlockDefinition(btr, tr));
                        }
                    }

                    root["blockDefinitions"] = blockDefs;
                    root["spaces"] = spaces;
                    root["xrefs"] = xrefs;
                    root["entityCounts"] = counts;

                    tr.Commit();
                }

                string path = OutputPath(db);
                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(path, JsonSerializer.Serialize(root, opts));

                ed.WriteMessage($"\nGADEXPORT: wrote {path}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nGADEXPORT failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ---------- top-level sections ----------

        private static Dictionary<string, object> DumpDrawing(Database db)
        {
            var d = new Dictionary<string, object>
            {
                ["filename"] = Safe(() => db.Filename),
                ["originalFileName"] = Safe(() => db.OriginalFileName),
                ["fingerprintGuid"] = Safe(() => db.FingerprintGuid.ToString()),
                ["versionGuid"] = Safe(() => db.VersionGuid.ToString()),
                ["dwgVersion"] = Safe(() => db.LastSavedAsVersion.ToString()),
                ["tdcreate"] = Safe(() => db.Tdcreate.ToString()),
                ["tdupdate"] = Safe(() => db.Tdupdate.ToString()),
            };

            try
            {
                DatabaseSummaryInfo si = db.SummaryInfo;
                var info = new Dictionary<string, object>
                {
                    ["title"] = si.Title,
                    ["author"] = si.Author,
                    ["subject"] = si.Subject,
                    ["keywords"] = si.Keywords,
                    ["comments"] = si.Comments,
                    ["lastSavedBy"] = si.LastSavedBy,
                    ["revisionNumber"] = si.RevisionNumber,
                    ["hyperlinkBase"] = si.HyperlinkBase,
                };
                var custom = new Dictionary<string, object>();
                System.Collections.IDictionaryEnumerator e = si.CustomProperties;
                while (e.MoveNext())
                    custom[Convert.ToString(e.Key)] = Convert.ToString(e.Value);
                info["custom"] = custom;
                d["summaryInfo"] = info;
            }
            catch { /* summary info optional */ }

            return d;
        }

        private static Dictionary<string, object> DumpUnits(Database db) => new()
        {
            ["insUnits"] = Safe(() => db.Insunits.ToString()),
            ["measurement"] = Safe(() => db.Measurement.ToString()),
            ["lengthUnits"] = Safe(() => db.Lunits),
            ["lengthPrecision"] = Safe(() => db.Luprec),
            ["angleUnits"] = Safe(() => db.Aunits),
            ["anglePrecision"] = Safe(() => db.Auprec),
            ["currentLayer"] = Safe(() => LayerName(db, db.Clayer)),
        };

        private static Dictionary<string, object> DumpExtents(Database db) => new()
        {
            ["min"] = P(Safe(() => db.Extmin)),
            ["max"] = P(Safe(() => db.Extmax)),
            ["limMin"] = P2(Safe(() => db.Limmin)),
            ["limMax"] = P2(Safe(() => db.Limmax)),
        };

        private static List<object> DumpLayers(Database db, Transaction tr)
        {
            var list = new List<object>();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = l.Name,
                    ["color"] = ColorString(l.Color),
                    ["linetype"] = Safe(() => SymbolName(tr, l.LinetypeObjectId)),
                    ["lineWeight"] = l.LineWeight.ToString(),
                    ["isOff"] = l.IsOff,
                    ["isFrozen"] = l.IsFrozen,
                    ["isLocked"] = l.IsLocked,
                    ["isPlottable"] = l.IsPlottable,
                    ["transparencyAlpha"] = Safe(() => (int)l.Transparency.Alpha),
                    ["description"] = Safe(() => l.Description),
                });
            }
            return list;
        }

        private static List<object> DumpLinetypes(Database db, Transaction tr)
        {
            var list = new List<object>();
            var tbl = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            foreach (ObjectId id in tbl)
            {
                var r = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = r.Name,
                    ["description"] = r.Comments,
                    ["patternLength"] = r.PatternLength,
                    ["numDashes"] = r.NumDashes,
                });
            }
            return list;
        }

        private static List<object> DumpTextStyles(Database db, Transaction tr)
        {
            var list = new List<object>();
            var tbl = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in tbl)
            {
                var r = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = r.Name,
                    ["fontFile"] = Safe(() => r.FileName),
                    ["bigFontFile"] = Safe(() => r.BigFontFileName),
                    ["height"] = r.TextSize,
                    ["widthFactor"] = r.XScale,
                    ["obliqueAngle"] = r.ObliquingAngle,
                });
            }
            return list;
        }

        private static List<object> DumpDimStyles(Database db, Transaction tr)
        {
            var list = new List<object>();
            var tbl = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in tbl)
            {
                var r = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = r.Name,
                    ["dimscale"] = r.Dimscale,
                    ["dimtxt"] = r.Dimtxt,
                    ["dimasz"] = r.Dimasz,
                    ["dimexe"] = r.Dimexe,
                    ["dimexo"] = r.Dimexo,
                });
            }
            return list;
        }

        private static List<object> DumpLayouts(Database db, Transaction tr)
        {
            var list = new List<object>();
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = lay.LayoutName,
                    ["tabOrder"] = lay.TabOrder,
                    ["isModel"] = lay.ModelType,
                    ["plotSettingsName"] = Safe(() => lay.PlotSettingsName),
                    ["canonicalMediaName"] = Safe(() => lay.CanonicalMediaName),
                });
            }
            return list;
        }

        // ---------- blocks / spaces ----------

        private static Dictionary<string, object> DumpXref(BlockTableRecord btr) => new()
        {
            ["name"] = btr.Name,
            ["path"] = Safe(() => btr.PathName),
            ["isResolved"] = Safe(() => btr.IsResolved),
            ["isUnloaded"] = Safe(() => btr.IsUnloaded),
        };

        private static Dictionary<string, object> DumpBlockDefinition(BlockTableRecord btr, Transaction tr)
        {
            var typeCounts = new Dictionary<string, int>();
            var attDefs = new List<object>();
            foreach (ObjectId id in btr)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                Bump(typeCounts, ent.GetType().Name);
                if (ent is AttributeDefinition ad)
                {
                    attDefs.Add(new Dictionary<string, object>
                    {
                        ["tag"] = ad.Tag,
                        ["prompt"] = ad.Prompt,
                        ["default"] = ad.TextString,
                        ["constant"] = ad.Constant,
                    });
                }
            }
            return new Dictionary<string, object>
            {
                ["name"] = btr.Name,
                ["origin"] = P(btr.Origin),
                ["hasAttributes"] = btr.HasAttributeDefinitions,
                ["entityCounts"] = typeCounts,
                ["attributeDefinitions"] = attDefs,
            };
        }

        private static Dictionary<string, object> DumpSpace(BlockTableRecord btr, Transaction tr, Dictionary<string, int> globalCounts)
        {
            var entities = new List<object>();
            foreach (ObjectId id in btr)
            {
                try
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    Bump(globalCounts, ent.GetType().Name);
                    entities.Add(DumpEntity(ent, tr));
                }
                catch { /* skip unreadable entity */ }
            }
            return new Dictionary<string, object>
            {
                ["name"] = btr.Name,
                ["entityCount"] = entities.Count,
                ["entities"] = entities,
            };
        }

        // ---------- per-entity ----------

        private static Dictionary<string, object> DumpEntity(Entity ent, Transaction tr)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = ent.GetType().Name,
                ["handle"] = ent.Handle.ToString(),
                ["layer"] = ent.Layer,
                ["color"] = ColorString(ent.Color),
                ["linetype"] = ent.Linetype,
                ["lineWeight"] = ent.LineWeight.ToString(),
            };

            try { var ext = ent.GeometricExtents; d["bbox"] = new { min = P(ext.MinPoint), max = P(ext.MaxPoint) }; }
            catch { /* some entities have no extents */ }

            switch (ent)
            {
                case Line line:
                    d["start"] = P(line.StartPoint); d["end"] = P(line.EndPoint);
                    d["length"] = line.Length;
                    break;
                case Circle c:
                    d["center"] = P(c.Center); d["radius"] = c.Radius;
                    break;
                case Arc a:
                    d["center"] = P(a.Center); d["radius"] = a.Radius;
                    d["startAngle"] = a.StartAngle; d["endAngle"] = a.EndAngle;
                    break;
                case Ellipse el:
                    d["center"] = P(el.Center); d["majorRadius"] = el.MajorRadius; d["minorRadius"] = el.MinorRadius;
                    break;
                case Polyline pl:
                    d["closed"] = pl.Closed; d["length"] = Safe(() => pl.Length);
                    d["vertices"] = Enumerable.Range(0, pl.NumberOfVertices)
                        .Select(i => P2(pl.GetPoint2dAt(i))).ToList();
                    break;
                case Polyline2d p2:
                    d["closed"] = p2.Closed;
                    d["vertices"] = VertsFromSeq(p2, tr);
                    break;
                case Polyline3d p3:
                    d["closed"] = p3.Closed;
                    d["vertices"] = VertsFrom3d(p3, tr);
                    break;
                case DBText t:
                    d["text"] = t.TextString; d["position"] = P(t.Position); d["height"] = t.Height;
                    d["rotation"] = t.Rotation; d["style"] = Safe(() => SymbolName(tr, t.TextStyleId));
                    break;
                case MText m:
                    d["text"] = m.Contents; d["position"] = P(m.Location);
                    d["height"] = m.TextHeight; d["width"] = m.Width; d["rotation"] = m.Rotation;
                    break;
                case Table tb: // NOTE: Table derives from BlockReference, so it must be matched first
                    d["numRows"] = tb.Rows.Count; d["numCols"] = tb.Columns.Count;
                    var grid = new List<object>();
                    for (int r = 0; r < tb.Rows.Count; r++)
                    {
                        var row = new List<object>();
                        for (int col = 0; col < tb.Columns.Count; col++)
                        {
                            string cell = null;
                            try { cell = tb.Cells[r, col].TextString; } catch { }
                            row.Add(cell);
                        }
                        grid.Add(row);
                    }
                    d["cells"] = grid;
                    break;
                case BlockReference br:
                    // For dynamic/anonymous blocks the real (authoring) name lives on the
                    // dynamic block table record; BlockTableRecord points at an anonymous *U.. def.
                    ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    d["blockName"] = Safe(() => SymbolName(tr, defId));
                    d["rawBlockName"] = Safe(() => SymbolName(tr, br.BlockTableRecord));
                    d["position"] = P(br.Position); d["rotation"] = br.Rotation;
                    d["scale"] = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z };
                    d["isDynamic"] = br.IsDynamicBlock;
                    d["attributes"] = DumpAttributes(br, tr);
                    if (br.IsDynamicBlock)
                    {
                        var props = new List<object>();
                        foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                            props.Add(new Dictionary<string, object> { ["name"] = p.PropertyName, ["value"] = Convert.ToString(p.Value) });
                        d["dynamicProperties"] = props;
                    }
                    break;
                case Dimension dim:
                    d["measurement"] = Safe(() => dim.Measurement);
                    d["dimText"] = Safe(() => dim.DimensionText);
                    d["dimStyle"] = Safe(() => SymbolName(tr, dim.DimensionStyle));
                    break;
                case Hatch h:
                    d["patternName"] = Safe(() => h.PatternName); d["area"] = Safe(() => h.Area);
                    d["isSolid"] = Safe(() => h.IsSolidFill);
                    break;
                case Spline sp:
                    d["degree"] = sp.Degree; d["closed"] = Safe(() => sp.Closed);
                    d["numControlPoints"] = Safe(() => sp.NumControlPoints);
                    break;
                case MLeader ml:
                    try { d["text"] = ml.MText?.Contents; } catch { }
                    break;
            }

            return d;
        }

        private static List<object> DumpAttributes(BlockReference br, Transaction tr)
        {
            var list = new List<object>();
            foreach (ObjectId attId in br.AttributeCollection)
            {
                var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                if (att == null) continue;
                list.Add(new Dictionary<string, object> { ["tag"] = att.Tag, ["value"] = att.TextString });
            }
            return list;
        }

        private static List<object> VertsFromSeq(Polyline2d p2, Transaction tr)
        {
            var v = new List<object>();
            foreach (ObjectId id in p2)
            {
                var vx = tr.GetObject(id, OpenMode.ForRead) as Vertex2d;
                if (vx != null) v.Add(P(vx.Position));
            }
            return v;
        }

        private static List<object> VertsFrom3d(Polyline3d p3, Transaction tr)
        {
            var v = new List<object>();
            foreach (ObjectId id in p3)
            {
                var vx = tr.GetObject(id, OpenMode.ForRead) as PolylineVertex3d;
                if (vx != null) v.Add(P(vx.Position));
            }
            return v;
        }

        // ---------- helpers ----------

        private static double[] P(Point3d p) => new[] { p.X, p.Y, p.Z };
        private static double[] P(object o) => o is Point3d p ? new[] { p.X, p.Y, p.Z } : null;
        private static double[] P2(Point2d p) => new[] { p.X, p.Y };
        private static double[] P2(object o) => o is Point2d p ? new[] { p.X, p.Y } : null;

        private static void Bump(Dictionary<string, int> d, string k)
        {
            d.TryGetValue(k, out int n);
            d[k] = n + 1;
        }

        private static string ColorString(Color c)
        {
            try
            {
                if (c == null) return null;
                if (c.IsByAci) return c.ColorIndex.ToString();
                return $"RGB({c.Red},{c.Green},{c.Blue})";
            }
            catch { return null; }
        }

        private static string SymbolName(Transaction tr, ObjectId id)
        {
            if (id.IsNull) return null;
            var rec = tr.GetObject(id, OpenMode.ForRead) as SymbolTableRecord;
            return rec?.Name;
        }

        private static string LayerName(Database db, ObjectId id)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var n = SymbolName(tr, id);
                tr.Commit();
                return n;
            }
        }

        private static object Safe(Func<object> f)
        {
            try { return f(); } catch { return null; }
        }

        private static string OutputPath(Database db)
        {
            string dir, name;
            string fn = db.Filename;
            if (!string.IsNullOrEmpty(fn) && File.Exists(fn))
            {
                dir = Path.GetDirectoryName(fn);
                name = Path.GetFileNameWithoutExtension(fn);
            }
            else
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                name = string.IsNullOrEmpty(fn) ? "drawing" : Path.GetFileNameWithoutExtension(fn);
            }
            return Path.Combine(dir, name + "_dump.json");
        }
    }
}
