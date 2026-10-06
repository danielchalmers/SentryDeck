using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace SentryDeck;

/// <summary>
/// An ItemsControl whose items are only drawn, such as the seek bar's chunk-seam and gap ticks, which screen readers skip.
/// </summary>
/// <remarks>
/// A plain ItemsControl puts every item into the UI Automation tree under the item's value, so the seek bar read out its ticks as raw fractions like "0.0936324907".
/// WPF has no attached property that takes an element out of that tree, and returning no automation peer only makes WPF fall back to the ItemsControl's own.
/// So this peer has no children and stays out of the control and content views that screen readers walk.
/// </remarks>
public sealed class DecorativeItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeAutomationPeer(this);

    private sealed class DecorativeAutomationPeer(DecorativeItemsControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetChildrenCore() => null;

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
