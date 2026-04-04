using System;
using System.IO;
using NAudio.Wave;

namespace VoxTyria {

    /// <summary>
    /// Manages microphone input via NAudio's WaveInEvent.
    /// Records in 16 kHz / mono / 16-bit PCM — the format Whisper expects,
    /// avoiding any re-sampling step at transcription time.
    ///
    /// Usage:
    ///   Call StartRecording() to begin capture.
    ///   Call StopRecording() to end capture and receive a WAV MemoryStream.
    ///   Always Dispose() this class when the module unloads.
    /// </summary>
    internal sealed class AudioRecorder : IDisposable {

        // ── Whisper-optimal audio format: 16 kHz, mono, 16-bit PCM ──────────
        private static readonly WaveFormat RecordingFormat =
            new WaveFormat(sampleRate: 16000, channels: 1);

        private WaveInEvent  _waveIn;
        private WaveFileWriter _writer;
        private MemoryStream _captureBuffer;

        private bool _isRecording;
        private bool _disposed;

        // ── Mic availability ─────────────────────────────────────────────────

        /// <summary>
        /// Returns true if at least one recording device is present.
        /// Call this before instantiating to drive the CornerIcon state.
        /// </summary>
        public static bool HasMicrophoneDevice() => WaveIn.DeviceCount > 0;

        // ── Recording control ────────────────────────────────────────────────

        /// <summary>
        /// Opens the default recording device and begins capturing audio.
        /// Throws <see cref="InvalidOperationException"/> if already recording
        /// or if no recording devices are available.
        /// </summary>
        public void StartRecording() {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioRecorder));
            if (_isRecording) throw new InvalidOperationException("Already recording.");
            if (WaveIn.DeviceCount == 0)
                throw new InvalidOperationException("No recording devices found.");

            _captureBuffer = new MemoryStream();

            _waveIn = new WaveInEvent {
                WaveFormat  = RecordingFormat,
                DeviceNumber = 0,            // default device
                BufferMilliseconds = 100     // 100 ms chunks — low latency without excess overhead
            };

            _writer = new WaveFileWriter(_captureBuffer, _waveIn.WaveFormat);

            _waveIn.DataAvailable    += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            _isRecording = true;
        }

        /// <summary>
        /// Stops the active recording and returns the captured audio as a
        /// WAV-formatted <see cref="MemoryStream"/> rewound to position 0.
        /// The caller is responsible for disposing the returned stream.
        /// Returns null if no recording was active.
        /// </summary>
        public MemoryStream StopRecording() {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioRecorder));
            if (!_isRecording) return null;

            _isRecording = false;

            // StopRecording() is synchronous — it waits for the final DataAvailable
            // callback to fire before returning, so _writer receives all audio.
            _waveIn.StopRecording();

            return FinalizeStream();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private void OnDataAvailable(object sender, WaveInEventArgs e) {
            if (_writer == null) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e) {
            // Nothing extra needed here — FinalizeStream() handles the flush.
            // If NAudio surfaces a device error, it will be in e.Exception.
        }

        private MemoryStream FinalizeStream() {
            // Flush the WaveFileWriter so the WAV header is written correctly.
            // We must NOT call Dispose() on the writer — WaveFileWriter.Dispose()
            // closes the underlying MemoryStream, making it unreadable afterwards.
            // Setting _writer to null is enough; the GC will finalize it later.
            _writer?.Flush();
            _writer = null;

            MemoryStream result = _captureBuffer;
            _captureBuffer = null;

            result.Position = 0;
            return result;
        }

        private void DisposeWaveIn() {
            if (_waveIn == null) return;
            _waveIn.DataAvailable    -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // IDisposable
        // ─────────────────────────────────────────────────────────────────────

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;

            // If still recording on Dispose (e.g. module forcibly unloaded),
            // stop cleanly so the capture thread does not linger.
            if (_isRecording) {
                _isRecording = false;
                _waveIn?.StopRecording();
            }

            DisposeWaveIn();

            _writer?.Dispose();
            _writer = null;

            _captureBuffer?.Dispose();
            _captureBuffer = null;
        }
    }
}
