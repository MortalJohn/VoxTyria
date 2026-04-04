using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;

namespace VoxTyria {

    internal static class ModuleSettingsView {

        // ── Layout constants ──────────────────────────────────────────────────
        private const int LABEL_WIDTH   = 210;
        private const int CONTROL_WIDTH = 230;
        private const int ROW_HEIGHT    = 34;
        private const int ROW_WIDTH     = LABEL_WIDTH + CONTROL_WIDTH; // 440

        // ── Window factory ────────────────────────────────────────────────────

        public static StandardWindow CreateWindow(
            SettingEntry<KeyBinding>      micToggleKey,
            SettingEntry<MicToggleMode>   micToggleMode,
            SettingEntry<WhisperModel>    whisperModel,
            SettingEntry<WhisperLanguage> transcriptionLanguage,
            SettingEntry<string>          microphoneDevice,
            SettingEntry<string>          customDictionary,
            SettingEntry<bool>            chatChannelPrefixEnabled,
            SettingEntry<bool>            emoteEnabled,
            SettingEntry<bool>            onlyWhenGw2Focused,
            SettingEntry<bool>            showCornerIcon,
            SettingEntry<bool>            hideMicPopup,
            string                        moduleDataDir,
            AsyncTexture2D                emblem) {

            var window = new StandardWindow(
                GameService.Content.DatAssetCache.GetTextureFromAssetId(155985),
                new Rectangle(40, 26, 913, 691),
                new Rectangle(70, 71, 839, 605)) {
                Parent        = GameService.Graphics.SpriteScreen,
                Title         = "Vox Tyria",
                Emblem        = emblem,
                SavesPosition = true,
                SavesSize     = true,
                Id            = "VoxTyriaSettingsWindow",
                Width         = 500,
                Height        = 560,
                CanResize     = true,
            };

            // Scrollable flow panel that always fills the window content area,
            // even after the user resizes the window.
            var flow = new FlowPanel {
                Parent              = window,
                FlowDirection       = ControlFlowDirection.SingleTopToBottom,
                ControlPadding      = new Vector2(4, 6),
                OuterControlPadding = new Vector2(8, 8),
                WidthSizingMode     = SizingMode.Fill,
                HeightSizingMode    = SizingMode.Fill,
                CanScroll           = true,
            };

            AddKeybindRow(flow, micToggleKey,
                "Keybind to start/stop recording. In Push-to-talk mode, hold to record and release to send.");

            AddEnumRow(flow, "Hotkey Mode", micToggleMode,
                v => (MicToggleMode)Enum.Parse(typeof(MicToggleMode), v),
                "Toggle: press once to start, press again to stop.\nPush-to-talk: hold to record, release to send.");

            AddEnumRow(flow, "Whisper Model", whisperModel,
                v => (WhisperModel)Enum.Parse(typeof(WhisperModel), v),
                "Tiny — fastest, least accurate (~75 MB). Good for short chat phrases.\nBase — moderate speed, noticeably better accuracy (~145 MB).\nSmall — slowest, best accuracy (~460 MB). Worth it for complex sentences.\nDownloaded automatically on first use. Changing model triggers a re-download.");

            AddEnumRow(flow, "Language", transcriptionLanguage,
                v => (WhisperLanguage)Enum.Parse(typeof(WhisperLanguage), v),
                "Language of your speech. Auto detects automatically.\nPicking a specific language improves speed and accuracy.");

            AddMicDeviceRow(flow, microphoneDevice,
                "Which microphone to record from.\n(Windows Default) lets Windows choose the active input device.");

            AddVocabRow(flow, customDictionary, moduleDataDir,
                "Comma-separated words/phrases that bias transcription toward GW2 terms.\nHelps Whisper recognise guild tags, boss names, and player names.");

            AddSeparator(flow);

            AddCheckboxRow(flow, "Chat Channel Commands", chatChannelPrefixEnabled,
                "Start your phrase with a channel name to route it.\n\"map incoming at north\" → /map incoming at north\nChannels: say, map, yell, squad, party, guild, whisper.");

            AddCheckboxRow(flow, "Voice Emotes", emoteEnabled,
                "Say a single emote word (e.g. \"dance\") to perform /dance in-game.\nOnly triggers when the entire phrase is a recognised emote.");

            AddCheckboxRow(flow, "Only When GW2 Focused", onlyWhenGw2Focused,
                "Ignore the mic hotkey unless Guild Wars 2 is the active foreground window.\nPrevents accidental recordings while tabbed out.");

            AddCheckboxRow(flow, "Hide Menu Icon", showCornerIcon,
                "When checked, hides the Vox Tyria icon from the Blish HUD corner icon tray.\nThe module continues to work even when the icon is hidden.");

            AddCheckboxRow(flow, "Hide Mic On/Off Popup", hideMicPopup,
                "Suppress the on-screen notification when recording starts or stops.\nError messages and 'nothing heard' warnings are always shown.");

            return window;
        }

        // ── Row builders ──────────────────────────────────────────────────────

        private static void AddKeybindRow(Container parent,
            SettingEntry<KeyBinding> setting, string tooltip) {

            // KeybindingAssigner mutates the KeyBinding in-place AND fires BindingChanged.
            // We subscribe to BindingChanged to force the setter so Blish HUD marks
            // the setting dirty and persists it on next save.
            var assigner = new KeybindingAssigner(setting.Value) {
                Parent           = parent,
                KeyBindingName   = "Mic Hotkey",
                NameWidth        = LABEL_WIDTH,
                Width            = ROW_WIDTH,
                Height           = ROW_HEIGHT,
                BasicTooltipText = tooltip,
            };
            assigner.BindingChanged += (s, e) => {
                // Re-assign through the setter so the settings manager marks it dirty.
                setting.Value = new KeyBinding(assigner.KeyBinding.PrimaryKey) {
                    ModifierKeys = assigner.KeyBinding.ModifierKeys,
                    Enabled      = true,
                };
            };
        }

        private static void AddEnumRow<T>(Container parent, string label,
            SettingEntry<T> setting, Func<string, T> parse,
            string tooltip) where T : struct, Enum {

            var row = MakeRow(parent, tooltip);
            MakeLabel(row, label);

            var dd = new Dropdown {
                Parent           = row,
                Left             = LABEL_WIDTH,
                Width            = CONTROL_WIDTH,
                Height           = ROW_HEIGHT,
                BasicTooltipText = tooltip,
            };

            foreach (T val in Enum.GetValues(typeof(T)))
                dd.Items.Add(val.ToString());

            dd.SelectedItem  = setting.Value.ToString();
            dd.ValueChanged += (s, e) => {
                try { setting.Value = parse(e.CurrentValue); } catch { }
            };
            setting.SettingChanged += (s, e) => dd.SelectedItem = e.NewValue.ToString();
        }

        private static void AddMicDeviceRow(Container parent,
            SettingEntry<string> setting, string tooltip) {

            var row = MakeRow(parent, tooltip);
            MakeLabel(row, "Microphone Device");

            const string DEFAULT_LABEL = "(Windows Default)";

            var dd = new Dropdown {
                Parent           = row,
                Left             = LABEL_WIDTH,
                Width            = CONTROL_WIDTH,
                Height           = ROW_HEIGHT,
                BasicTooltipText = tooltip,
            };

            dd.Items.Add(DEFAULT_LABEL);
            foreach (string name in AudioRecorder.GetAvailableDeviceNames())
                dd.Items.Add(name);

            dd.SelectedItem  = string.IsNullOrWhiteSpace(setting.Value) ? DEFAULT_LABEL : setting.Value;
            dd.ValueChanged += (s, e) =>
                setting.Value = e.CurrentValue == DEFAULT_LABEL ? string.Empty : e.CurrentValue;
            setting.SettingChanged += (s, e) =>
                dd.SelectedItem = string.IsNullOrWhiteSpace(e.NewValue) ? DEFAULT_LABEL : e.NewValue;
        }

        private static void AddVocabRow(Container parent,
            SettingEntry<string> setting, string dataDir, string tooltip) {

            var row = MakeRow(parent, tooltip);
            MakeLabel(row, "Custom Vocabulary");

            new StandardButton {
                Parent           = row,
                Left             = LABEL_WIDTH,
                Text             = "Edit Vocabulary in Notepad\u2026",
                Width            = CONTROL_WIDTH,
                Height           = ROW_HEIGHT,
                BasicTooltipText = tooltip,
            }.Click += (s, e) => OpenVocabFile(setting, dataDir);
        }

        private static void OpenVocabFile(SettingEntry<string> setting, string dataDir) {
            try {
                Directory.CreateDirectory(dataDir);
                string filePath = Path.Combine(dataDir, "vocabulary.txt");
                File.WriteAllText(filePath, setting.Value ?? string.Empty);
                Process.Start(new ProcessStartInfo {
                    FileName        = filePath,
                    UseShellExecute = true,
                });
                StartWatcher(filePath, setting);
            } catch (Exception ex) {
                Logger.GetLogger<Module>().Warn(ex, "Failed to open vocabulary file.");
                ScreenNotification.ShowNotification(
                    "Vox Tyria: Could not open vocabulary file.",
                    ScreenNotification.NotificationType.Error, null, 5);
            }
        }

        private static FileSystemWatcher _vocabWatcher;

        private static void StartWatcher(string filePath, SettingEntry<string> setting) {
            _vocabWatcher?.Dispose();
            var watcher = new FileSystemWatcher {
                Path                = Path.GetDirectoryName(filePath),
                Filter              = Path.GetFileName(filePath),
                NotifyFilter        = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            Timer debounce = null;
            watcher.Changed += (s, e) => {
                debounce?.Dispose();
                debounce = new Timer(_ => {
                    try { setting.Value = File.ReadAllText(e.FullPath); } catch { }
                }, null, 400, Timeout.Infinite);
            };
            _vocabWatcher = watcher;
        }

        private static void AddCheckboxRow(Container parent, string label,
            SettingEntry<bool> setting, string tooltip) {

            // Wrap checkbox in a panel so the tooltip covers the whole row width.
            var wrapper = new Panel {
                Parent           = parent,
                Width            = ROW_WIDTH + 8,
                Height           = ROW_HEIGHT,
                BasicTooltipText = tooltip,
            };
            var cb = new Checkbox {
                Parent   = wrapper,
                Text     = label,
                Checked  = setting.Value,
                Width    = ROW_WIDTH,
                Height   = ROW_HEIGHT,
            };
            cb.CheckedChanged += (s, e) => setting.Value = cb.Checked;
            setting.SettingChanged += (s, e) => {
                if (cb.Checked != e.NewValue) cb.Checked = e.NewValue;
            };
        }

        private static void AddSeparator(Container parent) {
            new Panel {
                Parent          = parent,
                Width           = ROW_WIDTH + 8,
                Height          = 1,
                BackgroundColor = new Color(Color.White, 0.15f),
            };
        }

        private static Panel MakeRow(Container parent, string tooltip) =>
            new Panel {
                Parent           = parent,
                Width            = ROW_WIDTH,
                Height           = ROW_HEIGHT,
                BasicTooltipText = tooltip,
            };

        private static void MakeLabel(Container row, string text) =>
            new Label {
                Parent            = row,
                Text              = text,
                TextColor         = Color.White,
                Width             = LABEL_WIDTH,
                Height            = ROW_HEIGHT,
                AutoSizeWidth     = false,
                AutoSizeHeight    = false,
                VerticalAlignment = VerticalAlignment.Middle,
            };
    }
}
