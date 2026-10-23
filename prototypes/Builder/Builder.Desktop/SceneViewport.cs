using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Builder.Desktop;

/// <summary>Viewport presentation surface with a discoverable keyboard focus target.</summary>
public sealed class SceneViewport : Border
{
    protected override AutomationPeer OnCreateAutomationPeer()=>new SceneViewportPeer(this);

    private sealed class SceneViewportPeer(SceneViewport owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore()=>nameof(SceneViewport);
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Pane;
        protected override bool IsControlElementCore()=>true;
        protected override bool IsContentElementCore()=>true;
    }
}
