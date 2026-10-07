using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace SentryDeck;

/// <summary>
/// A tile in the camera strip under the video: a radio button that runs its command however it is chosen, not only when clicked.
/// </summary>
/// <remarks>
/// A plain RadioButton runs its command only from a click.
/// Screen readers and other UI Automation clients choose it through its selection pattern, which only checks it, so the tile lit up while the enlarged camera stayed the same.
/// It also ignores Enter, so a keyboard user who tabbed to a tile had no way to choose it (Space is the app-wide play and pause shortcut).
/// </remarks>
public sealed class CameraTileButton : RadioButton
{
    static CameraTileButton()
    {
        // Buttons accept Enter by default, and RadioButton turns that off again.
        KeyboardNavigation.AcceptsReturnProperty.OverrideMetadata(typeof(CameraTileButton), new FrameworkPropertyMetadata(true));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CameraTileAutomationPeer(this);

    private void ClickForAutomation() => OnClick();

    // Re-implements only Select; the other selection members keep RadioButton's behavior.
    private sealed class CameraTileAutomationPeer(CameraTileButton owner) : RadioButtonAutomationPeer(owner), ISelectionItemProvider
    {
        void ISelectionItemProvider.Select()
        {
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }

            owner.ClickForAutomation();
        }
    }
}
