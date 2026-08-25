// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoGAD
{
    /// <summary>
    /// The chat UI hosted inside the AUTOGAD palette. WhatsApp-style transcript (user right, assistant
    /// left, system notes centred) rendered as HTML in a WebBrowser control, with a toolbar for the
    /// key / settings / memory / reset actions and an Auto-approve toggle.
    ///
    /// Every control gets an explicit colour: the palette host applies AutoCAD's own theme to
    /// anything left at the defaults, which turned the bars light in the dark theme.
    /// </summary>
    public class ChatControl : UserControl
    {
        // AutoCAD dark-theme palette
        private static readonly Color BarBg = Color.FromArgb(43, 43, 43);
        private static readonly Color PanelBg = Color.FromArgb(30, 30, 30);
        private static readonly Color InputBg = Color.FromArgb(51, 51, 51);
        private static readonly Color Border = Color.FromArgb(85, 85, 85);
        private static readonly Color BtnBg = Color.FromArgb(62, 62, 66);
        private static readonly Color BtnHover = Color.FromArgb(82, 82, 88);
        private static readonly Color Accent = Color.FromArgb(0, 122, 204);
        private static readonly Color AccentHover = Color.FromArgb(28, 142, 224);
        private static readonly Color Fg = Color.FromArgb(230, 230, 230);
        private static readonly Color Muted = Color.FromArgb(160, 160, 160);

        private const string UserBg = "#1f5f4a", BotBg = "#3a3f46", SysFg = "#8a9099";

        private const string Skeleton =
            "<!DOCTYPE html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta charset=\"utf-8\">" +
            "<style>" +
            "body{background:#1e1e1e;color:#e6e6e6;font-family:'Segoe UI',Tahoma,sans-serif;font-size:10pt;margin:6px;word-wrap:break-word;}" +
            "p{margin:4px 0;} h2,h3,h4{margin:8px 0 4px;font-size:11pt;} ul,ol{margin:4px 0 4px 22px;padding:0;} li{margin:2px 0;}" +
            "pre{background:#111;color:#dcdcdc;padding:6px;overflow:auto;font-family:Consolas,monospace;font-size:9pt;white-space:pre-wrap;margin:4px 0;}" +
            "code{font-family:Consolas,monospace;background:#111;padding:0 3px;}" +
            "table.md{border-collapse:collapse;margin:4px 0;} table.md td,table.md th{border:1px solid #666;padding:2px 6px;vertical-align:top;} table.md th{background:#2b2f35;}" +
            "blockquote{border-left:3px solid #666;margin:4px 0;padding:2px 8px;color:#c9ced6;}" +
            "a{color:#6cb6ff;} hr{border:0;border-top:1px solid #555;margin:6px 0;}" +
            ".who{font-size:8pt;color:#c9ced6;} .sys{color:" + SysFg + ";font-size:8.5pt;}" +
            "</style></head><body><div id=\"chat\"></div></body></html>";

        private readonly WebBrowser _web;
        private readonly TextBox _input;
        private readonly Button _send;
        private readonly Label _docLabel;
        private readonly Label _status;
        private readonly CheckBox _auto;
        private readonly Button _effortBtn;
        private readonly ContextMenuStrip _effortMenu;

        private Agent _agent;
        private bool _busy, _docReady, _syncing;
        private readonly List<(string role, string text)> _transcript = new List<(string, string)>();
        private string _current;          // assistant text being streamed, null when idle
        private History _history;         // transcript store for the drawing the panel currently shows

        public Agent AgentOrNull => _agent;

        public ChatControl()
        {
            BackColor = PanelBg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9f);
            History.Cleanup();

            // ---- top bar: drawing name, auto-approve, effort, Key / Settings / Memory / Reset (wraps when narrow)
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(6, 4, 4, 2),
                BackColor = BarBg
            };
            _docLabel = new Label
            {
                AutoSize = true,
                ForeColor = Fg,
                BackColor = BarBg,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Margin = new Padding(0, 6, 12, 4)
            };
            _auto = new CheckBox
            {
                Text = "Auto-approve",
                AutoSize = true,
                ForeColor = Fg,
                BackColor = BarBg,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 4, 8, 2)
            };
            _auto.FlatAppearance.BorderSize = 0;
            _auto.FlatAppearance.CheckedBackColor = BarBg;
            _auto.FlatAppearance.MouseOverBackColor = BarBg;
            _auto.FlatAppearance.MouseDownBackColor = BarBg;
            Tip(_auto, "Skip confirmation dialogs for write tools (saved in Settings)");
            _auto.CheckedChanged += OnAutoToggled;

            _effortBtn = ToolButton("Effort: high ▾", "Effort: low = cheap & fast … max = deepest reasoning");
            _effortMenu = new ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                BackColor = BarBg,
                ForeColor = Fg,
                ShowImageMargin = false,
                ShowCheckMargin = true
            };
            foreach (var level in Config.Efforts)
            {
                var item = new ToolStripMenuItem(level) { ForeColor = Fg, BackColor = BarBg };
                var lvl = level;
                item.Click += (s, e) => OnEffortPicked(lvl);
                _effortMenu.Items.Add(item);
            }
            _effortBtn.Click += (s, e) => _effortMenu.Show(_effortBtn, new Point(0, _effortBtn.Height));

            bar.Controls.Add(_docLabel);
            bar.Controls.Add(_auto);
            bar.Controls.Add(_effortBtn);
            foreach (var (label, tip, fn) in new (string, string, Action)[]
            {
                ("Key", "Set / change the Anthropic API key", OnKey),
                ("Settings", "Model, effort, tokens, auto-approve", OnSettings),
                ("Memory", "View / edit what AutoGAD remembers", OnMemory),
                ("Reset", "Start a new conversation for this drawing", OnReset),
            })
            {
                var b = ToolButton(label, tip);
                var f = fn;
                b.Click += (s, e) => f();
                bar.Controls.Add(b);
            }

            // ---- transcript
            _web = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                IsWebBrowserContextMenuEnabled = false,
                WebBrowserShortcutsEnabled = false,
                AllowWebBrowserDrop = false
            };
            _web.DocumentCompleted += (s, e) => { _docReady = true; Render(); };
            _web.Navigating += (s, e) =>
            {
                if (e.Url == null || !e.Url.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Url.ToString()) { UseShellExecute = true }); } catch { }
            };
            _web.DocumentText = Skeleton;

            // ---- input (1 px border drawn by the wrapping panel)
            _input = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                BackColor = InputBg,
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10f),
                PlaceholderText = "Ask about the drawing or tell AutoGAD what to change…  (Enter to send, Shift+Enter = newline)"
            };
            _input.KeyDown += OnInputKeyDown;
            var inputBorder = new Panel { Dock = DockStyle.Fill, BackColor = Border, Padding = new Padding(1) };
            var inputInner = new Panel { Dock = DockStyle.Fill, BackColor = InputBg, Padding = new Padding(4, 3, 4, 3) };
            inputInner.Controls.Add(_input);
            inputBorder.Controls.Add(inputInner);
            var inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(6, 4, 6, 2), BackColor = BarBg };
            inputPanel.Controls.Add(inputBorder);
            _input.GotFocus += (s, e) => inputBorder.BackColor = Accent;
            _input.LostFocus += (s, e) => inputBorder.BackColor = Border;

            // ---- status + Send
            _status = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Muted, BackColor = BarBg,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
            _send = new Button
            {
                Text = "Send", Dock = DockStyle.Right, Width = 72, FlatStyle = FlatStyle.Flat,
                BackColor = Accent, ForeColor = Color.White, UseVisualStyleBackColor = false,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _send.FlatAppearance.BorderSize = 0;
            _send.FlatAppearance.MouseOverBackColor = AccentHover;
            _send.Click += async (s, e) => await Run();
            var sendRow = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6, 3, 6, 6), BackColor = BarBg };
            sendRow.Controls.Add(_status);
            sendRow.Controls.Add(_send);

            Controls.Add(_web);
            Controls.Add(bar);
            Controls.Add(inputPanel);
            Controls.Add(sendRow);

            try
            {
                AcApp.DocumentManager.DocumentActivated += (s, e) => OnDocumentChanged();
                AcApp.DocumentManager.DocumentToBeDestroyed += (s, e) => { try { _agent?.Reset(e.Document); } catch { } };
            }
            catch { }

            SyncFromConfig(Config.Load());
            RefreshDocLabel();
            LoadHistory();
            ShowWelcome();
        }

        // ---------------------------------------------------------------- styling helpers

        private static void Tip(Control c, string text) => new ToolTip().SetToolTip(c, text);

        private static Button ToolButton(string text, string tip)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = BtnBg,
                ForeColor = Fg,
                UseVisualStyleBackColor = false,
                Margin = new Padding(0, 2, 4, 2),
                Padding = new Padding(6, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = BtnHover;
            b.FlatAppearance.MouseDownBackColor = BtnHover;
            Tip(b, tip);
            return b;
        }

        /// <summary>Dark colours for the effort drop-down (the stock renderer paints it light).</summary>
        private class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColorTable()) { }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // Draw the check mark in the foreground colour instead of the stock black-on-blue box.
                var r = e.ImageRectangle;
                using (var pen = new Pen(Fg, 2f))
                {
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new Point(r.Left + 3, r.Top + r.Height / 2),
                        new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4),
                        new Point(r.Right - 3, r.Top + 3)
                    });
                }
            }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => BtnHover;
            public override Color MenuItemSelected => BtnHover;
            public override Color MenuItemSelectedGradientBegin => BtnHover;
            public override Color MenuItemSelectedGradientEnd => BtnHover;
            public override Color MenuItemPressedGradientBegin => BtnHover;
            public override Color MenuItemPressedGradientEnd => BtnHover;
            public override Color ToolStripDropDownBackground => BarBg;
            public override Color ImageMarginGradientBegin => BarBg;
            public override Color ImageMarginGradientMiddle => BarBg;
            public override Color ImageMarginGradientEnd => BarBg;
            public override Color CheckBackground => BarBg;
            public override Color CheckSelectedBackground => BtnHover;
            public override Color CheckPressedBackground => BtnHover;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
        }

        // ---------------------------------------------------------------- helpers

        private void SyncFromConfig(Config cfg)
        {
            _syncing = true;
            try
            {
                _auto.Checked = cfg.AutoApprove;
                _effortBtn.Text = "Effort: " + cfg.Effort + " ▾";
                foreach (ToolStripMenuItem item in _effortMenu.Items) item.Checked = item.Text == cfg.Effort;
                Approval.Configured = cfg.AutoApprove;
            }
            finally { _syncing = false; }
        }

        /// <summary>Re-read config.json (after Settings / AUTOGADKEY) and push it into the running agent.</summary>
        public void ReloadSettings()
        {
            var cfg = Config.Load();
            _agent?.SetConfig(cfg);
            SyncFromConfig(cfg);
        }

        private void RefreshDocLabel()
        {
            var d = AcApp.DocumentManager.MdiActiveDocument;
            _docLabel.Text = d == null ? "no drawing open" : System.IO.Path.GetFileName(d.Name);
        }

        public void ShowWelcome()
        {
            var cfg = _agent?.Cfg ?? Config.Load();
            if (!cfg.HasApiKey)
                AppendSystem("No " + cfg.ProviderLabel + " API key set. Click **Key** to enter one.");
            else
                AppendSystem("AutoGAD " + Config.Version + " · " + cfg.ProviderLabel + " · " + cfg.Model + " · effort " + cfg.Effort +
                             ". Ask about the active drawing or tell me what to change.");
        }

        public void AppendSystem(string text)
        {
            _transcript.Add(("system", text));
            Render();
        }

        /// <summary>Called when the active drawing changes: relabel and switch to that drawing's transcript.</summary>
        public void OnDocumentChanged()
        {
            RefreshDocLabel();
            LoadHistory();
        }

        /// <summary>Show the stored transcript of the active drawing (on open / document switch).</summary>
        private void LoadHistory(bool force = false)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            History h;
            try { h = new History(doc); } catch { return; }
            if (_history != null && _history.Key == h.Key && !force) return;
            _history = h;
            _transcript.Clear();
            foreach (var e in h.Tail()) _transcript.Add((e.Role, e.Text));
            if (_transcript.Count > 0)
                _transcript.Add(("system", "— earlier conversation restored; " + h.Entries.Count + " lines kept in " + h.Path_ + " —"));
            Render();
        }

        public void ClearHistory()
        {
            _history?.Clear();
            _transcript.Clear();
            _current = null;
            Render();
        }

        // ---------------------------------------------------------------- rendering

        private static string Bubble(string role, string text)
        {
            if (role == "system")
                return "<table width=\"100%\" cellpadding=\"2\"><tr><td align=\"center\"><span class=\"sys\">" +
                       Markdown.Inline(text) + "</span></td></tr></table>";

            string body = role == "assistant" ? Markdown.ToHtml(text)
                                              : WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>");
            bool user = role == "user";
            string bg = user ? UserBg : BotBg, who = user ? "You" : "AutoGAD";
            string bubble =
                "<table width=\"100%\" cellpadding=\"8\" cellspacing=\"0\" bgcolor=\"" + bg + "\" style=\"color:#f2f2f2;border-radius:8px;\">" +
                "<tr><td><span class=\"who\"><b>" + who + "</b></span></td></tr>" +
                "<tr><td>" + body + "</td></tr></table>";
            const string spacer = "<td width=\"18%\"></td>";
            string cells = user ? spacer + "<td>" + bubble + "</td>" : "<td>" + bubble + "</td>" + spacer;
            return "<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>" + cells + "</tr></table><p></p>";
        }

        private void Render()
        {
            if (InvokeRequired) { BeginInvoke(new Action(Render)); return; }
            if (!_docReady) return;
            try
            {
                var el = _web.Document?.GetElementById("chat");
                if (el == null) return;
                var sb = new StringBuilder();
                foreach (var (role, text) in _transcript) sb.Append(Bubble(role, text));
                if (_current != null) sb.Append(Bubble("assistant", _current.Length == 0 ? "…" : _current));
                el.InnerHtml = sb.ToString();
                var body = _web.Document.Body;
                if (body != null) _web.Document.Window?.ScrollTo(0, body.ScrollRectangle.Height);
            }
            catch { /* the WebBrowser can be torn down while AutoCAD closes */ }
        }

        private void SetStatus(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), s); return; }
            _status.Text = s ?? "";
        }

        private void OnText(string t)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnText), t); return; }
            if (_current == null) _current = "";
            _current += t;
            Render();
        }

        // ---------------------------------------------------------------- actions

        private async void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await Run();
            }
        }

        private void OnAutoToggled(object sender, EventArgs e)
        {
            if (_syncing) return;
            try { Config.SaveSettings(autoApprove: _auto.Checked); } catch { }
            ReloadSettings();
        }

        private void OnEffortPicked(string level)
        {
            try { Config.SaveSettings(effort: level); } catch { }
            ReloadSettings();
        }

        private void OnKey()
        {
            if (ApiKeyForm.Prompt()) ReloadSettings();
            ShowWelcome();
        }

        private void OnSettings()
        {
            if (SettingsForm.Prompt())
            {
                ReloadSettings();
                var cfg = _agent?.Cfg ?? Config.Load();
                AppendSystem("Settings saved: " + cfg.ProviderLabel + " · " + cfg.Model + " · effort " + cfg.Effort + " · max " + cfg.MaxTokens.ToString("N0") + " tokens" +
                             (cfg.AutoApprove ? " · auto-approve on" : ""));
            }
        }

        private void OnMemory()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var (user, drawing) = _agent != null ? _agent.Memories(doc)
                                                 : (Memory.LoadUser(), doc?.Database != null ? Memory.LoadDrawing(doc.Database, doc.Name) : null);
            if (MemoryForm.Prompt(user, drawing, ClearHistory) && doc != null)
                _agent?.Reset(doc);   // next turn rebuilds the system prompt from the edited stores
        }

        private void OnReset() => ResetConversation();

        /// <summary>Forget the current chat and re-read the drawing on the next question (saved history is kept).</summary>
        public void ResetConversation()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null) _agent?.Reset(doc);
            _transcript.Clear();
            _current = null;
            RefreshDocLabel();
            AppendSystem("Conversation reset; the drawing snapshot will be re-read on the next question. " +
                         "(Saved history is kept - clear it from Memory if needed.)");
        }

        /// <summary>Drop the cached agent so the next question picks up new settings (e.g. a changed API key).</summary>
        public void ResetAgent()
        {
            _agent = null;
            AppendSystem("Settings reloaded — the next question uses the new configuration.");
        }

        private async Task Run()
        {
            if (_busy) return;
            string prompt = _input.Text.Trim();
            if (prompt.Length == 0) return;

            var cfg = Config.Load();
            if (!cfg.HasApiKey)
            {
                AppendSystem("No API key set — opening the key dialog…");
                if (!ApiKeyForm.Prompt())
                {
                    AppendSystem("An Anthropic API key is required. Click **Key** (or run AUTOGADKEY) when you have one.");
                    return;
                }
                cfg = Config.Load();
                if (!cfg.HasApiKey) { AppendSystem("Still no key — aborting."); return; }
                AppendSystem("Key saved to " + Config.FilePath);
            }
            if (_agent == null) _agent = new Agent(cfg); else _agent.SetConfig(cfg);
            SyncFromConfig(cfg);

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { AppendSystem("No active drawing — open one first."); return; }

            _busy = true; _send.Enabled = false; _input.ReadOnly = true;
            _input.Clear();
            RefreshDocLabel();
            LoadHistory();                     // switch transcript if the active drawing changed
            _transcript.Add(("user", prompt));
            _history?.Append("user", prompt);
            _current = "";
            Render();
            SetStatus("Thinking…");

            try
            {
                await _agent.AskAsync(doc, prompt, OnText, SetStatus);
            }
            catch (Exception ex)
            {
                _current = (_current ?? "") + "\n\n**Error:** " + ex.Message;
            }
            finally
            {
                if (_current != null)
                {
                    _transcript.Add(("assistant", _current));
                    _history?.Append("assistant", _current);
                    _current = null;
                }
                Render();
                SetStatus("");
                _busy = false; _send.Enabled = true; _input.ReadOnly = false;
                _input.Focus();
            }
        }
    }
}
