using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Kaeo.LlmProxy.VSExtension.Diagnostics
{
    /// <summary>
    /// Dockable "Kaeo Debug" tool window. Plain text surface for everything the extension logs
    /// while a debugger is attached; reachable from the chat toolbar icon or View &gt; Other Windows.
    /// </summary>
    [Guid("b3c2f6a1-0000-4a6d-9c2f-000000000003")]
    public class DebugOutputPane : ToolWindowPane
    {
        public DebugOutputPane() : base(null)
        {
            Caption = "Kaeo Debug";
            Content = new DebugOutputControl();
        }
    }
}
