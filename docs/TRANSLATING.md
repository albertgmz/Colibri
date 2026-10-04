# Contributing translations

Translations are reviewed GitHub contributions and ship with releases. English is the complete fallback. No runtime language-pack download or import is required. A partial translation is welcome: omit unfinished entries rather than copying English or saving empty values, and describe coverage in the pull request.

## Application

The canonical English catalog is `src/Colibri.App/Resources/Strings.resx`. Create `Strings.<culture>.resx` beside it, for example `Strings.fr.resx` or `Strings.pt-BR.resx`. Keep the RESX headers and only the `<data name="…"><value>…</value></data>` entries you translate. Use a standard .NET culture name and preserve each key exactly. Do not edit generated `Strings.Designer.cs`, add a new designer generator, or translate identifiers and paths.

Preserve every composite-format placeholder, including format and alignment, such as `{0}`, `{1:0.0}`, or `{0,4}`. You may reorder complete placeholders to suit grammar. XML-escape literal `&` and `<`; keep existing escaped braces, keyboard notation, product names, and technical commands meaningful.

Validate before opening a pull request:

```powershell
python scripts/validate-translations.py
python -m unittest discover -s scripts -p test_translation_validation.py
```

The validator rejects unknown or duplicate keys, empty translations, non-string resource entries, invalid culture filenames, and missing/changed placeholders. Missing keys are allowed. A .NET build compiles culture files into standard satellite assemblies; packaging includes their culture directories. No project-file listing is needed for each new culture.

Settings → Appearance → Language offers System default, English, and automatically discovered bundled translations, labeled with their native language names. The choice is saved and takes effect after restarting Colibri. It changes UI culture only; date and number formatting retain their existing culture. Standard .NET lookup tries a requested region, its parent language, then neutral English. With no bundled translation, English is used. Notification text uses the same startup choice before notification services initialize.

Check main/add/details/settings windows, errors, notifications, keyboard navigation, wrapping and long labels after building. Include screenshots or a short coverage note; ask a fluent speaker to review wording. No sample translation is shipped merely to exercise tests: the incomplete French fallback fixture lives only in the test project.

## Browser extension

In the sibling `colibri-browser-integration` repository, English is `public/_locales/en/messages.json`. Add `public/_locales/<browser-locale>/messages.json`, for example `fr` or `pt_BR`, following browser locale naming. Preserve message keys, named `$placeholders$`, and each placeholder's `content` (such as `$1`). Omit unfinished keys. Do not change `default_locale: "en"` or replace `browser.i18n` with app-side language logic.

Run `node scripts/validate-locales.mjs`, `npm run typecheck`, `npm run lint`, and `npm test` in that repository. Its validator checks unknown/empty keys and placeholder preservation. Browser localization selects the browser's locale and falls back through its parent/default English catalog. App language does not override browser language; this feature adds no protocol field or polling.

See the official [.NET satellite resource guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/create-satellite-assemblies) and [browser i18n reference](https://developer.chrome.com/docs/extensions/reference/api/i18n) for naming and fallback behavior.
