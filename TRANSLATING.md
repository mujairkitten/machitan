# Translating Machitan

Machitan's text lives in one place: `Translations/`. English is in `Strings.resx`, and each translation is one more file next to it, named after its language code:

| File | Language |
|---|---|
| `Translations/Strings.resx` | English (the original; every other language falls back to it) |
| `Translations/Strings.id.resx` | Bahasa Indonesia |

People pick the language in Settings > General > Language ("Windows' language" follows Windows' display language). The change shows the next time Machitan starts.

## Add a language

1. Copy `Translations/Strings.resx` to `Translations/Strings.<code>.resx`, where `<code>` is the language's code: `de` for German, `pt-BR` for Brazilian Portuguese, `ja` for Japanese, and so on.
2. Translate every `<value>`. Leave the `name="..."` of each entry as it is.
3. Add your language to the list in `Core/Languages.cs`, with its name written in that language:

   ```csharp
   ("de", "Deutsch"),
   ```

4. Build, choose your language in Settings > General > Language, restart Machitan, and look through every page of Settings, Start, the bar's menus, the Win+X menu, Dynaland and the flyouts.

Any text editor works for the `.resx` file. In Visual Studio, the resource editor shows the English and your file side by side if you open both.

## Improve a translation

Edit the `<value>` in that language's file. Nothing else changes.

## Rules for the text

- **Keep `{0}`, `{1}` ...** exactly as they are. Machitan fills them in (an app's name, a number, a place). You may move them within the sentence. Entries that have them say so in their `<comment>`.
- **Keep one `&` in the Win+X menu's entries** (`WinX...`): the letter after it is the key that picks the entry, and it is underlined. Put it before a letter of your text that no other entry of the same menu uses (the main menu, or "Shut down or sign out" and its entries). `&&` shows a plain `&`.
- **Keep the Windows words people know.** When Windows itself, in your language, has a word for something (Start, taskbar, Pin to taskbar, Run as administrator, File Explorer), use Windows' word, so Machitan reads like the rest of the PC.
- **Short and plain.** Labels are short; descriptions say one thing. If your language needs more words, that is fine, but check that the text still fits on screen.
- **No new text, only translation.** Do not add facts or claims that the English does not have.
- **Names stay names:** Machitan, Dynaland, Open-Meteo, Wi-Fi, Ethernet, Win+N and other key names are not translated.
- Write the file as UTF-8 (the `.resx` files start that way; keep it).

## What is not translated

- `diagnostics.log`: it stays in English, so anyone can help read it.
- The names apps give themselves, their windows and their tray icons: those come from the apps.
- Dates and times: they follow Windows' regional format (Settings > Time & language > Language & region), as everywhere in Windows.

## For developers: adding text

Never write text people will see as a string in the code. Instead:

1. Add an entry to `Translations/Strings.resx` (Visual Studio's resource editor updates `Strings.Designer.cs` when you save; with another editor, add the matching property to `Strings.Designer.cs` by hand, in the same form as the others).
2. Add the same entry to every `Strings.<code>.resx`. If you cannot translate it, copy the English and say so in the pull request, so a translator can pick it up.
3. Use it as `Strings.YourName`. For text with a value in it, put `{0}` in the entry and use `Fmt.F(Strings.YourName, value)`, never `+`, because other languages put the value elsewhere in the sentence.

Log messages (`Log.Info`, `Log.Warn`, `Log.Error`) stay in English and are not in the string table.

## Pull requests

- One language per pull request, please.
- Title: `Translation: <language> (<code>)`, for example `Translation: Deutsch (de)`.
- Say in the description whether you are a native speaker, and list any entries you were unsure about.
- Screenshots of Settings and Start in your language help the review.
- A check before you send it: the number of `<data name=` entries in your file is the same as in `Strings.resx`, and every `{0}` in the English is also in your text.
