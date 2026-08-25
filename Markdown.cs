// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoGAD
{
    /// <summary>
    /// Small Markdown → HTML converter for the chat bubbles (headings, bold/italic, inline code, fenced
    /// code blocks, lists, tables, links, blockquotes). Good enough for Claude's answers; anything it
    /// doesn't understand is shown as escaped text, never dropped.
    /// </summary>
    internal static class Markdown
    {
        private static readonly Regex Heading = new Regex(@"^(#{1,6})\s+(.*)$");
        private static readonly Regex Bullet = new Regex(@"^\s*[-*+]\s+(.*)$");
        private static readonly Regex Numbered = new Regex(@"^\s*\d+[.)]\s+(.*)$");
        private static readonly Regex TableSep = new Regex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");
        private static readonly Regex Rule = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$");

        public static string ToHtml(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder();
            var para = new List<string>();
            string list = null;          // "ul" | "ol" | null
            bool inCode = false, inTable = false, tableHeaderDone = false;

            void FlushPara()
            {
                if (para.Count == 0) return;
                sb.Append("<p>").Append(string.Join("<br>", para)).Append("</p>");
                para.Clear();
            }
            void CloseList() { if (list != null) { sb.Append("</").Append(list).Append('>'); list = null; } }
            void CloseTable() { if (inTable) { sb.Append("</table>"); inTable = false; tableHeaderDone = false; } }
            void CloseBlocks() { FlushPara(); CloseList(); CloseTable(); }

            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (inCode) { sb.Append("</code></pre>"); inCode = false; }
                    else { CloseBlocks(); sb.Append("<pre><code>"); inCode = true; }
                    continue;
                }
                if (inCode) { sb.Append(WebUtility.HtmlEncode(raw)).Append('\n'); continue; }

                if (line.Length == 0) { CloseBlocks(); continue; }

                Match m;
                if ((m = Heading.Match(line)).Success)
                {
                    CloseBlocks();
                    int lvl = Math.Min(4, m.Groups[1].Value.Length + 1);   // # -> h2 ... keeps sizes sane in a narrow palette
                    sb.Append("<h").Append(lvl).Append('>').Append(Inline(m.Groups[2].Value)).Append("</h").Append(lvl).Append('>');
                    continue;
                }
                if (Rule.IsMatch(line)) { CloseBlocks(); sb.Append("<hr>"); continue; }

                if (line.StartsWith("|", StringComparison.Ordinal) && line.IndexOf('|', 1) > 0)
                {
                    if (TableSep.IsMatch(line)) { tableHeaderDone = true; continue; }
                    if (!inTable) { FlushPara(); CloseList(); sb.Append("<table class=\"md\">"); inTable = true; tableHeaderDone = false; }
                    string cellTag = tableHeaderDone ? "td" : "th";
                    var cells = line.Trim().Trim('|').Split('|');
                    sb.Append("<tr>");
                    foreach (var c in cells) sb.Append('<').Append(cellTag).Append('>').Append(Inline(c.Trim())).Append("</").Append(cellTag).Append('>');
                    sb.Append("</tr>");
                    if (!tableHeaderDone) tableHeaderDone = true;   // a table without a separator row: only the first row is a header
                    continue;
                }
                CloseTable();

                if ((m = Bullet.Match(line)).Success || (m = Numbered.Match(line)).Success)
                {
                    string kind = Bullet.IsMatch(line) ? "ul" : "ol";
                    FlushPara();
                    if (list != kind) { CloseList(); sb.Append('<').Append(kind).Append('>'); list = kind; }
                    sb.Append("<li>").Append(Inline(m.Groups[1].Value)).Append("</li>");
                    continue;
                }
                if (list != null && (raw.StartsWith("  ") || raw.StartsWith("\t")))
                {
                    // continuation line of the previous list item
                    sb.Append("<br>").Append(Inline(line.Trim()));
                    continue;
                }
                CloseList();

                if (line.StartsWith(">", StringComparison.Ordinal))
                {
                    FlushPara();
                    sb.Append("<blockquote>").Append(Inline(line.Substring(1).Trim())).Append("</blockquote>");
                    continue;
                }

                para.Add(Inline(line));
            }
            if (inCode) sb.Append("</code></pre>");
            CloseBlocks();
            return sb.ToString();
        }

        private static readonly Regex CodeSpan = new Regex("`([^`]+)`");
        private static readonly Regex Bold = new Regex(@"\*\*(.+?)\*\*|__(.+?)__");
        private static readonly Regex Italic = new Regex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?!\w)|(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)");
        private static readonly Regex Link = new Regex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)");
        private static readonly Regex BareUrl = new Regex(@"(?<![""'>=\]])\bhttps?://[^\s<]+[^\s<.,;:!?)]");

        /// <summary>Inline formatting on one line. Escapes HTML first, so raw tags from the model never render.</summary>
        public static string Inline(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Protect code spans from the other substitutions.
            var codes = new List<string>();
            s = CodeSpan.Replace(s, m => { codes.Add("<code>" + WebUtility.HtmlEncode(m.Groups[1].Value) + "</code>"); return "" + (codes.Count - 1) + ""; });
            s = WebUtility.HtmlEncode(s);
            s = Link.Replace(s, m => "<a href=\"" + m.Groups[2].Value + "\">" + m.Groups[1].Value + "</a>");
            s = BareUrl.Replace(s, m => "<a href=\"" + m.Value + "\">" + m.Value + "</a>");
            s = Bold.Replace(s, m => "<b>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</b>");
            s = Italic.Replace(s, m => "<i>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</i>");
            s = Regex.Replace(s, "(\\d+)", m => codes[int.Parse(m.Groups[1].Value)]);
            return s;
        }
    }
}
