using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;

namespace SentryDeck;

/// <summary>
/// A toggle button for an action that is on or off, such as the Trim button, whose command runs however it is pressed.
/// Bind IsChecked one way to the state the command switches, so screen readers hear whether it is on.
/// </summary>
/// <remarks>
/// A plain ToggleButton runs its command only from a click or Enter.
/// UI Automation's toggle, which screen readers use, only flips its checked state, so the button would claim the trim panel was open while it stayed shut.
/// </remarks>
public sealed class CommandToggleButton : ToggleButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new CommandToggleAutomationPeer(this);

    private void ClickForAutomation() => OnClick();

    // Re-implements only Toggle; the toggle state still comes from ToggleButton's own peer.
    private sealed class CommandToggleAutomationPeer(CommandToggleButton owner) : ToggleButtonAutomationPeer(owner), IToggleProvider
    {
        void IToggleProvider.Toggle()
        {
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }

            owner.ClickForAutomation();
        }
    }
}
