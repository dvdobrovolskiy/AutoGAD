// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Collections.Generic;
using System.IO;

using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoGAD
{
    /// <summary>
    /// The AutoGAD menu: a ribbon tab (visible in every default workspace) plus a classic pull-down
    /// menu (shows when MENUBAR is 1). Both just fire the AUTOGAD* commands, so they stay in sync
    /// with the command table automatically.
    /// </summary>
    internal static class RibbonUi
    {
        private const string TabId = "AUTOGAD_TAB";

        private static readonly (string label, string command, string tip, string glyph, System.Drawing.Color color)[] Items =
        {
            ("Chat",     "AUTOGAD",      "Open the AutoGAD chat palette (Claude inside AutoCAD)", "A", System.Drawing.Color.FromArgb(0, 122, 204)),
            ("API key",  "AUTOGADKEY",   "Set or change the Anthropic API key",                  "K", System.Drawing.Color.FromArgb(200, 140, 40)),
            ("Settings", "AUTOGADSET",   "Model, effort, max tokens, auto-approve",              "S", System.Drawing.Color.FromArgb(90, 90, 96)),
            ("Memory",   "AUTOGADMEM",   "View and edit what AutoGAD remembers",                 "M", System.Drawing.Color.FromArgb(120, 80, 190)),
            ("Reset",    "AUTOGADRESET", "Forget the current chat and re-read the drawing",      "R", System.Drawing.Color.FromArgb(180, 60, 60)),
        };

        private static bool _installed;

        /// <summary>Create the ribbon tab now if the ribbon exists, otherwise as soon as it appears; re-add it after workspace switches.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            if (ComponentManager.Ribbon == null) ComponentManager.ItemInitialized += OnItemInitialized;
            else Build();

            try
            {
                AcApp.SystemVariableChanged += (s, e) =>
                {
                    if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
                        AcApp.Idle += RebuildOnce;
                };
            }
            catch { }
        }

        private static void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            Build();
        }

        private static void RebuildOnce(object sender, EventArgs e)
        {
            AcApp.Idle -= RebuildOnce;
            Build();
        }

        private static void Build()
        {
            try
            {
                var ribbon = ComponentManager.Ribbon;
                if (ribbon == null || ribbon.FindTab(TabId) != null) return;

                var tab = new RibbonTab { Title = "AutoGAD", Id = TabId };
                var source = new RibbonPanelSource { Title = "AutoGAD" };
                foreach (var it in Items)
                {
                    source.Items.Add(new RibbonButton
                    {
                        Text = it.label,
                        Id = "AUTOGAD_BTN_" + it.command,
                        ShowText = true,
                        ShowImage = true,
                        Size = RibbonItemSize.Large,
                        Orientation = System.Windows.Controls.Orientation.Vertical,
                        LargeImage = Icon(it.glyph, it.color, 32),
                        Image = Icon(it.glyph, it.color, 16),
                        ToolTip = it.tip,
                        CommandHandler = new CommandHandler(it.command)
                    });
                }
                tab.Panels.Add(new RibbonPanel { Source = source });
                ribbon.Tabs.Add(tab);
            }
            catch { /* the menu is a convenience; the commands work without it */ }
        }

        /// <summary>Sends the command to the active document, exactly as if typed on the command line.</summary>
        private class CommandHandler : System.Windows.Input.ICommand
        {
            private readonly string _command;
            public CommandHandler(string command) { _command = command; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                doc.SendStringToExecute("_" + _command + " ", true, false, false);
            }
        }

        /// <summary>Simple generated icon: coloured disc with a letter (no image resources to ship).</summary>
        private static System.Windows.Media.ImageSource Icon(string glyph, System.Drawing.Color color, int size)
        {
            try
            {
                using (var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.Clear(System.Drawing.Color.Transparent);
                        using (var b = new System.Drawing.SolidBrush(color))
                            g.FillEllipse(b, 0.5f, 0.5f, size - 1.5f, size - 1.5f);
                        using (var f = new System.Drawing.Font("Segoe UI", size * 0.55f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
                        {
                            var sz = g.MeasureString(glyph, f);
                            g.DrawString(glyph, f, System.Drawing.Brushes.White, (size - sz.Width) / 2f, (size - sz.Height) / 2f);
                        }
                    }
                    var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- classic pull-down menu

        /// <summary>
        /// Add an "AutoGAD" pull-down to the main menu bar through the COM automation API (best effort:
        /// silently skipped if COM is unavailable). Visible when MENUBAR = 1; harmless otherwise.
        /// </summary>
        public static void AddPulldownMenu()
        {
            try
            {
                dynamic acad = AcApp.AcadApplication;
                dynamic groups = acad.MenuGroups;
                if ((int)groups.Count == 0) return;
                dynamic group = groups.Item(0);
                dynamic menus = group.Menus;

                dynamic menu = null;
                int n = (int)menus.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic m = menus.Item(i);
                    string name = ((string)m.Name ?? "").Replace("&", "");
                    if (string.Equals(name, "AutoGAD", StringComparison.OrdinalIgnoreCase)) { menu = m; break; }
                }
                if (menu == null)
                {
                    menu = menus.Add("AutoGAD");
                    foreach (var it in Items)
                        menu.AddMenuItem((int)menu.Count, it.label, "^C^C_" + it.command + " ");
                }
                if (!(bool)menu.OnMenuBar)
                    menu.InsertInMenuBar((int)acad.MenuBar.Count);
            }
            catch { }
        }
    }
}
