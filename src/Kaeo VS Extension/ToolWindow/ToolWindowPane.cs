using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using System.Windows;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow;

[Guid("b3c2f6a1-0000-4a6d-9c2f-000000000001")]
public class ToolWindowPaneImpl : ToolWindowPane
{
    public ToolWindowPaneImpl() : base(null)
    {
        this.Caption = "Kaeo Assistant";
        this.Content = new ToolWindowControl();
    }
}
