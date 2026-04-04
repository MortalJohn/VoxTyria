using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net.Ggml;

namespace VoxTyria {

    internal enum WhisperModel {
        Tiny,
        Base,
        Small
    }

    /// <summary>
    /// Resolves and auto-downloads the appropriate Whisper GGML model for a
    /// given language, caching it to the module's registered models directory.
    ///
    /// Model selection rules:
    ///   • "en"        → ggml-tiny.en_q8_0.bin  (English-only, fastest)
    ///   • anything else → ggml-tiny_q8_0.bin   (multilingual tiny, used for all other
    ///                                             languages including "auto")
    ///
    /// Tiny+Q8_0: ~3-4x faster than base with acceptable accuracy for short chat phrases.
    /// </summary>
    internal static class ModelManager {

        private const QuantizationType Quantization = QuantizationType.Q8_0;

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns the full path to the required model file, downloading it
        /// from Hugging Face (via Whisper.net's built-in downloader) if it is
        /// not already cached in <paramref name="modelsDirectory"/>.
        ///
        /// Reports status strings via <paramref name="onStatus"/> for corner
        /// icon tooltip updates.  Throws <see cref="OperationCanceledException"/>
        /// if <paramref name="cancellationToken"/> is cancelled mid-download.
        /// </summary>
        public static async Task<string> EnsureModelAsync(
            string language,
            WhisperModel modelSize,
            string modelsDirectory,
            Action<string> onStatus,
            CancellationToken cancellationToken) {

            (GgmlType ggmlType, string fileName) = GetModelSpec(language, modelSize);
            string modelPath = Path.Combine(modelsDirectory, fileName);

            if (File.Exists(modelPath)) return modelPath;

            // Model not cached — download it.
            onStatus?.Invoke($"Vox Tyria: Downloading {fileName}\u2026");

            Directory.CreateDirectory(modelsDirectory);

            // Write to a temp file first; rename only on success so a cancelled
            // or failed download does not leave a corrupt partial file behind.
            string tempPath = modelPath + ".download";
            try {
                using (Stream download = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    ggmlType, Quantization, cancellationToken))
                using (FileStream dest = File.Create(tempPath)) {
                    await download.CopyToAsync(dest, 81920, cancellationToken);
                }
            } catch {
                TryDeleteFile(tempPath);
                throw;
            }

            // Atomic rename so the file either exists complete or not at all.
            File.Move(tempPath, modelPath);
            return modelPath;
        }

        /// <summary>
        /// Returns the <see cref="GgmlType"/> and expected filename for the
        /// given <paramref name="language"/> code.
        /// </summary>
        /// The filename mirrors the convention used by the Whisper.net downloader
        /// (sandrohanea/whisper.net on HuggingFace); e.g. "ggml-base.en_q8_0.bin".
        public static (GgmlType ggmlType, string fileName) GetModelSpec(string language, WhisperModel modelSize) {
            bool en = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
            switch (modelSize) {
                case WhisperModel.Base:
                    return en
                        ? (GgmlType.BaseEn, "ggml-base.en_q8_0.bin")
                        : (GgmlType.Base,   "ggml-base_q8_0.bin");
                case WhisperModel.Small:
                    return en
                        ? (GgmlType.SmallEn, "ggml-small.en_q8_0.bin")
                        : (GgmlType.Small,   "ggml-small_q8_0.bin");
                default: // Tiny
                    return en
                        ? (GgmlType.TinyEn, "ggml-tiny.en_q8_0.bin")
                        : (GgmlType.Tiny,   "ggml-tiny_q8_0.bin");
            }
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private static void TryDeleteFile(string path) {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
