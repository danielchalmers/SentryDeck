using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace SentryDeck;

/// <summary>
/// An ItemsControl that screen readers see as one group holding the controls its items render, such as the camera strip's tiles.
/// Name the group with AutomationProperties.Name.
/// </summary>
/// <remarks>
/// A plain ItemsControl wraps every item in a list item named by the item's ToString(), so each camera tile was announced as "SentryDeck.CameraViewOption" before the tile itself.
/// In this group the tiles are the direct children, so each one is announced once, by its own name.
/// </remarks>
public sealed class ControlGroupItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ControlGroupAutomationPeer(this);

    private sealed class ControlGroupAutomationPeer(ControlGroupItemsControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    }
}
