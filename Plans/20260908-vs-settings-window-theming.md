# VS Extension Settings Window — Match Visual Studio Theme

## Understanding
The `SettingsWindow` (opened from the tool window's gear button) should look like a native VS dialog in whatever theme the parent devenv uses (dark/light/contrast). It didn't because:
- The XAML was broken (`<vsui:DialogWindow>` opened but `</ui:DialogWindow>` closed) so the project did not compile — VS ran a stale build.
- The code-behind derived from `VsUIDialogWindow`, a class that does not exist anywhere (repo or CVST package).
- The window was shown without an owner (the `wnd.Owner = (Window)HwndSource.FromHwnd(hwndOwner)?.RootVisual` cast was commented out; it would have thrown anyway since the VS shell HWND has no WPF RootVisual).
- Only TextBox/ComboBox picked up VS styles; buttons, tabs, expanders, the DataGrid and the ToolBar rendered with default Aero chrome.

## Approach
- Root element: `Microsoft.VisualStudio.PlatformUI.DialogWindow` (VS-drawn title bar) + `toolkit:Themes.UseVsTheme="True"` (themed text boxes/combos, scrollbars, `ThemedDialogStyleLoader`).
- Window-level implicit styles: `Button`/`CheckBox`/`RadioButton`/`ListBox`/`ListView`/`ComboBoxItem` BasedOn `shell:VsResourceKeys.ThemedDialog*StyleKey`; custom `TabItem`/`Expander` templates and `DataGrid` styles bound via `DynamicResource` to `vsui:EnvironmentColors`/`vsui:ThemedDialogColors` brush keys so live theme switches propagate.
- Replaced the default `ToolBar` with a themed Border command bar; error text uses `ThemedDialogColors.ValidationErrorTextBrushKey`.
- Font pinned to `VsFonts.EnvironmentFontFamilyKey` / `Environment122PercentFontSizeKey` (Segoe UI Variable Text in VS 2022+ themes).
- Code-behind: base class → `DialogWindow`; delete-confirm now uses `VS.MessageBox.ShowConfirm` (themed, VS-parented) instead of un-themed WPF `MessageBox`.
- Opener: `uiShell.GetDialogOwnerHwnd` + `new WindowInteropHelper(wnd).Owner = hwndOwner` so modality/centering behave like native VS dialogs.
- Error surfacing: load/save/init failures show a themed `VS.MessageBox.ShowError` popup with flattened exception detail (auto-save errors report once per failure streak, not per keystroke).

## Steps
- [x] 1. Fix SettingsWindow.xaml — closing tag, implicit themed styles, themed command bar, error text brush
- [x] 2. Fix SettingsWindow.xaml.cs — DialogWindow base, brush/font refs, themed confirm, error popups (load/save/init)
- [x] 3. Fix ToolWindowControl.xaml.cs — owner HWND via WindowInteropHelper; themed ShowError in send catch
- [x] 4. Build extension — 0 errors (remaining warnings pre-existing)
- [ ] 5. Runtime verification in experimental instance — USER: press F5 (or `devenv.exe /rootSuffix Exp` after installing `Kaeo VS Extension\bin\Debug\net48\win-x64\Kaeo VS Extension.vsix` with `VSIXInstaller.exe /quiet /rootSuffix:Exp <vsix>`), open Kaeo Assistant → gear, check dark/light theme + live theme switch, and report any popup text
- [x] 6. Save plan + git commit (Kaeo84)

## Notes for testing
- If the window throws on load, the popup will now say exactly what failed ("There was a problem initializing the settings window...") — send that text back.
- The `Newtonsoft.Json 13.0.3 > 13.0.1` CVST build warning (CVSTBLD002) is pre-existing and unrelated.
