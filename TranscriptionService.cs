using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace VoxTyria {

    /// <summary>
    /// Wraps Whisper.net to transcribe WAV audio into text.
    ///
    /// A single <see cref="WhisperFactory"/> is kept alive for the lifetime of
    /// the service — loading the model once is expensive (100 ms – several
    /// seconds depending on model size) so we amortise it across all calls.
    ///
    /// <see cref="TranscribeAudioAsync"/> builds a fresh processor per call so
    /// the per-call <paramref name="vocabularyPrompt"/> is applied each time
    /// (Whisper.net processors are not thread-safe and must not be shared).
    /// </summary>
    internal sealed class TranscriptionService : IDisposable {

        private readonly WhisperFactory _factory;
        private readonly string         _language;
        private bool _disposed;

        // ── Construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Loads the GGML model at <paramref name="modelPath"/> and holds it
        /// ready for transcription.  This constructor is synchronous and may
        /// take a moment on large models — call from a background thread or
        /// during module load (not from Update()).
        /// </summary>
        /// <param name="modelPath">
        ///   Absolute path to a whisper.cpp-compatible GGML model file,
        ///   e.g. <c>C:\models\ggml-base.en.bin</c>.
        ///   Download models from https://huggingface.co/ggerganov/whisper.cpp
        /// </param>
        /// <param name="language">
        ///   ISO 639-1 language code (e.g. "en", "es", "de") or "auto" for
        ///   automatic language detection.  Must match the model type: English-only
        ///   models (<c>base.en</c>) only support "en".
        /// </param>
        public TranscriptionService(string modelPath, string language = "en") {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("Model path must not be empty.", nameof(modelPath));
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Whisper model file not found.", modelPath);

            _language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
            _factory  = WhisperFactory.FromPath(modelPath);
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Transcribes the WAV audio in <paramref name="audioStream"/> and
        /// returns the recognised text, trimmed.
        ///
        /// The <paramref name="vocabularyPrompt"/> is passed to Whisper as an
        /// initial prompt, biasing the model toward the supplied vocabulary
        /// (GW2 slang, player names, etc.).
        ///
        /// The stream must be in WAV format and rewound to position 0.
        /// The caller retains ownership of the stream and must dispose it.
        /// </summary>
        public async Task<string> TranscribeAudioAsync(
            MemoryStream audioStream,
            string vocabularyPrompt) {

            if (_disposed) throw new ObjectDisposedException(nameof(TranscriptionService));
            if (audioStream == null) throw new ArgumentNullException(nameof(audioStream));

            // Build a one-shot processor with the per-call vocabulary prompt.
            // Processors are lightweight wrappers — the expensive model weights
            // live in _factory and are reused automatically.
            WhisperProcessorBuilder builder = _factory.CreateBuilder()
                .WithNoContext()      // ignore context from prior segments; each call is independent
                .WithSingleSegment(); // GW2 chat messages are short; single-segment is faster

            // Apply language: "auto" enables Whisper's built-in language detection;
            // everything else is passed as a 2-letter ISO 639-1 code.
            if (string.Equals(_language, "auto", StringComparison.Ordinal))
                builder = builder.WithLanguageDetection();
            else
                builder = builder.WithLanguage(_language);

            if (!string.IsNullOrWhiteSpace(vocabularyPrompt))
                builder = builder.WithPrompt(vocabularyPrompt);

            using WhisperProcessor processor = builder.Build();

            audioStream.Position = 0;

            var result = new StringBuilder();
            await foreach (SegmentData segment in processor.ProcessAsync(audioStream)) {
                result.Append(segment.Text);
            }

            string text = result.ToString().Trim();

            // Whisper emits bracketed tokens like [BLANK_AUDIO], [MUSIC], [NOISE]
            // for silent or non-speech input. Treat these as empty so the caller
            // doesn't inject placeholder text into chat.
            if (text.StartsWith("[") && text.EndsWith("]"))
                return string.Empty;

            return text;
        }

        // ── IDisposable ──────────────────────────────────────────────────────

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _factory?.Dispose();
        }
    }
}
