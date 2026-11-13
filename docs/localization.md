# Localization

Developer: Alex Cooper

Modstoke and its mods show text in the player's language. The language follows the game's own language
setting, so a player who runs Sons of the Forest in Russian gets Modstoke and every translated mod in Russian
with nothing to configure.

## For mod authors

Put one JSON file per language in a `lang` folder next to your mod's manifest, named by language code:

```text
Modstoke/Mods/MyMod/
  openacai.mod.json
  MyMod.dll
  lang/
    en.json
    ru.json
    zh-Hans.json
```

Each file maps a key to its text:

```json
{
  "greeting": "Hello, {name}!",
  "menu": {
    "open": "Open the workbench",
    "close": "Close"
  },
  "logs": { "one": "{count} log", "other": "{count} logs" }
}
```

Read it in code through `Modstoke.Localization.Strings`, with your mod's id:

```csharp
using Modstoke.Localization;

var text = Strings.For("author.mymod");

label.text = text["menu.open"];
status.text = text.Get("greeting", ("name", player.Name));
count.text = text.Count("logs", carried);
```

The Loader finds the `lang` folder from the mod's package; there is nothing to register.
`Strings.For(typeof(MyMod).Assembly)` gives the same text without writing the id.

### Rules of the file format

- **Keys.** Nested objects join their keys with dots, so `"menu": { "open": ... }` is `menu.open`.
- **Placeholders.** `{name}` is filled from the values you pass. A number or date can carry a format that
  follows the language, such as `{distance:N1}`. Write `{{` for a literal brace. A placeholder with no value
  stays visible, which makes a typo easy to spot.
- **Counted text.** An object made of plural forms is one counted text. `Count` picks the form the language
  needs and fills `{count}`. English, German, Spanish, Italian, Swedish, Finnish and Turkish use `one` and
  `other`. French and Portuguese use `one` for 0 and 1. Russian and Polish use `one`, `few`, `many` and
  `other`. Czech uses `one`, `few` and `other`. Chinese, Japanese and Korean use `other` only. Every counted
  text needs `other`.
- **Notes.** Keys starting with `_` are ignored, and `//` comments are allowed, so a file can carry notes for
  translators.
- **Encoding.** UTF-8.

### Fallback

A text is looked up in the player's language first, then in English. A regional code falls back to its
language first (`pt-BR` to `pt`). Start with `en.json` and add languages one file at a time: anything not
translated yet shows in English. A key with no text anywhere is shown as the key and logged once.

### Redrawing when the language changes

`Languages.Changed` is raised on the game thread after the language changes. Text read at that moment is
already in the new language:

```csharp
Languages.Changed += Redraw;
```

### Shipping the files inside the DLL

Language files can be embedded in the mod's assembly as `lang/<code>.json` instead of, or as well as, the
folder. Files in the folder win over embedded ones.

```xml
<EmbeddedResource Include="lang/*.json" LogicalName="lang/%(Filename).json" />
```

## Language codes

The game ships sixteen languages, and Modstoke uses the same codes with one exception: the game's Simplified
Chinese code `zh` is `zh-Hans` here.

| Code | Language | Code | Language |
|---|---|---|---|
| `en` | English | `ru` | Russian |
| `de` | German | `tr` | Turkish |
| `fr` | French | `sv` | Swedish |
| `es` | Spanish | `fi` | Finnish |
| `it` | Italian | `ja` | Japanese |
| `pt` | Portuguese | `ko` | Korean |
| `pl` | Polish | `zh-Hans` | Chinese (Simplified) |
| `cs` | Czech | `zh-Hant` | Chinese (Traditional) |

A mod may ship any other code as well, for example `uk.json`. It is used when the player sets that language
in the Loader settings.

## For players and translators

- **Choosing a language.** Modstoke follows the game's language. To use another one, add `language = ru` to
  `Modstoke/UserData/openacai-loader.settings.cfg`; `language = auto` follows the game again.
- **Adding or correcting a translation.** Drop a file into `Modstoke/UserData/lang/<mod id>/<code>.json`. It
  only needs the keys you want to change or add, and it wins over the mod's own file. The Loader's own text
  uses the id `modstoke`.
- **Seeing your changes.** Run `language reload` in the Modstoke console to read the files again without
  restarting the game. `language` alone shows the language in use.

## The Loader's own text

The Loader's text lives in `src/Modstoke.Loader/Localization/lang/` and is embedded in `Modstoke.Loader.dll`.
`en.json` is the source: new text is written there first, then translated. The other fifteen files were
translated from it and welcome review by native speakers.
