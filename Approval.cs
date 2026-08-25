// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Drawing;
using System.Windows.Forms;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoGAD
{
    /// <summary>
    /// Gate in front of every write tool. Auto-approve comes either from the saved setting
    /// (config.json autoApprove, mirrored in <see cref="Configured"/>) or from the "Allow all this
    /// session" button, which lasts until AutoCAD is closed and does not touch the saved setting.
    /// </summary>
    public static class Approval
    {
        /// <summary>The saved Auto-approve setting (synced from Config by the chat control).</summary>
        public static bool Configured;

        /// <summary>Set by "Allow all this session"; never persisted.</summary>
        public static bool Session;

        /// <summary>Ask the user unless auto-approve is on. Must be called on the UI thread.</summary>
        public static bool Confirm(string title, string detail)
        {
            if (Configured || Session) return true;
            using (var f = new ConfirmForm(title, detail))
            {
                DialogResult r;
                try { r = AcApp.ShowModalDialog(f); }   // parents to the AutoCAD main window
                catch { r = f.ShowDialog(); }
                return r == DialogResult.OK;
            }
        }
    }

    /// <summary>"AutoGAD wants to: …" dialog with Allow / Allow all this session / Deny.</summary>
    internal class ConfirmForm : Form
    {
        public ConfirmForm(string title, string detail)
        {
            Text = "AutoGAD wants to: " + title;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 400);
            MinimumSize = new Size(420, 260);
            BackColor = Color.FromArgb(37, 37, 38);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            var intro = new Label
            {
                Text = "Claude wants to apply this change to the drawing. Allow?",
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(10, 8, 10, 0),
                ForeColor = Color.Gainsboro
            };

            var view = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Text = (detail ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f)
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6, 6, 6, 6)
            };

            var deny = Btn("Deny", Color.FromArgb(60, 60, 63));
            deny.DialogResult = DialogResult.Cancel;

            var allowAll = Btn("Allow all this session", Color.FromArgb(60, 60, 63));
            allowAll.Width = 160;
            allowAll.Click += (s, e) => { Approval.Session = true; DialogResult = DialogResult.OK; Close(); };
            new ToolTip().SetToolTip(allowAll,
                "Skip confirmations until AutoCAD is closed (the saved Auto-approve setting is not changed)");

            var allow = Btn("Allow", Color.FromArgb(0, 122, 204));
            allow.DialogResult = DialogResult.OK;

            buttons.Controls.AddRange(new Control[] { deny, allowAll, allow });

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
            body.Controls.Add(view);

            Controls.Add(body);
            Controls.Add(intro);
            Controls.Add(buttons);
            AcceptButton = allow;
            CancelButton = deny;
            view.Select(0, 0);
        }

        private static Button Btn(string text, Color back) => new Button
        {
            Text = text,
            Width = 90,
            Height = 30,
            Margin = new Padding(4, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White
        };
    }
}
