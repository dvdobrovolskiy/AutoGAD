// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoGAD
{
    /// <summary>
    /// Modal dialog that collects an API key for one provider (Anthropic or any OpenAI-compatible API),
    /// checks it against that API, and stores it encrypted. Saving also makes that provider active.
    /// </summary>
    public class ApiKeyForm : Form
    {
        private const string AnthropicConsole = "https://console.anthropic.com/settings/keys";
        private const string OpenAiConsole = "https://platform.openai.com/api-keys";
        private const string OpenRouterConsole = "https://openrouter.ai/keys";

        private readonly Config _cfg;
        private readonly RadioButton _anthropic;
        private readonly RadioButton _openai;
        private readonly LinkLabel _link;
        private readonly Label _current;
        private readonly Label _baseUrlLabel;
        private readonly ComboBox _baseUrl;
        private readonly TextBox _key;
        private readonly CheckBox _show;
        private readonly Button _save;
        private readonly Button _remove;
        private readonly Button _cancel;
        private readonly Label _status;

        /// <summary>Show the dialog; returns true when the stored key or provider was changed.</summary>
        public static bool Prompt()
        {
            var cfg = Config.Load();
            using (var f = new ApiKeyForm(cfg))
            {
                DialogResult r;
                try { r = AcApp.ShowModalDialog(f); }   // parents to the AutoCAD main window
                catch { r = f.ShowDialog(); }
                return r == DialogResult.OK;
            }
        }

        private string SelectedProvider => _openai.Checked ? Config.ProviderOpenAi : Config.ProviderAnthropic;

        public ApiKeyForm(Config cfg)
        {
            _cfg = cfg;
            Text = "AutoGAD — API key";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 372);
            BackColor = Color.FromArgb(37, 37, 38);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            var intro = new Label
            {
                Text = "AutoGAD needs your own API key. It is stored encrypted (Windows DPAPI, this user only) in:\r\n" + Config.FilePath,
                Location = new Point(14, 12), Size = new Size(532, 40), ForeColor = Color.Gainsboro
            };

            var provLabel = new Label { Text = "Provider", Location = new Point(14, 62), Size = new Size(70, 20) };
            _anthropic = new RadioButton
            {
                Text = "Anthropic (Claude)", Location = new Point(84, 60), Size = new Size(150, 22),
                ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat, Checked = !cfg.IsOpenAi
            };
            _openai = new RadioButton
            {
                Text = "OpenAI-compatible (OpenAI, OpenRouter, …)", Location = new Point(240, 60), Size = new Size(300, 22),
                ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat, Checked = cfg.IsOpenAi
            };
            _anthropic.CheckedChanged += (s, e) => OnProviderChanged();

            _baseUrlLabel = new Label { Text = "Base URL", Location = new Point(14, 92), Size = new Size(70, 20) };
            _baseUrl = new ComboBox
            {
                Location = new Point(84, 89), Size = new Size(462, 24), DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            _baseUrl.Items.AddRange(Config.KnownBaseUrls);
            _baseUrl.Text = cfg.OpenAiBaseUrl;

            _link = new LinkLabel
            {
                Location = new Point(14, 120), Size = new Size(532, 20),
                LinkColor = Color.FromArgb(80, 170, 240), ActiveLinkColor = Color.White, BackColor = Color.FromArgb(37, 37, 38)
            };
            _link.LinkClicked += (s, e) => Open((string)e.Link.LinkData);

            _current = new Label { Location = new Point(14, 144), Size = new Size(532, 20), ForeColor = Color.FromArgb(140, 200, 140) };

            var label = new Label { Text = "API key", Location = new Point(14, 176), Size = new Size(70, 20) };
            _key = new TextBox
            {
                Location = new Point(84, 173), Size = new Size(462, 24), UseSystemPasswordChar = true,
                BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle
            };
            _key.TextChanged += (s, e) => _status.Text = "";

            _show = new CheckBox
            {
                Text = "Show key", Location = new Point(84, 202), Size = new Size(100, 22),
                ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat
            };
            _show.CheckedChanged += (s, e) => _key.UseSystemPasswordChar = !_show.Checked;

            _status = new Label { Location = new Point(14, 232), Size = new Size(532, 52), ForeColor = Color.Silver };

            _remove = Btn("Remove key", new Point(14, 328), 100, Color.FromArgb(60, 60, 63));
            _remove.Click += OnRemove;
            _save = Btn("Validate && save", new Point(366, 328), 120, Color.FromArgb(0, 122, 204));
            _save.Click += OnSave;
            _cancel = Btn("Cancel", new Point(492, 328), 54, Color.FromArgb(60, 60, 63));
            _cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { intro, provLabel, _anthropic, _openai, _baseUrlLabel, _baseUrl, _link, _current,
                                              label, _key, _show, _status, _remove, _save, _cancel });
            AcceptButton = _save;
            CancelButton = _cancel;

            OnProviderChanged();
        }

        private static Button Btn(string text, Point at, int width, Color back) => new Button
        {
            Text = text, Location = at, Size = new Size(width, 30), FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White
        };

        private static void Open(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void OnProviderChanged()
        {
            bool openai = _openai.Checked;
            _baseUrlLabel.Enabled = _baseUrl.Enabled = openai;

            _link.Links.Clear();
            if (openai)
            {
                _link.Text = "Create a key at platform.openai.com  ·  or openrouter.ai (set the base URL accordingly)";
                _link.Links.Add(16, 20, OpenAiConsole);
                _link.Links.Add(43, 13, OpenRouterConsole);
            }
            else
            {
                _link.Text = "Create a key at console.anthropic.com";
                _link.Links.Add(16, 21, AnthropicConsole);
            }
            ShowCurrent();
        }

        /// <summary>Describe what is configured for the selected provider, without ever revealing the whole key.</summary>
        private void ShowCurrent()
        {
            bool openai = _openai.Checked;
            string key = openai ? _cfg.OpenAiKey : _cfg.AnthropicKey;
            string source = openai ? _cfg.OpenAiKeySource : _cfg.AnthropicKeySource;
            string env = openai ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY";
            bool stored = !string.IsNullOrEmpty(key) && source == "config";
            _remove.Enabled = stored;

            if (stored)
            {
                _current.ForeColor = Color.FromArgb(140, 200, 140);
                _current.Text = "A key is already saved (" + Mask(key) + "). Enter a new one below to replace it.";
                _save.Text = "Validate && replace";
            }
            else if (!string.IsNullOrEmpty(key) && source == "env")
            {
                _current.ForeColor = Color.FromArgb(220, 200, 120);
                _current.Text = "Using " + env + " from your environment (" + Mask(key) + "). A key saved here takes priority.";
                _save.Text = "Validate && save";
            }
            else
            {
                _current.ForeColor = Color.Silver;
                _current.Text = "No key configured for this provider yet.";
                _save.Text = "Validate && save";
            }
        }

        private static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 12) return new string('*', key.Length);
            return key.Substring(0, 8) + new string('*', 6) + key.Substring(key.Length - 4);
        }

        private void OnRemove(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "Delete the saved " + (_openai.Checked ? "OpenAI-compatible" : "Anthropic") + " API key from " + Config.FilePath + "?",
                    "AutoGAD", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try { Config.ClearApiKey(SelectedProvider); }
            catch (Exception ex)
            {
                _status.ForeColor = Color.Salmon;
                _status.Text = "Could not remove the key: " + ex.Message;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private async void OnSave(object sender, EventArgs e)
        {
            string key = _key.Text.Trim();
            string baseUrl = _baseUrl.Text.Trim().TrimEnd('/');
            if (key.Length == 0)
            {
                _status.ForeColor = Color.Salmon;
                _status.Text = "Enter a key first.";
                return;
            }
            if (_openai.Checked && !baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                _status.ForeColor = Color.Salmon;
                _status.Text = "Enter the base URL of the API, e.g. https://api.openai.com/v1 or https://openrouter.ai/api/v1.";
                return;
            }

            _save.Enabled = _cancel.Enabled = _key.Enabled = false;
            _status.ForeColor = Color.Silver;
            _status.Text = "Checking the key with the API...";

            string error;
            try { error = await LlmClient.ValidateKeyAsync(SelectedProvider, key, baseUrl); }
            catch (Exception ex) { error = ex.Message; }

            _save.Enabled = _cancel.Enabled = _key.Enabled = true;

            if (error != null)
            {
                _status.ForeColor = Color.Salmon;
                _status.Text = error;
                // Network/proxy failures shouldn't block a key the user knows is good.
                if (error.StartsWith("Rejected", StringComparison.Ordinal)) return;
                if (MessageBox.Show(this, error + "\r\n\r\nSave the key anyway?", "AutoGAD",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            try { Config.SaveApiKey(key, SelectedProvider, _openai.Checked ? baseUrl : null); }
            catch (Exception ex)
            {
                _status.ForeColor = Color.Salmon;
                _status.Text = "Could not save: " + ex.Message;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
