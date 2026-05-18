# Localization check

OpenClaw WinUI strings should stay in `src\OpenClaw.Tray.WinUI\Strings\<locale>\Resources.resw`. XAML uses `x:Uid` for static UI text, and runtime strings should go through `LocalizationHelper.GetString(...)` or `LocalizationHelper.Format(...)`.

Run the regular check before changing UI copy:

```powershell
.\scripts\Test-Localization.ps1
```

For a stricter audit that also fails on candidate hard-coded XAML text:

```powershell
.\scripts\Test-Localization.ps1 -StrictHardcodedXaml
```

When adding or changing user-facing text:

1. Add an `x:Uid` to the XAML element that owns the visible text.
2. Add matching keys to every `Resources.resw` file, for example `MyControl.Text` or `MyButton.Content`.
3. Preserve format placeholders like `{0}` in every locale.
4. Keep only true identifiers, URLs, model names, and brand names hard-coded.
5. Re-run the localization check and the tray tests.

The `localization-audit` agentic workflow runs this audit on a schedule and can open a draft PR when it finds localization drift.
