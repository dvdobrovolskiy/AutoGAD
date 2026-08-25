// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoGAD
{
    /// <summary>
    /// %APPDATA%\AutoGAD\config.json:
    ///   provider          "anthropic" (default) or "openai" (any OpenAI-compatible API: OpenAI, OpenRouter, ...)
    ///   apiKeyEnc         Anthropic key      | model         Anthropic model
    ///   openaiApiKeyEnc   OpenAI-compat key  | openaiModel   OpenAI-compat model | openaiBaseUrl
    ///   maxTokens, effort, fallbacks (Anthropic only), autoApprove
    /// Keys are stored DPAPI-encrypted for the current Windows user, never in plain text. A plain-text
    /// "apiKey" (or the installer's apikey.pending file) is migrated to the encrypted field on first read.
    /// If nothing is configured, ANTHROPIC_API_KEY / OPENAI_API_KEY from the environment are used.
    /// </summary>
    public class Config
    {
        public const string ProviderAnthropic = "anthropic";
        public const string ProviderOpenAi = "openai";

        public const string DefaultModel = "claude-opus-5";
        public const string DefaultOpenAiModel = "gpt-5";
        public const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
        public const int DefaultMaxTokens = 16000;
        public const string DefaultEffort = "high";

        public static readonly string[] Efforts = { "low", "medium", "high", "xhigh", "max" };
        public static readonly string[] KnownModels = { "claude-opus-5", "claude-fable-5", "claude-sonnet-5", "claude-opus-4-8" };
        public static readonly string[] KnownOpenAiModels =
        {
            "gpt-5", "gpt-5-mini", "gpt-4.1", "o3",
            "anthropic/claude-opus-4.8", "openai/gpt-5", "google/gemini-2.5-pro"   // OpenRouter-style ids
        };
        public static readonly string[] KnownBaseUrls = { DefaultOpenAiBaseUrl, "https://openrouter.ai/api/v1" };

        // ---- stored fields
        public string Provider = ProviderAnthropic;
        public string AnthropicKey, AnthropicKeySource;    // source: "config", "env" or null
        public string OpenAiKey, OpenAiKeySource;
        public string AnthropicModel = DefaultModel;
        public string OpenAiModel = DefaultOpenAiModel;
        public string OpenAiBaseUrl = DefaultOpenAiBaseUrl;
        public int MaxTokens = DefaultMaxTokens;
        public string Effort = DefaultEffort;
        public bool Fallbacks = true;       // server-side refusal fallback (Anthropic only)
        public bool AutoApprove = false;    // skip confirmation dialogs for write tools

        // ---- views of the ACTIVE provider (what the rest of the plugin uses)
        public bool IsOpenAi => Provider == ProviderOpenAi;
        public string ProviderLabel => IsOpenAi ? "OpenAI-compatible" : "Anthropic";
        public string EnvVarName => IsOpenAi ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY";
        public string ApiKey => IsOpenAi ? OpenAiKey : AnthropicKey;
        public string ApiKeySource => IsOpenAi ? OpenAiKeySource : AnthropicKeySource;
        public string Model => IsOpenAi ? OpenAiModel : AnthropicModel;
        public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

        public static string NormalizeProvider(string p) =>
            string.Equals(p, ProviderOpenAi, StringComparison.OrdinalIgnoreCase) ? ProviderOpenAi : ProviderAnthropic;

        /// <summary>Plugin version (from the assembly; stamped by make_installer.bat from version.txt).</summary>
        public static string Version
        {
            get
            {
                try { return typeof(Config).Assembly.GetName().Version?.ToString(3) ?? "?"; }
                catch { return "?"; }
            }
        }

        public static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoGAD");
        public static string FilePath => Path.Combine(Dir, "config.json");
        public static string PendingKeyPath => Path.Combine(Dir, "apikey.pending");

        /// <summary>Create %APPDATA%\AutoGAD and the memory/history folders so every store is ready on first run.</summary>
        public static void EnsureDirs()
        {
            foreach (var d in new[] { Dir, Path.Combine(Dir, "memory"), Path.Combine(Dir, "memory", "drawings"), Path.Combine(Dir, "history") })
            {
                try { Directory.CreateDirectory(d); } catch { }
            }
        }

        /// <summary>Read the config, creating it from a template if missing. Never returns null; keys may be unset.</summary>
        public static Config Load()
        {
            var c = new Config();
            EnsureDirs();

            JsonObject o = ReadJson();
            if (o == null)
            {
                o = Template();
                TryWrite(o);
            }

            c.Provider = NormalizeProvider(Str(o, "provider", ProviderAnthropic));
            c.AnthropicModel = Str(o, "model", DefaultModel);
            c.OpenAiModel = Str(o, "openaiModel", DefaultOpenAiModel);
            c.OpenAiBaseUrl = Str(o, "openaiBaseUrl", DefaultOpenAiBaseUrl).Trim().TrimEnd('/');
            if (c.OpenAiBaseUrl.Length == 0) c.OpenAiBaseUrl = DefaultOpenAiBaseUrl;
            c.MaxTokens = Int(o, "maxTokens", DefaultMaxTokens);
            c.Effort = Str(o, "effort", DefaultEffort);
            if (Array.IndexOf(Efforts, c.Effort) < 0) c.Effort = DefaultEffort;
            c.Fallbacks = Bool(o, "fallbacks", true);
            c.AutoApprove = Bool(o, "autoApprove", false);

            // Installer hand-off (plain file) — may also switch the provider and base URL.
            TakePendingKey(c);

            // Legacy plain-text "apiKey": take it, then re-save encrypted.
            string legacy = Str(o, "apiKey", "");
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                c.AnthropicKey = legacy.Trim();
                c.AnthropicKeySource = "config";
                try { SaveApiKey(c.AnthropicKey, ProviderAnthropic, keepProvider: true); } catch { }
            }

            if (c.AnthropicKey == null) c.AnthropicKey = Decrypt(Str(o, "apiKeyEnc", ""), out c.AnthropicKeySource);
            if (c.OpenAiKey == null) c.OpenAiKey = Decrypt(Str(o, "openaiApiKeyEnc", ""), out c.OpenAiKeySource);

            if (string.IsNullOrWhiteSpace(c.AnthropicKey))
            {
                string env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (!string.IsNullOrWhiteSpace(env)) { c.AnthropicKey = env.Trim(); c.AnthropicKeySource = "env"; }
            }
            if (string.IsNullOrWhiteSpace(c.OpenAiKey))
            {
                string env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (!string.IsNullOrWhiteSpace(env)) { c.OpenAiKey = env.Trim(); c.OpenAiKeySource = "env"; }
            }

            return c;
        }

        /// <summary>Decrypt a stored blob; fails (→ null) if it was written by a different Windows user.</summary>
        private static string Decrypt(string enc, out string source)
        {
            source = null;
            if (string.IsNullOrWhiteSpace(enc)) return null;
            try
            {
                string key = DataProtection.Unprotect(enc);
                if (string.IsNullOrWhiteSpace(key)) return null;
                source = "config";
                return key;
            }
            catch { return null; }
        }

        /// <summary>
        /// The installer drops the key in a plain file: either a bare Anthropic key (old format) or
        /// JSON {"provider":..,"apiKey":..,"baseUrl":..}. Encrypt it into config.json once and delete the file.
        /// </summary>
        private static void TakePendingKey(Config c)
        {
            string text;
            try
            {
                if (!File.Exists(PendingKeyPath)) return;
                text = File.ReadAllText(PendingKeyPath).Trim();
                try { File.Delete(PendingKeyPath); } catch { }
            }
            catch { return; }
            if (text.Length == 0) return;

            string provider = ProviderAnthropic, key = text, baseUrl = null;
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    var j = JsonNode.Parse(text) as JsonObject;
                    provider = NormalizeProvider(Str(j, "provider", ProviderAnthropic));
                    key = Str(j, "apiKey", "").Trim();
                    baseUrl = Str(j, "baseUrl", "").Trim();
                }
                catch { return; }
            }
            if (key.Length == 0) return;

            try
            {
                SaveApiKey(key, provider, baseUrl: string.IsNullOrEmpty(baseUrl) ? null : baseUrl);
                c.Provider = provider;
                if (provider == ProviderOpenAi)
                {
                    c.OpenAiKey = key; c.OpenAiKeySource = "config";
                    if (!string.IsNullOrEmpty(baseUrl)) c.OpenAiBaseUrl = baseUrl.TrimEnd('/');
                }
                else { c.AnthropicKey = key; c.AnthropicKeySource = "config"; }
            }
            catch { }
        }

        /// <summary>Encrypt and store a key for one provider and make that provider active (unless keepProvider).</summary>
        public static void SaveApiKey(string apiKey, string provider, string baseUrl = null, bool keepProvider = false)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is empty.", nameof(apiKey));
            provider = NormalizeProvider(provider);
            Directory.CreateDirectory(Dir);

            var o = ReadJson() ?? Template();
            o.Remove("apiKey");                       // never leave the plain-text field behind
            o[provider == ProviderOpenAi ? "openaiApiKeyEnc" : "apiKeyEnc"] = DataProtection.Protect(apiKey.Trim());
            if (!keepProvider) o["provider"] = provider;
            if (baseUrl != null && provider == ProviderOpenAi) o["openaiBaseUrl"] = baseUrl.Trim().TrimEnd('/');
            Write(o);
        }

        /// <summary>Remove one provider's stored key (its environment-variable fallback, if any, still applies).</summary>
        public static void ClearApiKey(string provider)
        {
            var o = ReadJson();
            if (o == null) return;
            o.Remove("apiKey");
            o.Remove(NormalizeProvider(provider) == ProviderOpenAi ? "openaiApiKeyEnc" : "apiKeyEnc");
            Write(o);
        }

        /// <summary>Update any subset of the non-secret settings; null means "leave as is".</summary>
        public static void SaveSettings(string provider = null, string model = null, string openAiModel = null,
                                        string openAiBaseUrl = null, int? maxTokens = null, string effort = null,
                                        bool? fallbacks = null, bool? autoApprove = null)
        {
            var o = ReadJson() ?? Template();
            if (provider != null) o["provider"] = NormalizeProvider(provider);
            if (model != null) o["model"] = model;
            if (openAiModel != null) o["openaiModel"] = openAiModel;
            if (openAiBaseUrl != null) o["openaiBaseUrl"] = openAiBaseUrl.Trim().TrimEnd('/');
            if (maxTokens != null) o["maxTokens"] = maxTokens.Value;
            if (effort != null) o["effort"] = effort;
            if (fallbacks != null) o["fallbacks"] = fallbacks.Value;
            if (autoApprove != null) o["autoApprove"] = autoApprove.Value;
            Write(o);
        }

        private static JsonObject ReadJson()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            }
            catch { return null; }
        }

        private static JsonObject Template() => new JsonObject
        {
            ["provider"] = ProviderAnthropic,
            ["apiKeyEnc"] = "",
            ["model"] = DefaultModel,
            ["openaiApiKeyEnc"] = "",
            ["openaiModel"] = DefaultOpenAiModel,
            ["openaiBaseUrl"] = DefaultOpenAiBaseUrl,
            ["maxTokens"] = DefaultMaxTokens,
            ["effort"] = DefaultEffort,
            ["fallbacks"] = true,
            ["autoApprove"] = false
        };

        private static void Write(JsonObject o)
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }

        private static void TryWrite(JsonObject o) { try { Write(o); } catch { } }

        private static string Str(JsonObject o, string k, string d)
        {
            try { return o?[k] == null ? d : o[k].GetValue<string>(); } catch { return d; }
        }

        private static int Int(JsonObject o, string k, int d)
        {
            try { return o?[k] == null ? d : o[k].GetValue<int>(); } catch { return d; }
        }

        private static bool Bool(JsonObject o, string k, bool d)
        {
            try { return o?[k] == null ? d : o[k].GetValue<bool>(); } catch { return d; }
        }
    }
}
