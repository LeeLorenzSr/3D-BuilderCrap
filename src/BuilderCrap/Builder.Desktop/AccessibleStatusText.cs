using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Builder.Desktop;

/// <summary>Visible status/failure text announces without moving focus or changing editor state.</summary>
public sealed class AccessibleStatusText : TextBlock
{
    static AccessibleStatusText() => TextProperty.OverrideMetadata(typeof(AccessibleStatusText),
        new FrameworkPropertyMetadata("", (sender, _) => ((AccessibleStatusText)sender).Announce()));
    public AccessibleStatusText()
    {
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        IsVisibleChanged += (_, _) => { if (IsVisible) Announce(); };
    }
    private void Announce()
    {
        if (!IsLoaded || !IsVisible || string.IsNullOrWhiteSpace(Text)) return;
        var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
            AutomationNotificationProcessing.CurrentThenMostRecent, Text, string.IsNullOrEmpty(Name) ? "editor-status" : Name);
    }
}
