// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Drawing;
using System.Windows.Forms;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoGAD
{
    /// <summary>AUTOGADMEM dialog: what AutoGAD remembers about this drawing and this user; delete / clear.</summary>
    public class MemoryForm : Form
    {
        private readonly ListView _list;
        private readonly Memory _user;
        private readonly Memory _drawing;
        private readonly Action _clearHistory;
        private bool _changed;

        /// <summary>Show the dialog; returns true when notes or history were changed (caller should reset the agent).</summary>
        public static bool Prompt(Memory user, Memory drawing, Action clearHistory)
        {
            using (var f = new MemoryForm(user, drawing, clearHistory))
            {
                try { AcApp.ShowModalDialog(f); }
                catch { f.ShowDialog(); }
                return f._changed;
            }
        }

        public MemoryForm(Memory user, Memory drawing, Action clearHistory)
        {
            _user = user;
            _drawing = drawing;
            _clearHistory = clearHistory;

            Text = "AutoGAD — Memory";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 440);
            MinimumSize = new Size(520, 300);
            BackColor = Color.FromArgb(37, 37, 38);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
                GridLines = false,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.None
            };
            _list.Columns.Add("Scope", 150);
            _list.Columns.Add("Id", 60);
            _list.Columns.Add("Category", 90);
            _list.Columns.Add("Note", 340);
            _list.Columns.Add("Created", 80);
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) OnDelete(); };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(6, 6, 6, 6)
            };
            var del = Btn("Delete selected", 110);
            del.Click += (s, e) => OnDelete();
            var clearUser = Btn("Clear user memory", 130);
            clearUser.Click += (s, e) => OnClear(_user);
            var clearDrawing = Btn("Clear drawing memory", 140);
            clearDrawing.Enabled = _drawing != null;
            clearDrawing.Click += (s, e) => OnClear(_drawing);
            var clearHist = Btn("Clear chat history", 120);
            clearHist.Enabled = _clearHistory != null;
            new ToolTip().SetToolTip(clearHist, "Delete the saved transcript for this drawing (" + History.Dir + ")");
            clearHist.Click += (s, e) => OnClearHistory();
            var close = Btn("Close", 70);
            close.BackColor = Color.FromArgb(0, 122, 204);
            close.Click += (s, e) => Close();
            buttons.Controls.AddRange(new Control[] { del, clearUser, clearDrawing, clearHist, close });

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                ForeColor = Color.Silver,
                Text = "Notes Claude saved with the remember tool. They are injected into every conversation. " +
                       "Stored in " + Memory.Dir + "\r\nYou can also just tell AutoGAD \"remember that …\" or \"forget that …\"."
            };

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
            body.Controls.Add(_list);
            Controls.Add(body);
            Controls.Add(hint);
            Controls.Add(buttons);
            CancelButton = close;

            Refresh_();
        }

        private static Button Btn(string text, int width) => new Button
        {
            Text = text, Width = width, Height = 30, Margin = new Padding(0, 0, 6, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 63), ForeColor = Color.White
        };

        private void Refresh_()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var m in new[] { _drawing, _user })
            {
                if (m == null) continue;
                foreach (var e in m.Entries)
                {
                    var it = new ListViewItem(new[]
                    {
                        m.Scope + " (" + m.Title + ")", e.Id, e.Category ?? "", e.Text,
                        e.Created == default ? "" : e.Created.ToString("yyyy-MM-dd")
                    }) { Tag = m };
                    _list.Items.Add(it);
                }
            }
            if (_list.Items.Count == 0)
                _list.Items.Add(new ListViewItem(new[] { "", "", "", "(nothing remembered yet)", "" }));
            _list.EndUpdate();
        }

        private void OnDelete()
        {
            bool any = false;
            foreach (ListViewItem it in _list.SelectedItems)
            {
                if (!(it.Tag is Memory m)) continue;
                if (m.Remove(it.SubItems[1].Text) != null) any = true;
            }
            if (any) { _changed = true; Refresh_(); }
        }

        private void OnClear(Memory m)
        {
            if (m == null) return;
            if (MessageBox.Show(this, "Clear all " + m.Scope + " memory (" + m.Entries.Count + " note(s))?", "AutoGAD",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            m.Clear();
            _changed = true;
            Refresh_();
        }

        private void OnClearHistory()
        {
            if (MessageBox.Show(this, "Delete the saved chat history for this drawing?", "AutoGAD",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { _clearHistory?.Invoke(); } catch { }
            _changed = true;
        }
    }
}
