# Third-party notices

## Codex Token Glass fork

This distribution is derived from [dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar), revision `17f0ddd`, under the MIT license. Its license and the earlier base's attribution below are preserved.

The interactive material uses `LiquidGlass` 1.0.0 by shayann07, Apache-2.0: [source](https://github.com/shayann07/liquidglass), with license/notice copies in `assets/liquidglass/`.

Rendering uses SkiaSharp 4.153.1, MIT, [source](https://github.com/mono/SkiaSharp). Inter 4.1 is bundled under SIL Open Font License 1.1; see `assets/fonts/LICENSE-Inter.txt`. The settings vector is from Lucide under ISC; see `assets/icons/LICENSE-LUCIDE.txt`.

CodexStatusbar is MIT licensed — see [`LICENSE`](LICENSE). It is a derivative work, and the
attribution below is a condition of the licences it is derived under. GitHub's licence detector
reads `LICENSE` only, which is why this text lives in its own file: keeping the MIT text
uninterrupted is what makes the repository show up as MIT rather than "Other".

## codex-token-overlay — the base

<https://github.com/soleillevant0125/codex-token-overlay> · Copyright (c) 2026 soleillevant0125 · MIT

This project is a **modified redistribution** of it. Reused: the Win32 Codex-window locator and
classifier, the overlay window with `WS_EX_TOOLWINDOW`/`NOACTIVATE`, the window-follow loop, manual
attachment/snapping, theme handling and the settings/tray shell. Its licence text is kept verbatim in
[`LICENSE-codex-token-overlay`](LICENSE-codex-token-overlay), as that licence requires.

## codex-monitor-hud

<https://github.com/LH-03/codex-monitor-hud> · MIT

Design ideas taken from it: read-only access to `state_5.sqlite`'s `threads` table, the
`originator == "Codex Desktop"` root-session filter, and the bounded-tail/incremental JSONL reading
approach.

## codex-model-benchmarks

<https://github.com/zakmandhro/codex-model-benchmarks> · MIT

TPS methodology taken from it: TPS is computed from official token counts over the model's output
window, never from characters.

## Not affiliated

Codex Desktop, ChatGPT and OpenAI are trademarks of OpenAI. This project is a separate companion
process: it does not patch, replace, inject into or hook the Codex binary, and it is not developed,
endorsed or supported by OpenAI. Codex's session-file and IPC formats are internal implementation
details and may change without notice.

## o200k streaming boundaries

The o200k_base pre-tokenization pattern in HostTokenizer.cs is derived from OpenAI tiktoken (MIT): https://github.com/openai/tiktoken/blob/main/tiktoken_ext/openai_public.py . License retained in assets/tiktoken/LICENSE.txt. SharpToken 2.0.6 supplies token counts.
