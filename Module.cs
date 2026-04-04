using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Blish_HUD.Input;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace VoxTyria {

    [Export(typeof(Blish_HUD.Modules.Module))]
    public class Module : Blish_HUD.Modules.Module {

        private static readonly Logger Logger = Logger.GetLogger<Module>();

        // ── Blish HUD service accessors ───────────────────────────────────────
        internal SettingsManager    SettingsManager    => ModuleParameters.SettingsManager;
        internal ContentsManager    ContentsManager    => ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager      Gw2ApiManager      => ModuleParameters.Gw2ApiManager;

        // ── Settings ──────────────────────────────────────────────────────────
        private SettingEntry<KeyBinding>   _micToggleKey;
        private SettingEntry<string>       _customDictionary;
        private SettingEntry<bool>         _onlyWhenGw2Focused;
        private SettingEntry<string>       _whisperModelPath;
        private SettingEntry<string>       _transcriptionLanguage;
        private SettingEntry<WhisperModel> _whisperModel;
        private SettingEntry<bool>         _chatChannelPrefixEnabled;
        private SettingEntry<bool>         _emoteEnabled;

        // ── Recording state ───────────────────────────────────────────────
        private AudioRecorder        _audioRecorder;
        private TranscriptionService _transcriptionService;
        private ChatInjector         _chatInjector;
        private CancellationTokenSource _downloadCts;
        private SemaphoreSlim        _modelLock = new SemaphoreSlim(1, 1);
        private bool                 _isRecording;
        private DateTime             _lastToggleTime = DateTime.MinValue;

        // ── Corner icon UI ────────────────────────────────────────────────────
        private CornerIcon     _cornerIcon;
        private AsyncTexture2D _texReady;
        private AsyncTexture2D _texRecording;
        private AsyncTexture2D _texNotReady;

        // ── Native interop ────────────────────────────────────────────────────
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        // Whisper.net's NativeLibraryLoader searches for native DLLs by looking
        // for a "runtimes/{platform}-{arch}/" folder relative to the paths in
        // RuntimeOptions.LibraryPath, AppDomain.BaseDirectory, etc.
        // Because Blish HUD loads module assemblies from bytes, all assembly-based
        // paths are null.  We extract the DLLs from the .bhm into a known directory
        // and point RuntimeOptions.LibraryPath at it.
        private void ExtractAndLoadWhisperNatives() {
            string nativesDir = Path.Combine(
                DirectoriesManager.GetFullDirectoryPath("models"), "natives");
            // Whisper.net looks for runtimes/win-x64/ relative to the LibraryPath
            // *directory*, so the actual DLLs must live one level deeper.
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

            // Tell Whisper.net where to look. It calls GetDirectoryName on this
            // value, so any filename in nativesDir works as the anchor path.
            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath =
                Path.Combine(nativesDir, "whisper.net.anchor");
        }

        // ── Constants ─────────────────────────────────────────────────────────
        private const string DEFAULT_VOCABULARY =
            "zerg, condi, alac, mesmer, chrono, WvW, Lion's Arch, boon, Holosmith, Firebrand";

        private static readonly TimeSpan DebounceInterval =
            TimeSpan.FromMilliseconds(200);

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
                () => "Mic Toggle Key",
                () => "Keybind to start / stop microphone recording.");

            _customDictionary = settings.DefineSetting(
                "CustomDictionary",
                DEFAULT_VOCABULARY,
                () => "Custom Vocabulary",
                () => "Comma-separated terms to bias transcription toward GW2 terminology " +
                      "(slang, player names, guild tags).");

            _onlyWhenGw2Focused = settings.DefineSetting(
                "OnlyWhenGw2Focused",
                true,
                () => "Only When GW2 Is Focused",
                () => "When enabled, the mic toggle keybind only fires if " +
                      "Guild Wars 2 is the active window.");

            _transcriptionLanguage = settings.DefineSetting(
                "TranscriptionLanguage",
                "en",
                () => "Transcription Language",
                () => "Language code for speech recognition. " +
                      "Use \"en\" for English (recommended), " +
                      "\"auto\" to detect language automatically, " +
                      "or any ISO 639-1 code: es, de, fr, zh, ja, ko, ru, pt, it, nl. " +
                      "Non-English languages automatically use the multilingual model.");

            _whisperModel = settings.DefineSetting(
                "WhisperModel",
                WhisperModel.Tiny,
                () => "Whisper Model",
                () => "Tiny: fastest, good for short phrases. " +
                      "Base: more accurate, ~2x slower. " +
                      "Small: best accuracy, ~4x slower. " +
                      "Changing this will download the new model on next use.");

            _whisperModelPath = settings.DefineSetting(
                "WhisperModelPath",
                string.Empty,
                () => "Whisper Model Path (Advanced)",
                () => "Optional: full path to a custom GGML model file. " +
                      "Leave empty to use the automatically downloaded model.");

            _chatChannelPrefixEnabled = settings.DefineSetting(
                "ChatChannelPrefixEnabled",
                true,
                () => "Chat Channel Voice Commands",
                () => "When enabled, saying a channel name at the start of your phrase " +
                      "routes it to that channel. " +
                      "e.g. \"map this is a test\" sends \"/map this is a test\".");

            _emoteEnabled = settings.DefineSetting(
                "EmoteEnabled",
                true,
                () => "Voice Emotes",
                () => "When enabled, saying a single emote word performs it in-game. " +
                      "e.g. saying \"dance\" sends \"/dance\".");
        }

        protected override void Initialize() { }

        protected override void OnModuleLoaded(EventArgs e) {
            // Extract whisper native DLLs from the .bhm zip to the models/natives
            // directory and load them before any Whisper.net code runs.
            ExtractAndLoadWhisperNatives();

            // Pre-load all three corner icon textures.
            // Texture files live in ref/textures/ in the module package;
            // ContentsManager paths strip the leading ref/ prefix.
            _texNotReady  = ContentsManager.GetTexture("textures/icon_white.png");
            _texReady     = ContentsManager.GetTexture("textures/icon_green.png");
            _texRecording = ContentsManager.GetTexture("textures/icon_red.png");

            // Enable the keybind and subscribe to its event.
            // IgnoreWhenInTextField defaults to true so typing in GW2 chat
            // cannot accidentally trigger a recording.
            _micToggleKey.Value.Enabled    = true;
            _micToggleKey.Value.Activated += OnMicToggleActivated;

            // Instantiate the audio recorder and probe for a microphone.
            // If no recording device is present the icon stays white and the
            // keybind is still registered so it works if a device is plugged in
            // later (Blish HUD keeps the module loaded between sessions).
            _audioRecorder = new AudioRecorder();
            _chatInjector  = new ChatInjector();

            // Force NAudio.WinMM into the AppDomain now, while Blish HUD's
            // assembly resolver is active.  Without this, if the model download
            // fails before HasMicrophoneDevice() is called, the JIT can't find
            // NAudio.WinMM when AudioRecorder.Dispose() is compiled at unload.
            _ = AudioRecorder.HasMicrophoneDevice();

            // Icon starts white (not ready) — EnsureModelAndInitServiceAsync will
            // update it to green once the model is loaded (or downloaded).
            _cornerIcon = new CornerIcon {
                Icon     = _texNotReady,
                IconName = "Vox Tyria: Initialising…"
            };

            // Subscribe to settings that require re-initialising the service.
            _transcriptionLanguage.SettingChanged += OnTranscriptionSettingChanged;
            _whisperModelPath.SettingChanged      += OnTranscriptionSettingChanged;
            _whisperModel.SettingChanged          += OnWhisperModelSettingChanged;

            // Kick off model resolution / download in the background.
            // base.OnModuleLoaded must be called before the async work so Blish
            // HUD treats the module as loaded regardless of download time.
            base.OnModuleLoaded(e);

            TryInitTranscriptionService();
        }

        protected override void Update(GameTime gameTime) { }

        protected override void Unload() {
            if (_micToggleKey?.Value != null) {
                _micToggleKey.Value.Activated -= OnMicToggleActivated;
                _micToggleKey.Value.Enabled    = false;
            }

            if (_transcriptionLanguage != null)
                _transcriptionLanguage.SettingChanged -= OnTranscriptionSettingChanged;
            if (_whisperModelPath != null)
                _whisperModelPath.SettingChanged -= OnTranscriptionSettingChanged;
            if (_whisperModel != null)
                _whisperModel.SettingChanged -= OnWhisperModelSettingChanged;

            // Cancel any in-progress model download before releasing resources.
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = null;

            // Force-stop any in-flight recording so AudioRecorder.Dispose
            // does not have to guess about outstanding WaveFileWriter state.
            _isRecording  = false;
            _chatInjector = null;

            _audioRecorder?.Dispose();
            _audioRecorder = null;

            _transcriptionService?.Dispose();
            _transcriptionService = null;

            _modelLock?.Dispose();

            _cornerIcon?.Dispose();
            _cornerIcon = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Transcription helpers
        // ─────────────────────────────────────────────────────────────────────

        private void TryInitTranscriptionService() {
            // Cancel any download already in progress.
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();

            // Null the reference immediately so ProcessAudioAsync won't start
            // new transcriptions on the old service. Capture it so we can
            // dispose it safely after any in-flight call finishes.
            var oldService = _transcriptionService;
            _transcriptionService = null;

            _ = EnsureModelAndInitServiceAsync(oldService, _downloadCts.Token);
        }

        private async Task EnsureModelAndInitServiceAsync(
            TranscriptionService oldService, CancellationToken ct) {
            // Serialize model switches so two rapid changes don't race.
            await _modelLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                // Give any in-flight TranscribeAudioAsync a moment to return
                // before we tear down the factory underneath it.
                try { await Task.Delay(300, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                oldService?.Dispose();

                ct.ThrowIfCancellationRequested();

                string language   = _transcriptionLanguage?.Value ?? "en";
                WhisperModel size = _whisperModel?.Value ?? WhisperModel.Tiny;

                string modelPath;
                string overridePath = _whisperModelPath?.Value ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) {
                    modelPath = overridePath;
                } else {
                    string modelsDir = DirectoriesManager.GetFullDirectoryPath("models");
                    if (string.IsNullOrEmpty(modelsDir))
                        throw new InvalidOperationException(
                            "Blish HUD did not provide the 'models' directory path. " +
                            "Check that manifest.json lists \"models\" in directories_provided.");
                    modelPath = await ModelManager.EnsureModelAsync(
                        language, size, modelsDir,
                        status => { if (_cornerIcon != null) _cornerIcon.IconName = status; },
                        ct);
                }

                ct.ThrowIfCancellationRequested();

                _transcriptionService = new TranscriptionService(modelPath, language);

                if (_cornerIcon != null) {
                    bool hasMic = AudioRecorder.HasMicrophoneDevice();
                    _cornerIcon.Icon     = hasMic ? _texReady : _texNotReady;
                    _cornerIcon.IconName = hasMic
                        ? "Vox Tyria: Ready"
                        : "Vox Tyria: No microphone detected";
                }
            } catch (OperationCanceledException) {
                // Settings changed again or module unloading — silently exit.
            } catch (Exception ex) {
                Logger.Error(ex, "Failed to load Whisper model.");
                if (_cornerIcon != null) {
                    _cornerIcon.Icon     = _texNotReady;
                    _cornerIcon.IconName = "Vox Tyria: Model error — check log";
                }
                ScreenNotification.ShowNotification(
                    $"Vox Tyria: Failed to load model — {ex.GetType().Name}: {ex.Message}",
                    ScreenNotification.NotificationType.Error, null, 10);
            } finally {
                _modelLock.Release();
            }
        }

        private void OnTranscriptionSettingChanged(object sender,
            ValueChangedEventArgs<string> e) {
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
                        ScreenNotification.NotificationType.Warning,
                        null, 5);
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

        private static readonly string[] ChatChannels =
            { "say", "map", "guild", "team", "squad" };

        private static readonly string[] Emotes = {
            "beckon", "bow", "cheer", "cower", "cry", "crossarms", "dance",
            "kneel", "laugh", "no", "point", "ponder", "salute", "shrug", "sit",
            "sleep", "stretch", "surprised", "talk", "threaten", "upset",
            "victory", "wave", "yes", "angry", "beg", "sad", "swoon"
        };

        // Strips leading/trailing punctuation and whitespace so Whisper's
        // added punctuation ("dance!" / "Dance.") doesn't break keyword matching.
        private static string NormalizeForMatch(string s) =>
            s.Trim().TrimStart(' ').ToLowerInvariant().Trim('.', '!', '?', ',', ';', ':', '"', '\'', '-');

        private string ApplyChatTransforms(string text) {
            // Channel prefix: "say hello" → "/say hello"
            // Strip punctuation from the first word only before comparing.
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

            // Emote: "dance" → "/dance"
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
        // Mic toggle handler
        // ─────────────────────────────────────────────────────────────────────

        private void OnMicToggleActivated(object sender, EventArgs e) {
            // Debounce: ignore activations within 200 ms of the last one to
            // prevent accidental double-triggers from noisy keybind devices.
            if (DateTime.UtcNow - _lastToggleTime < DebounceInterval) return;
            _lastToggleTime = DateTime.UtcNow;

            // GW2 focus gate: user-configurable checkbox.
            if (_onlyWhenGw2Focused.Value &&
                !GameService.GameIntegration.Gw2Instance.Gw2HasFocus) return;

            _isRecording = !_isRecording;

            if (_isRecording) {
                _cornerIcon.Icon     = _texRecording;
                _cornerIcon.IconName = "Vox Tyria: Recording\u2026";

                ScreenNotification.ShowNotification(
                    "Vox Tyria: Mic On",
                    ScreenNotification.NotificationType.Green,
                    null,
                    2);

                try {
                    _audioRecorder.StartRecording();
                } catch (Exception ex) {
                    Logger.Warn(ex, "Failed to start recording.");
                    _isRecording = false;
                    bool hasMic = AudioRecorder.HasMicrophoneDevice();
                    _cornerIcon.Icon     = hasMic ? _texReady : _texNotReady;
                    _cornerIcon.IconName = hasMic
                        ? "Vox Tyria: Ready"
                        : "Vox Tyria: No microphone detected";
                    ScreenNotification.ShowNotification(
                        "Vox Tyria: Failed to start microphone. Is a mic connected?",
                        ScreenNotification.NotificationType.Error, null, 5);
                }
            } else {
                _cornerIcon.Icon     = _texReady;
                _cornerIcon.IconName = "Vox Tyria: Ready";

                ScreenNotification.ShowNotification(
                    "Vox Tyria: Mic Off",
                    ScreenNotification.NotificationType.Red,
                    null,
                    2);

                MemoryStream audioStream = _audioRecorder.StopRecording();

                if (audioStream != null) {
                    // Fire-and-forget onto a background thread so Blish HUD's
                    // game loop is never blocked by CPU-bound Whisper inference.
                    Task.Run(() => ProcessAudioAsync(audioStream));
                }
            }
        }
    }
}
