# HebrewDictate

Hebrew voice typing for any text field on Windows, in the spirit of `Win+H`. Windows' built-in voice typing does not support Hebrew, so this fills the gap.

Press a hotkey (or click a small floating mic button), speak Hebrew, and the text is typed into whatever window has focus, live, as you speak.

[עברית ← README.md](README.md)

![Mic button states](docs/mic-states.png)

## What it does

- Types Hebrew speech into **any** app (browsers, Word, chat apps, editors), not just one website.
- **Live typing**: words appear while you speak and are corrected as the recognizer refines its guess.
- Global hotkey **Ctrl+Alt+D** (falls back to Ctrl+Alt+H, then Ctrl+Shift+H if taken), or a floating, draggable, always-on-top **mic button** that does not steal focus when clicked.
- **Spoken punctuation**: say "נקודה" `.`, "פסיק" `,`, "נקודתיים" `:`, "סימן שאלה" `?`, "סימן קריאה" `!`, "שורה חדשה" / "ירידת שורה" (Enter), "פסקה חדשה" (two Enters). Disable with `VoiceCommands = false` in the source.
- Lives in the system tray: toggle, show/hide the recognition window, show/hide the floating button, start with Windows, exit.
- No account, no API key, no usage limit imposed by this program.

## How it works

Speech recognition is done by **Chrome's built-in Web Speech API** (`webkitSpeechRecognition`, Google's engine, language `he-IL`). HebrewDictate is a small native Windows program that hosts it and types the result:

```
 microphone ─► Chrome app window (hidden) ──► Google speech service
                   │  page runs SpeechRecognition (he-IL, continuous, interim results)
                   │  HTTP on 127.0.0.1:48731  (random per-run token)
                   ▼
            HebrewDictate.exe  (C#, WinForms, tray icon)
              - RegisterHotKey for the global hotkey
              - long-poll tells the page when to start/stop
              - receives interim/final text, turns spoken commands into punctuation
              - SendInput (Unicode) types into the focused window,
                backspacing and retyping only the part that changed
```

Details worth knowing:

- Chrome runs with its **own profile** (`%LOCALAPPDATA%\HebrewDictate\profile`), separate from your normal browsing, and its window is hidden (not minimized) so it never appears in the taskbar. It is shown only when it needs your attention, such as the first microphone permission prompt.
- The local server only accepts requests with the right `Host` header and a random token that is generated at every start and passed to the page, so other websites cannot make it type into your windows.
- Interim text is typed as it arrives. If you switch to another window mid-sentence, the old text is left alone and the sentence continues in the new window.

## Requirements

- Windows 10/11 (needs the .NET Framework 4.x that ships with Windows)
- Google Chrome
- An internet connection (recognition happens at Google)
- A microphone

## Install and use

1. Download `HebrewDictate-win.zip` from the [Releases](../../releases) page and extract it anywhere (a stable folder such as `%LOCALAPPDATA%\HebrewDictate` works well).
2. Run `HebrewDictate.exe`. A mic icon appears in the tray and a floating mic button on the screen.
3. The first time, a small window asks for microphone access. Click **Allow**; it hides itself afterwards.
4. Click into any text field, then press **Ctrl+Alt+D** or click the floating mic button. Speak. Press again to stop.
5. Right-click the tray icon or the button for the menu, including **Start with Windows**.

The exe is unsigned, so Windows SmartScreen may warn on first run ("More info" → "Run anyway"). You can verify the download against `SHA256.txt` from the release, or build it yourself.

## Build from source

No SDK required. Windows ships a C# compiler:

```bat
build.bat
```

This produces `bin\HebrewDictate.exe` from `src\Dictate.cs` (a single file). Pushing a tag like `v1.0.0` (or running it manually) runs `.github/workflows/build.yml`, which builds on GitHub and uploads the zip as a workflow artifact.

## Privacy

Audio goes from Chrome to Google's speech service, exactly as with voice search in Chrome. HebrewDictate itself sends nothing anywhere: it talks only to Chrome on `127.0.0.1`. It stores a small settings file (floating button position) and the Chrome profile under `%LOCALAPPDATA%\HebrewDictate`. Do not dictate anything you would not say to Google.

## Limitations

- Needs Chrome and internet; there is no offline mode.
- Does not type into windows running as administrator (Windows blocks it), and some apps (games, secure fields) ignore synthetic input.
- Live correction works by backspacing; a few editors with aggressive autocomplete or autocorrect may behave oddly. If so, report the app.
- Hebrew punctuation is not added automatically by the recognizer; use the spoken commands.
- Recognition accuracy and speed are those of Chrome's Hebrew model.
- Tested mostly on Windows 11 with the logic checked in isolation; broader testing is welcome.

## Troubleshooting

- **Nothing is typed**: check that the tray icon is red (listening) and that Chrome has microphone permission (tray menu → *Show/Hide recognition window*).
- **Hotkey does nothing**: another program may own it; the app picks the first free one of Ctrl+Alt+D, Ctrl+Alt+H, Ctrl+Shift+H and shows it in the tray tooltip.
- **Chrome not found**: install Google Chrome (the app looks in the standard Program Files and per-user locations).
- **Port in use**: the app needs `127.0.0.1:48731`; change `Port` in the source and rebuild.

## License

MIT, see [LICENSE](LICENSE).
