// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Drawing;
using System.Windows.Forms;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoGAD
{
    /// <summary>AUTOGADSET dialog: provider, model, base URL, effort, max output tokens, refusal fallback, auto-approve.</summary>
    public class SettingsForm : Form
    {
        private readonly ComboBox _provider;
        private readonly ComboBox _model;
        private readonly ComboBox _baseUrl;
        private readonly Label _baseUrlLabel;
        private readonly ComboBox _effort;
        private readonly Label _effortHint;
        private readonly NumericUpDown _maxTokens;
        private readonly CheckBox _fallbacks;
        private readonly CheckBox _auto;
        private readonly Label _keyStatus;

        private readonly Config _cfg;
        private string _anthropicModel, _openAiModel;   // remembered per provider while the user switches
        private bool _switching;

        /// <summary>Show the dialog; returns true when settings were saved.</summary>
        public static bool Prompt()
        {
            using (var f = new SettingsForm(Config.Load()))
            {
                DialogResult r;
                try { r = AcApp.ShowModalDialog(f); }
                catch { r = f.ShowDialog(); }
                return r == DialogResult.OK;
            }
        }

        private bool OpenAiSelected => _provider.SelectedIndex == 1;

        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            _anthropicModel = cfg.AnthropicModel;
            _openAiModel = cfg.OpenAiModel;

            Text = "AutoGAD — Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(480, 392);
            BackColor = Color.FromArgb(37, 37, 38);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            int y = 16;
            Label L(string t) => new Label { Text = t, Location = new Point(14, y + 3), Size = new Size(130, 20), ForeColor = Color.Gainsboro };
            ComboBox Combo(int width, bool editable) => new ComboBox
            {
                Location = new Point(150, y), Size = new Size(width, 24),
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };

            var lProvider = L("Provider");
            _provider = Combo(316, false);
            _provider.Items.AddRange(new object[] { "Anthropic (Claude)", "OpenAI-compatible (OpenAI, OpenRouter, …)" });
            _provider.SelectedIndex = cfg.IsOpenAi ? 1 : 0;
            _provider.SelectedIndexChanged += (s, e) => OnProviderChanged();
            y += 26;
            _keyStatus = new Label { Location = new Point(150, y), Size = new Size(316, 18), ForeColor = Color.Silver };
            y += 26;

            var lModel = L("Model");
            _model = Combo(316, true);
            y += 34;

            _baseUrlLabel = L("Base URL");
            _baseUrl = Combo(316, true);
            _baseUrl.Items.AddRange(Config.KnownBaseUrls);
            _baseUrl.Text = cfg.OpenAiBaseUrl;
            y += 34;

            var lEffort = L("Effort");
            _effort = Combo(140, false);
            _effort.Items.AddRange(Config.Efforts);
            _effort.SelectedItem = cfg.Effort;
            _effortHint = new Label { Location = new Point(296, y + 3), Size = new Size(180, 20), ForeColor = Color.Silver };
            y += 34;

            var lTokens = L("Max output tokens");
            _maxTokens = new NumericUpDown
            {
                Location = new Point(150, y), Size = new Size(140, 24),
                Minimum = 1024, Maximum = 128000, Increment = 1024,
                BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                ThousandsSeparator = true
            };
            _maxTokens.Value = Math.Max(_maxTokens.Minimum, Math.Min(_maxTokens.Maximum, cfg.MaxTokens));
            y += 40;

            _fallbacks = new CheckBox
            {
                Text = "Server-side refusal fallback to another model (Anthropic only)",
                Location = new Point(14, y), Size = new Size(452, 22),
                Checked = cfg.Fallbacks, ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat
            };
            y += 28;

            _auto = new CheckBox
            {
                Text = "Auto-approve edits (no confirmation dialogs for write tools)",
                Location = new Point(14, y), Size = new Size(452, 22),
                Checked = cfg.AutoApprove, ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat
            };
            y += 34;

            var path = new Label
            {
                Text = "Config file: " + Config.FilePath + "\r\nAPI keys are set separately with AUTOGADKEY (one per provider).",
                Location = new Point(14, y), Size = new Size(452, 36), ForeColor = Color.Silver
            };

            var ok = new Button
            {
                Text = "Save", Location = new Point(320, 350), Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White
            };
            ok.Click += OnOk;

            var cancel = new Button
            {
                Text = "Cancel", Location = new Point(406, 350), Size = new Size(60, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 63), ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[] { lProvider, _provider, _keyStatus, lModel, _model, _baseUrlLabel, _baseUrl,
                                              lEffort, _effort, _effortHint, lTokens, _maxTokens, _fallbacks, _auto, path, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;

            OnProviderChanged();
        }

        private void OnProviderChanged()
        {
            if (_switching) return;
            _switching = true;
            try
            {
                bool openai = OpenAiSelected;
                // remember the model typed for the provider we are leaving
                if (_model.Items.Count > 0)
                {
                    if (_model.Tag as string == Config.ProviderOpenAi) _openAiModel = _model.Text.Trim();
                    else if (_model.Tag as string == Config.ProviderAnthropic) _anthropicModel = _model.Text.Trim();
                }
                _model.Items.Clear();
                _model.Items.AddRange(openai ? Config.KnownOpenAiModels : Config.KnownModels);
                _model.Text = openai ? _openAiModel : _anthropicModel;
                _model.Tag = openai ? Config.ProviderOpenAi : Config.ProviderAnthropic;

                _baseUrlLabel.Enabled = _baseUrl.Enabled = openai;
                _fallbacks.Enabled = !openai;
                _effortHint.Text = openai ? "sent as reasoning_effort low/medium/high" : "low = cheap & fast … max = deepest";

                string key = openai ? _cfg.OpenAiKey : _cfg.AnthropicKey;
                string src = openai ? _cfg.OpenAiKeySource : _cfg.AnthropicKeySource;
                _keyStatus.Text = string.IsNullOrEmpty(key) ? "No key for this provider yet — run AUTOGADKEY."
                                : src == "env" ? "Key: from " + (openai ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY")
                                : "Key: saved (encrypted).";
                _keyStatus.ForeColor = string.IsNullOrEmpty(key) ? Color.FromArgb(220, 200, 120) : Color.FromArgb(140, 200, 140);
            }
            finally { _switching = false; }
        }

        private void OnOk(object sender, EventArgs e)
        {
            bool openai = OpenAiSelected;
            string model = _model.Text.Trim();
            if (model.Length == 0) model = openai ? Config.DefaultOpenAiModel : Config.DefaultModel;
            if (openai) _openAiModel = model; else _anthropicModel = model;
            if (string.IsNullOrWhiteSpace(_anthropicModel)) _anthropicModel = Config.DefaultModel;
            if (string.IsNullOrWhiteSpace(_openAiModel)) _openAiModel = Config.DefaultOpenAiModel;

            string baseUrl = _baseUrl.Text.Trim().TrimEnd('/');
            if (openai && !baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Enter the base URL of the API, e.g. https://api.openai.com/v1 or https://openrouter.ai/api/v1.",
                    "AutoGAD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (baseUrl.Length == 0) baseUrl = Config.DefaultOpenAiBaseUrl;

            try
            {
                Config.SaveSettings(provider: openai ? Config.ProviderOpenAi : Config.ProviderAnthropic,
                                    model: _anthropicModel,
                                    openAiModel: _openAiModel,
                                    openAiBaseUrl: baseUrl,
                                    effort: (string)_effort.SelectedItem ?? Config.DefaultEffort,
                                    maxTokens: (int)_maxTokens.Value,
                                    fallbacks: _fallbacks.Checked,
                                    autoApprove: _auto.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings: " + ex.Message, "AutoGAD",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
