using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Blish_HUD.Graphics.UI;
using Blish_HUD.Common.UI.Views;
using Blish_HUD.Input;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace VoxTyria {

    // ── Enums for native Blish HUD dropdowns ─────────────────────────────────

    public enum MicToggleMode {
        Toggle,
        PushToTalk
    }

    public enum WhisperLanguage {
        Auto,
        English,
        Spanish,
        German,
        French,
        Italian,
        Portuguese,
        Dutch,
        Russian,
        Chinese,
        Japanese,
        Korean,
        Turkish,
        Polish,
        Swedish,
        Finnish,
        Norwegian,
        Danish,
        Czech,
        Hungarian,
        Romanian,
        Bulgarian,
        Greek,
        Arabic,
        Hebrew,
        Hindi,
        Indonesian,
        Thai,
        Vietnamese,
        Ukrainian,
        Persian,
        Urdu,
        Malay,
        Tamil
    }

    [Export(typeof(Blish_HUD.Modules.Module))]
    public class Module : Blish_HUD.Modules.Module {

        private static readonly Logger Logger = Logger.GetLogger<Module>();

        // ── Blish HUD service accessors ───────────────────────────────────────
        internal SettingsManager    SettingsManager    => ModuleParameters.SettingsManager;
        internal ContentsManager    ContentsManager    => ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager      Gw2ApiManager      => ModuleParameters.Gw2ApiManager;

        // ── Settings ──────────────────────────────────────────────────────────
        private SettingEntry<KeyBinding>      _micToggleKey;
        private SettingEntry<MicToggleMode>   _micToggleMode;
        private SettingEntry<WhisperModel>    _whisperModel;
        private SettingEntry<WhisperLanguage> _transcriptionLanguage;
        private SettingEntry<string>          _microphoneDevice;
        private SettingEntry<string>          _customDictionary;
        private SettingEntry<bool>            _chatChannelPrefixEnabled;
        private SettingEntry<bool>            _emoteEnabled;
        private SettingEntry<bool>            _onlyWhenGw2Focused;
        private SettingEntry<bool>            _showCornerIcon;
        private SettingEntry<bool>            _hideMicPopup;

        // ── Recording state ───────────────────────────────────────────────────
        private AudioRecorder           _audioRecorder;
        private TranscriptionService    _transcriptionService;
        private ChatInjector            _chatInjector;
        private CancellationTokenSource _downloadCts;
        private SemaphoreSlim           _modelLock = new SemaphoreSlim(1, 1);
        private bool                    _isRecording;
        private DateTime                _lastToggleTime = DateTime.MinValue;

        // ── Corner icon UI ────────────────────────────────────────────────────
        private CornerIcon     _cornerIcon;
        private StandardWindow _settingsWindow;
        private AsyncTexture2D _texIcon;
        private AsyncTexture2D _texIconBig;

        // ── Native interop ────────────────────────────────────────────────────
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        private void ExtractAndLoadWhisperNatives() {
            string nativesDir = Path.Combine(
                DirectoriesManager.GetFullDirectoryPath("WhisperModels"), "natives");
            string runtimeDir = Path.Combine(nativesDir, "runtimes", "win-x64");
            Directory.CreateDirectory(runtimeDir);

            string[] dllNames = {
                "ggml-base-whisper.dll",
                "ggml-cpu-whisper.dll",
                "ggml-whisper.dll",
                "whisper.dll"
            };

            foreach (string name in dllNames) {
                string destPath = Path.Combine(runtimeDir, name);
                if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0) {
                    string tmpPath = destPath + ".tmp";
                    using (Stream src = ContentsManager.GetFileStream($"natives/{name}"))
                    using (FileStream dst = File.Create(tmpPath)) {
                        src.CopyTo(dst);
                    }
                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(tmpPath, destPath);
                }
            }

            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath =
                Path.Combine(nativesDir, "whisper.net.anchor");
        }

        // ── Constants ─────────────────────────────────────────────────────────
        private const string DEFAULT_VOCABULARY =
            "zerg, condi, alac, mesmer, chrono, WvW, Lion's Arch, boon, Holosmith, Firebrand";

        private static readonly TimeSpan DebounceInterval =
            TimeSpan.FromMilliseconds(200);

        // Converts WhisperLanguage enum to the ISO 639-1 code used by Whisper.
        private static string LanguageToCode(WhisperLanguage lang) {
            switch (lang) {
                case WhisperLanguage.English:    return "en";
                case WhisperLanguage.Spanish:    return "es";
                case WhisperLanguage.German:     return "de";
                case WhisperLanguage.French:     return "fr";
                case WhisperLanguage.Italian:    return "it";
                case WhisperLanguage.Portuguese: return "pt";
                case WhisperLanguage.Dutch:      return "nl";
                case WhisperLanguage.Russian:    return "ru";
                case WhisperLanguage.Chinese:    return "zh";
                case WhisperLanguage.Japanese:   return "ja";
                case WhisperLanguage.Korean:     return "ko";
                case WhisperLanguage.Turkish:    return "tr";
                case WhisperLanguage.Polish:     return "pl";
                case WhisperLanguage.Swedish:    return "sv";
                case WhisperLanguage.Finnish:    return "fi";
                case WhisperLanguage.Norwegian:  return "no";
                case WhisperLanguage.Danish:     return "da";
                case WhisperLanguage.Czech:      return "cs";
                case WhisperLanguage.Hungarian:  return "hu";
                case WhisperLanguage.Romanian:   return "ro";
                case WhisperLanguage.Bulgarian:  return "bg";
                case WhisperLanguage.Greek:      return "el";
                case WhisperLanguage.Arabic:     return "ar";
                case WhisperLanguage.Hebrew:     return "he";
                case WhisperLanguage.Hindi:      return "hi";
                case WhisperLanguage.Indonesian: return "id";
                case WhisperLanguage.Thai:       return "th";
                case WhisperLanguage.Vietnamese: return "vi";
                case WhisperLanguage.Ukrainian:  return "uk";
                case WhisperLanguage.Persian:    return "fa";
                case WhisperLanguage.Urdu:       return "ur";
                case WhisperLanguage.Malay:      return "ms";
                case WhisperLanguage.Tamil:      return "ta";
                default:                         return "auto"; // WhisperLanguage.Auto
            }
        }

        [ImportingConstructor]
        public Module([Import("ModuleParameters")] ModuleParameters moduleParameters)
            : base(moduleParameters) { }

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        protected override void DefineSettings(SettingCollection settings) {
            _micToggleKey = settings.DefineSetting(
                "MicToggleKey",
                new KeyBinding(Keys.F10),
                () => "Mic Hotkey",
                () => "Keybind to start/stop recording, or hold to record in push-to-talk mode.");

            _micToggleMode = settings.DefineSetting(
                "MicToggleMode",
                MicToggleMode.Toggle,
                () => "Mic Hotkey Mode",
                () => "Toggle: press once to start, press again to stop. " +
                      "PushToTalk: hold the hotkey to record, release to transcribe and send.");

            _whisperModel = settings.DefineSetting(
                "WhisperModel",
                WhisperModel.Tiny,
                () => "Whisper Model",
                () => "Tiny is fastest — good for short chat phrases. " +
                      "Base is more accurate. Small is best accuracy but slowest. " +
                      "Changing this downloads a new model on next use.");

            _transcriptionLanguage = settings.DefineSetting(
                "TranscriptionLanguage",
                WhisperLanguage.Auto,
                () => "Transcription Language",
                () => "Language for speech recognition. Auto detects it automatically. " +
                      "Selecting a specific language improves accuracy and may use a language-optimised model.");

            _microphoneDevice = settings.DefineSetting(
                "MicrophoneDevice",
                string.Empty,
                () => "Microphone Device",
                () => "Name of the microphone to use. Leave blank to use the Windows default. " +
                      "Type the device name exactly as shown in Windows Sound settings. " +
                      "Available devices are listed in the Blish HUD log on startup.");

            _customDictionary = settings.DefineSetting(
                "CustomDictionary",
                DEFAULT_VOCABULARY,
                () => "Custom Vocabulary",
                () => "Comma-separated words or phrases that bias transcription toward GW2 terms, " +
                      "player names, and guild tags. Helps Whisper recognise game-specific jargon.");

            _chatChannelPrefixEnabled = settings.DefineSetting(
                "ChatChannelPrefixEnabled",
                true,
                () => "Chat Channel Voice Commands",
                () => "Say a channel name at the start of your phrase to route it to that channel. " +
                      "e.g. 'map incoming north' sends '/map incoming north'. " +
                      "Supported channels: say, map, yell, squad, team, party, guild, whisper.");

            _emoteEnabled = settings.DefineSetting(
                "EmoteEnabled",
                true,
                () => "Voice Emotes",
                () => "Say a single emote word to perform it in-game. " +
                      "e.g. saying 'dance' sends '/dance'. " +
                      "Only triggers when the entire recognised phrase is a single emote.");

            _onlyWhenGw2Focused = settings.DefineSetting(
                "OnlyWhenGw2Focused",
                true,
                () => "Only When GW2 Is Focused",
                () => "When enabled, the mic hotkey is ignored unless Guild Wars 2 is the " +
                      "currently active window. Prevents accidental recordings while tabbed out.");

            _showCornerIcon = settings.DefineSetting(
                "HideMenuIcon",
                false,
                () => "Hide Menu Icon",
                () => "When checked, hides the Vox Tyria icon from the Blish HUD corner icon tray. " +
                      "The module continues to work even when the icon is hidden.");

            _hideMicPopup = settings.DefineSetting(
                "HideMicPopup",
                false,
                () => "Hide 'Mic On/Off' Popup",
                () => "Suppresses the screen notification when the mic starts or stops. " +
                      "Error messages and 'nothing heard' warnings are always shown.");
        }

        protected override void Initialize() { }

        protected override void OnModuleLoaded(EventArgs e) {
            ExtractAndLoadWhisperNatives();

            _texIcon    = ContentsManager.GetTexture("textures/mic.png");
            _texIconBig = ContentsManager.GetTexture("textures/mic-big.png");

            _micToggleKey.Value.Enabled = true;
            BindHotkey();
            _micToggleMode.SettingChanged += OnMicToggleModeChanged;

            _audioRecorder = new AudioRecorder();
            _chatInjector  = new ChatInjector();
            _ = AudioRecorder.HasMicrophoneDevice();

            // Log and auto-select microphone devices on startup.
            PopulateMicrophoneDevice();

            _cornerIcon = new CornerIcon {
                Icon      = _texIcon,
                HoverIcon = _texIconBig,
                IconName  = "Vox Tyria: Initialising\u2026"
            };
            _cornerIcon.Click += OnCornerIconClicked;

            _settingsWindow = ModuleSettingsView.CreateWindow(
                _micToggleKey, _micToggleMode, _whisperModel, _transcriptionLanguage,
                _microphoneDevice, _customDictionary,
                _chatChannelPrefixEnabled, _emoteEnabled, _onlyWhenGw2Focused,
                _showCornerIcon, _hideMicPopup,
                DirectoriesManager.GetFullDirectoryPath("WhisperModels"),
                _texIcon);

            if (_showCornerIcon.Value) _cornerIcon.Visible = false;
            _showCornerIcon.SettingChanged += OnShowCornerIconChanged;

            _transcriptionLanguage.SettingChanged += OnTranscriptionSettingChanged;
            _whisperModel.SettingChanged          += OnWhisperModelSettingChanged;

            base.OnModuleLoaded(e);
            TryInitTranscriptionService();
        }

        // Wire hotkey events for the current mode, unsubscribing old ones first.
        private void BindHotkey() {
            _micToggleKey.Value.Activated             -= OnMicToggleActivated;
            GameService.Input.Keyboard.KeyPressed     -= OnPttKeyPressed;
            GameService.Input.Keyboard.KeyReleased    -= OnPttKeyReleased;

            if (_micToggleMode.Value == MicToggleMode.PushToTalk) {
                GameService.Input.Keyboard.KeyPressed  += OnPttKeyPressed;
                GameService.Input.Keyboard.KeyReleased += OnPttKeyReleased;
            } else {
                _micToggleKey.Value.Activated += OnMicToggleActivated;
            }
        }

        private void PopulateMicrophoneDevice() {
            string[] devices = AudioRecorder.GetAvailableDeviceNames();

            if (devices.Length == 0) {
                Logger.Warn("VoxTyria: No microphone devices found.");
                return;
            }

            // Log all available devices so users can find the exact name.
            Logger.Info($"VoxTyria: Available microphone devices: {string.Join(", ", devices)}");

            // Auto-select if only one device exists and user hasn't set a preference.
            if (devices.Length == 1 && string.IsNullOrWhiteSpace(_microphoneDevice.Value)) {
                _microphoneDevice.Value = devices[0];
                Logger.Info($"VoxTyria: Auto-selected microphone: {devices[0]}");
            }
        }

        private void OnMicToggleModeChanged(object sender, ValueChangedEventArgs<MicToggleMode> e) {
            BindHotkey();
        }

        private void OnShowCornerIconChanged(object sender, ValueChangedEventArgs<bool> e) {
            if (_cornerIcon != null) _cornerIcon.Visible = !e.NewValue;
        }

        private void OnCornerIconClicked(object sender, MouseEventArgs e) {
            _settingsWindow?.ToggleWindow();
        }

        public override IView GetSettingsView() {
            var panel = new Panel { Width = 260, Height = 34 };
            var btn = new StandardButton {
                Parent = panel,
                Text   = "Open Vox Tyria Settings\u2026",
                Width  = 240,
                Height = 26,
                Top    = 4,
            };
            btn.Click += (s, e) => _settingsWindow?.Show();
            return new StaticPanelView(panel);
        }

        protected override void Update(GameTime gameTime) {
            if (_cornerIcon == null) return;
            if (_isRecording) {
                // Pulse between 55% and 100% opacity (~2 cycles/sec) to indicate active recording.
                double t = gameTime.TotalGameTime.TotalSeconds;
                _cornerIcon.Opacity = 0.55f + 0.45f * (float)(Math.Sin(t * Math.PI * 2.0) * 0.5 + 0.5);
            } else if (_cornerIcon.Opacity != 1f) {
                _cornerIcon.Opacity = 1f;
            }
        }

        protected override void Unload() {
            if (_micToggleKey?.Value != null) {
                _micToggleKey.Value.Activated             -= OnMicToggleActivated;
                _micToggleKey.Value.Enabled                = false;
            }
            GameService.Input.Keyboard.KeyPressed  -= OnPttKeyPressed;
            GameService.Input.Keyboard.KeyReleased -= OnPttKeyReleased;

            if (_micToggleMode != null)          _micToggleMode.SettingChanged          -= OnMicToggleModeChanged;
            if (_transcriptionLanguage != null)  _transcriptionLanguage.SettingChanged  -= OnTranscriptionSettingChanged;
            if (_whisperModel != null)           _whisperModel.SettingChanged           -= OnWhisperModelSettingChanged;
            if (_showCornerIcon != null)         _showCornerIcon.SettingChanged         -= OnShowCornerIconChanged;

            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = null;

            _isRecording  = false;
            _chatInjector = null;

            _audioRecorder?.Dispose();
            _audioRecorder = null;

            _transcriptionService?.Dispose();
            _transcriptionService = null;

            _modelLock?.Dispose();

            if (_cornerIcon != null) {
                _cornerIcon.Click -= OnCornerIconClicked;
                _cornerIcon.Dispose();
                _cornerIcon = null;
            }

            _settingsWindow?.Dispose();
            _settingsWindow = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Transcription helpers
        // ─────────────────────────────────────────────────────────────────────

        private void TryInitTranscriptionService() {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();

            var oldService = _transcriptionService;
            _transcriptionService = null;

            _ = EnsureModelAndInitServiceAsync(oldService, _downloadCts.Token);
        }

        private async Task EnsureModelAndInitServiceAsync(
            TranscriptionService oldService, CancellationToken ct) {
            await _modelLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                try { await Task.Delay(300, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                oldService?.Dispose();

                ct.ThrowIfCancellationRequested();

                string language   = LanguageToCode(_transcriptionLanguage?.Value ?? WhisperLanguage.Auto);
                WhisperModel size = _whisperModel?.Value ?? WhisperModel.Tiny;

                string modelsDir = DirectoriesManager.GetFullDirectoryPath("WhisperModels");
                if (string.IsNullOrEmpty(modelsDir))
                    throw new InvalidOperationException(
                        "Blish HUD did not provide the 'WhisperModels' directory path. " +
                        "Check that manifest.json lists \"WhisperModels\" in directories_provided.");

                string modelPath = await ModelManager.EnsureModelAsync(
                    language, size, modelsDir,
                    status => { if (_cornerIcon != null) _cornerIcon.IconName = status; },
                    ct);

                ct.ThrowIfCancellationRequested();

                _transcriptionService = new TranscriptionService(modelPath, language);

                if (_cornerIcon != null) {
                    bool hasMic = AudioRecorder.HasMicrophoneDevice();
                    _cornerIcon.IconName = hasMic
                        ? "Vox Tyria: Ready"
                        : "Vox Tyria: No microphone detected";
                }
            } catch (OperationCanceledException) {
                // Settings changed or module unloading — silently exit.
            } catch (Exception ex) {
                Logger.Error(ex, "Failed to load Whisper model.");
                if (_cornerIcon != null) {
                    _cornerIcon.IconName = "Vox Tyria: Model error \u2014 check log";
                }
                ScreenNotification.ShowNotification(
                    $"Vox Tyria: Failed to load model \u2014 {ex.GetType().Name}: {ex.Message}",
                    ScreenNotification.NotificationType.Error, null, 10);
            } finally {
                _modelLock.Release();
            }
        }

        private void OnTranscriptionSettingChanged(object sender,
            ValueChangedEventArgs<WhisperLanguage> e) {
            TryInitTranscriptionService();
        }

        private void OnWhisperModelSettingChanged(object sender,
            ValueChangedEventArgs<WhisperModel> e) {
            TryInitTranscriptionService();
        }

        private async Task ProcessAudioAsync(MemoryStream audioStream) {
            try {
                if (_transcriptionService == null) {
                    ScreenNotification.ShowNotification(
                        "Vox Tyria: Model is still loading, please wait\u2026",
                        ScreenNotification.NotificationType.Warning, null, 5);
                    return;
                }

                Logger.Info($"VoxTyria: audio stream length = {audioStream.Length} bytes");

                string text = await _transcriptionService.TranscribeAudioAsync(
                    audioStream, _customDictionary.Value);

                Logger.Info($"VoxTyria: transcription result = \"{text}\"");

                if (string.IsNullOrWhiteSpace(text)) {
                    ScreenNotification.ShowNotification(
                        "Vox Tyria: Nothing heard \u2014 speak louder or check your mic.",
                        ScreenNotification.NotificationType.Warning, null, 4);
                    return;
                }

                if (_chatInjector == null) return;

                text = ApplyChatTransforms(text);
                await _chatInjector.InjectMessageAsync(text);
            } catch (Exception ex) {
                Logger.Warn(ex, "ProcessAudioAsync failed.");
                ScreenNotification.ShowNotification(
                    "Vox Tyria: Transcription or injection error.",
                    ScreenNotification.NotificationType.Error, null, 5);
            } finally {
                audioStream.Dispose();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Text transforms
        // ─────────────────────────────────────────────────────────────────────

        private static readonly string[] ChatChannels = {
            "say", "map", "yell", "squad", "team", "party", "guild",
            "g", "s", "t", "p", "m", "y", "w", "whisper"
        };

        private static readonly string[] Emotes = {
            "beckon", "bow", "cheer", "cower", "cry", "crossarms", "dance",
            "kneel", "laugh", "no", "point", "ponder", "salute", "shrug", "sit",
            "sleep", "stretch", "surprised", "talk", "threaten", "upset",
            "victory", "wave", "yes", "angry", "beg", "sad", "swoon"
        };

        private static string NormalizeForMatch(string s) =>
            s.Trim().ToLowerInvariant().Trim('.', '!', '?', ',', ';', ':', '"', '\'', '-');

        private string ApplyChatTransforms(string text) {
            if (_chatChannelPrefixEnabled?.Value == true) {
                string trimmed = text.TrimStart();
                int space = trimmed.IndexOf(' ');
                if (space > 0) {
                    string firstWord = NormalizeForMatch(trimmed.Substring(0, space));
                    foreach (string ch in ChatChannels) {
                        if (firstWord == ch) {
                            text = "/" + ch + trimmed.Substring(space);
                            break;
                        }
                    }
                }
            }

            if (_emoteEnabled?.Value == true) {
                string normalized = NormalizeForMatch(text);
                foreach (string emote in Emotes) {
                    if (normalized == emote) {
                        text = "/" + emote;
                        break;
                    }
                }
            }

            return text;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mic toggle handlers
        // ─────────────────────────────────────────────────────────────────────

        private void OnPttKeyPressed(object sender, KeyboardEventArgs e) {
            if (!_isRecording && e.Key == _micToggleKey.Value.PrimaryKey)
                StartRecording();
        }

        private void OnPttKeyReleased(object sender, KeyboardEventArgs e) {
            if (_isRecording && e.Key == _micToggleKey.Value.PrimaryKey)
                StopRecordingAndProcess();
        }

        private void OnMicToggleActivated(object sender, EventArgs e) {
            if (DateTime.UtcNow - _lastToggleTime < DebounceInterval) return;
            _lastToggleTime = DateTime.UtcNow;

            if (_onlyWhenGw2Focused.Value &&
                !GameService.GameIntegration.Gw2Instance.Gw2HasFocus) return;

            if (_isRecording) StopRecordingAndProcess();
            else              StartRecording();
        }

        private void StartRecording() {
            _isRecording = true;

            if (_cornerIcon != null) {
                _cornerIcon.Icon     = _texIconBig;
                _cornerIcon.IconName = "Vox Tyria: Recording\u2026";
            }

            if (_hideMicPopup?.Value != true) {
                ScreenNotification.ShowNotification(
                    "Vox Tyria: Mic On",
                    ScreenNotification.NotificationType.Green, null, 2);
            }

            try {
                _audioRecorder.StartRecording(_microphoneDevice?.Value);
            } catch (Exception ex) {
                Logger.Warn(ex, "Failed to start recording.");
                _isRecording = false;
                bool hasMic = AudioRecorder.HasMicrophoneDevice();
                if (_cornerIcon != null) {
                    _cornerIcon.Icon     = _texIcon;
                    _cornerIcon.IconName = hasMic
                        ? "Vox Tyria: Ready"
                        : "Vox Tyria: No microphone detected";
                }
                ScreenNotification.ShowNotification(
                    "Vox Tyria: Failed to start microphone. Is a mic connected?",
                    ScreenNotification.NotificationType.Error, null, 5);
            }
        }

        private void StopRecordingAndProcess() {
            _isRecording = false;

            if (_cornerIcon != null) {
                _cornerIcon.Icon     = _texIcon;
                _cornerIcon.IconName = "Vox Tyria: Ready";
            }

            if (_hideMicPopup?.Value != true) {
                ScreenNotification.ShowNotification(
                    "Vox Tyria: Mic Off",
                    ScreenNotification.NotificationType.Red, null, 2);
            }

            MemoryStream audioStream = _audioRecorder.StopRecording();
            if (audioStream != null)
                Task.Run(() => ProcessAudioAsync(audioStream));
        }
    }
}
