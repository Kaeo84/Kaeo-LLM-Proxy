namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Optional UI contract for modules that render configuration and help pages inside a host UI.
/// Kept separate from <see cref="IKaeoModule"/> so the core module contract stays UI-framework
/// agnostic and portable (net10.0): a headless host on Linux/macOS loads modules that never
/// implement this interface, while the Windows WinForms host casts the returned pages to
/// <c>System.Windows.Forms.TabPage</c>.
/// </summary>
/// <remarks>
/// The return types are <see cref="object"/> at this single boundary by design. The earlier
/// "WinForms by design" decision (a TabPage return type on <see cref="IKaeoModule"/>) is
/// superseded now that a second UI technology — the cross-platform web dashboard — exists.
/// Modules implement this interface only in their Windows build (KAEO_WINDOWS_UI); the returned
/// objects are WinForms pages that the Windows host adds to its tab controls. A module with no
/// help page returns <see langword="null"/> from <see cref="CreateHelpPage"/>.
/// </remarks>
public interface IModuleUi
{
    /// <summary>
    /// Builds the module's configuration page on the UI thread. The Windows host casts the
    /// result to <c>System.Windows.Forms.TabPage</c> and adds it to its tab control.
    /// </summary>
    object CreateConfigPage();

    /// <summary>
    /// Builds the module's help page on the UI thread, or returns <see langword="null"/> when the
    /// module ships no help. The Windows host casts a non-null result to
    /// <c>System.Windows.Forms.TabPage</c> and adds it to the Help tab.
    /// </summary>
    object? CreateHelpPage();
}