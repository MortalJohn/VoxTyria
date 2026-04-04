# Vox Tyria

A [Blish HUD](https://blishhud.com/) module that lets you speak into your microphone and have your words typed directly into the Guild Wars 2 chat box — no typing required.

---

## How it works

1. Press your configured **mic toggle key** (default: **F10**) to start recording.
2. Press it again to stop.
3. Your speech is transcribed locally using [OpenAI Whisper](https://github.com/openai/whisper) (via [Whisper.net](https://github.com/sandrohanea/whisper.net)) and injected into the GW2 chat input field.

No audio is sent to any external server — transcription happens entirely on your machine.

---

## Features

| Feature | Details |
|---|---|
| **Local speech-to-text** | Powered by whisper.cpp; runs fully offline after initial model download |
| **Automatic model download** | The required GGML model is downloaded on first use from Hugging Face and cached locally |
| **Voice channel routing** | Say a channel name at the start of your phrase to route it — e.g. *"map incoming at north"* sends `/map incoming at north` |
| **Voice emotes** | Say a single emote word — e.g. *"dance"* — to perform `/dance` in-game |
| **Custom vocabulary** | Bias transcription toward GW2 terms, guild tags, or player names |
| **GW2 focus guard** | Optionally restrict recording to when GW2 is the active window |
| **Multilingual** | Supports English and many other languages via an ISO 639-1 language code |

---

## Settings

All settings are exposed in the Blish HUD settings panel under **Vox Tyria**.

| Setting | Default | Description |
|---|---|---|
| **Mic Toggle Key** | `F10` | Keybind to start / stop recording |
| **Whisper Model** | `Tiny` | `Tiny` (fastest), `Base` (more accurate), or `Small` (best accuracy) |
| **Whisper Model Path (Advanced)** | *(empty)* | Provide an absolute path to use a custom GGML `.bin` model file |
| **Transcription Language** | `en` | ISO 639-1 language code (`en`, `de`, `fr`, `auto`, etc.). Non-English automatically uses the multilingual model |
| **Custom Vocabulary** | GW2 terms | Comma-separated words/phrases used to bias the transcription |
| **Chat Channel Voice Commands** | `on` | Prefix your phrase with a channel name to route to that channel |
| **Voice Emotes** | `on` | Say a single emote word to perform it in-game |
| **Only When GW2 Is Focused** | `on` | Ignore the keybind unless GW2 is the foreground window |

---

## Voice channel commands

When **Chat Channel Voice Commands** is enabled, start your phrase with a supported channel name:

| Say | Sends |
|---|---|
| *"map this is a test"* | `/map this is a test` |
| *"say hello"* | `/say hello` |
| *"squad ready check"* | `/squad ready check` |
| *"whisper John hey"* | `/whisper John hey` |

---

## Corner icon

The module adds a small icon to the Blish HUD corner icon tray:

| Colour | Meaning |
|---|---|
| White | No microphone detected or module loading |
| Green | Ready — waiting for mic toggle |
| Red | Recording in progress |

---

## Requirements

- **Blish HUD** `>= 0.11.8`
- A working microphone
- Windows (x64) — due to native whisper.cpp binaries

---

## Installation

1. Download the latest `.bhm` file from the [Releases](../../releases) page.
2. Place it in your Blish HUD modules folder (`Documents\Guild Wars 2\addons\blishhud\modules\`).
3. Enable **Vox Tyria** in the Blish HUD module list.
4. On first use, the required Whisper model (~40 MB for Tiny) will download automatically.

---

## Model sizes

| Model | VRAM | Relative speed | Notes |
|---|---|---|---|
| Tiny | ~75 MB | Fastest | Good for short chat phrases; default |
| Base | ~145 MB | ~2× slower | Better accuracy |
| Small | ~460 MB | ~4× slower | Best accuracy |

Quantised `Q8_0` versions are used to reduce file size and memory usage without meaningfully impacting quality for short utterances.

---

## Building from source

```
dotnet build -c Release
```

The output `.bhm` file is written to `bin/Release/net472/`.
