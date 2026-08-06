namespace Kaeo.LlmProxy.Modules;

/// <summary>
/// Optional contract for modules that ship user-facing documentation. The host injects the
/// returned page into the Help tab's Modules section so module help lives alongside the proxy's
/// own documentation. Modules build and own their entire page; the host only appends it.
/// </summary>
public interface IHelpModule
{
    /// <summary>
    /// Builds the module's help page on the UI thread. Return a fully built
    /// <see cref="System.Windows.Forms.TabPage"/> (the host adds it to the Help tab).
    /// </summary>
    System.Windows.Forms.TabPage CreateHelpPage();
}
