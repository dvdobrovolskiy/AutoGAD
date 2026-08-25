// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AutoGAD.AutoGadApp))]
[assembly: CommandClass(typeof(AutoGAD.AutoGadApp))]

namespace AutoGAD
{
    /// <summary>
    /// Entry point. On load: creates the AutoGAD ribbon tab + pull-down menu and prepares the config
    /// folder. Commands: AUTOGAD (chat), AUTOGADKEY, AUTOGADSET, AUTOGADMEM, AUTOGADMEMCLEAR,
    /// AUTOGADRESET, AUTOGADVER, AUTOGADCFG.
    /// </summary>
    public class AutoGadApp : IExtensionApplication
    {
        private static PaletteSet _ps;
        private static ChatControl _chat;
        private static readonly Guid PaletteId = new Guid("B6E9F1A2-7C44-4E0B-9A1D-A07A60000001");

        public void Initialize()
        {
            // The ribbon and the editor are not ready inside Initialize(); finish on the first idle tick.
            AcApp.Idle += OnFirstIdle;
        }

        public void Terminate() { }

        private static void OnFirstIdle(object sender, EventArgs e)
        {
            AcApp.Idle -= OnFirstIdle;
            try { Config.Load(); } catch { }        // creates %APPDATA%\AutoGAD + memory/history, migrates an installer-provided key
            RibbonUi.Install();
            RibbonUi.AddPulldownMenu();
            Editor()?.WriteMessage("\nAutoGAD " + Config.Version + " loaded. Ribbon tab: AutoGAD · command: AUTOGAD\n");
        }

        private static Editor Editor() => AcApp.DocumentManager.MdiActiveDocument?.Editor;

        private static ChatControl Chat
        {
            get
            {
                if (_ps == null)
                {
                    _ps = new PaletteSet("AutoGAD", PaletteId)
                    {
                        Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton,
                        MinimumSize = new System.Drawing.Size(340, 460)
                    };
                    _chat = new ChatControl();
                    _ps.Add("Chat", _chat);
                }
                return _chat;
            }
        }

        [CommandMethod("AUTOGAD")]
        public void ShowPalette()
        {
            var chat = Chat;
            _ps.Visible = true;
            chat.OnDocumentChanged();
        }

        /// <summary>Set, replace or remove the Anthropic API key.</summary>
        [CommandMethod("AUTOGADKEY")]
        public void SetApiKey()
        {
            var ed = Editor();
            if (!ApiKeyForm.Prompt())
            {
                ed?.WriteMessage("\nAutoGAD: API key unchanged.");
                return;
            }

            _chat?.ReloadSettings();   // next question uses the new key, no AutoCAD restart needed
            _chat?.ShowWelcome();

            var cfg = Config.Load();
            ed?.WriteMessage(cfg.HasApiKey
                ? "\nAutoGAD: API key updated (" + (cfg.ApiKeySource == "env" ? "from " + cfg.EnvVarName : Config.FilePath) + ")."
                : "\nAutoGAD: API key removed. Run AUTOGADKEY to set a new one.");
        }

        /// <summary>Model, effort, max tokens, refusal fallback, auto-approve.</summary>
        [CommandMethod("AUTOGADSET")]
        public void Settings()
        {
            var ed = Editor();
            if (!SettingsForm.Prompt()) { ed?.WriteMessage("\nAutoGAD: settings unchanged."); return; }
            _chat?.ReloadSettings();
            var cfg = Config.Load();
            ed?.WriteMessage("\nAutoGAD: " + cfg.ProviderLabel + " · " + cfg.Model + " · effort " + cfg.Effort + " · max " + cfg.MaxTokens +
                             " tokens · fallback " + (cfg.Fallbacks ? "on" : "off") + " · auto-approve " + (cfg.AutoApprove ? "on" : "off") + ".");
        }

        /// <summary>Show and edit what the agent has remembered about this drawing and this user.</summary>
        [CommandMethod("AUTOGADMEM")]
        public void ShowMemory()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var agent = _chat?.AgentOrNull;
            var (user, drawing) = agent != null ? agent.Memories(doc)
                                                : (Memory.LoadUser(), doc?.Database != null ? Memory.LoadDrawing(doc.Database, doc.Name) : null);
            bool changed = MemoryForm.Prompt(user, drawing, _chat != null ? (Action)_chat.ClearHistory : null);
            if (changed && doc != null) agent?.Reset(doc);   // next turn rebuilds the system prompt from the edited stores
        }

        /// <summary>Erase remembered notes from the command line, after asking which store.</summary>
        [CommandMethod("AUTOGADMEMCLEAR")]
        public void ClearMemory()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var ed = doc?.Editor;
            if (ed == null) return;

            var opts = new PromptKeywordOptions("\nClear which memory?");
            opts.Keywords.Add("Drawing");
            opts.Keywords.Add("User");
            opts.Keywords.Add("Both");
            opts.Keywords.Add("Cancel");
            opts.Keywords.Default = "Cancel";
            opts.AllowNone = true;

            PromptResult res = ed.GetKeywords(opts);
            if (res.Status != PromptStatus.OK) return;

            bool clearDrawing = res.StringResult == "Drawing" || res.StringResult == "Both";
            bool clearUser = res.StringResult == "User" || res.StringResult == "Both";
            if (!clearDrawing && !clearUser) { ed.WriteMessage("\nCancelled."); return; }

            if (clearDrawing && doc.Database != null)
            {
                var m = Memory.LoadDrawing(doc.Database, doc.Name);
                int n = m.Entries.Count;
                m.Clear();
                ed.WriteMessage("\nCleared " + n + " note(s) for this drawing.");
            }
            if (clearUser)
            {
                var m = Memory.LoadUser();
                int n = m.Entries.Count;
                m.Clear();
                ed.WriteMessage("\nCleared " + n + " note(s) for this user.");
            }

            _chat?.AgentOrNull?.Reset(doc);   // drop the in-memory copy so the next question reloads
        }

        /// <summary>Forget the current chat and re-read the drawing on the next question.</summary>
        [CommandMethod("AUTOGADRESET")]
        public void ResetConversation()
        {
            var chat = Chat;
            _ps.Visible = true;
            chat.ResetConversation();
        }

        /// <summary>Print version, load path and key status — the first thing to ask for in a support report.</summary>
        [CommandMethod("AUTOGADVER")]
        public void Version()
        {
            var ed = Editor();
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string location = string.IsNullOrEmpty(asm.Location) ? "(in memory)" : asm.Location;

            string key, settings;
            try
            {
                var cfg = Config.Load();
                key = !cfg.HasApiKey ? "not set"
                    : cfg.ApiKeySource == "env" ? "set (" + cfg.EnvVarName + ")"
                    : "set (encrypted in config.json)";
                settings = cfg.ProviderLabel + " · " + cfg.Model + " · effort " + cfg.Effort + " · max " + cfg.MaxTokens + " tokens" +
                           (cfg.AutoApprove ? " · auto-approve" : "");
            }
            catch (System.Exception ex) { key = "unreadable: " + ex.Message; settings = "?"; }   // Autodesk.AutoCAD.Runtime also defines Exception

            string text = "\nAutoGAD " + Config.Version +
                          "\n  loaded from: " + location +
                          "\n  config:      " + Config.FilePath +
                          "\n  API key:     " + key +
                          "\n  settings:    " + settings;

            if (ed != null) ed.WriteMessage(text);
            else Console.WriteLine(text);   // core console / no active document
        }

        /// <summary>Open config.json in Notepad. The key is encrypted — use AUTOGADKEY for that; AUTOGADSET edits the rest.</summary>
        [CommandMethod("AUTOGADCFG")]
        public void OpenConfig()
        {
            Config.Load();   // makes sure the file exists
            Editor()?.WriteMessage("\nAutoGAD config: " + Config.FilePath +
                                   "\nThe apiKeyEnc value is encrypted — use AUTOGADKEY to change the key.");
            try { System.Diagnostics.Process.Start("notepad.exe", Config.FilePath); } catch { }
        }
    }
}
