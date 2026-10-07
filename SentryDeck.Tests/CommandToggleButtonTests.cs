using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

/// <summary>
/// The Trim button reports whether its panel is open, so it must still open the panel however it is pressed, as the plain button it replaced did.
/// </summary>
public sealed class CommandToggleButtonTests
{
    [Fact]
    public void Toggle_ThroughUiAutomation_RunsTheCommandAndReportsTheNewState()
    {
        StaThread.Run(() =>
        {
            var (button, panel) = CreateTrimButton();

            var toggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Toggle);
            toggle.Toggle();

            panel.IsOpen.ShouldBeTrue();
            toggle.ToggleState.ShouldBe(ToggleState.On);
        });
    }

    [Fact]
    public void ToggleState_PanelClosedElsewhere_ReportsOff()
    {
        StaThread.Run(() =>
        {
            var (button, panel) = CreateTrimButton();
            var toggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Toggle);
            toggle.Toggle();

            // Esc and the panel's own Cancel button close it without touching this button.
            panel.IsOpen = false;

            toggle.ToggleState.ShouldBe(ToggleState.Off);
        });
    }

    [Fact]
    public void Enter_OnTheFocusedButton_RunsTheCommand()
    {
        StaThread.Run(() =>
        {
            // The plain button that Trim used to be opened the panel on Enter, and the toggle must not lose that.
            var (button, panel) = CreateTrimButton();

            var enter = KeyInput.Raise(button, Key.Enter, Keyboard.KeyDownEvent);

            enter.Handled.ShouldBeTrue();
            panel.IsOpen.ShouldBeTrue();
        });
    }

    // Wired the way the Trim button is: the command switches the state, and the checked state only follows it.
    private static (CommandToggleButton Button, TrimPanel Panel) CreateTrimButton()
    {
        var panel = new TrimPanel();
        var button = new CommandToggleButton { Command = new RelayCommand(() => panel.IsOpen = !panel.IsOpen) };
        button.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(TrimPanel.IsOpen)) { Source = panel, Mode = BindingMode.OneWay });
        return (button, panel);
    }

    private sealed class TrimPanel : ObservableObject
    {
        private bool _isOpen;

        public bool IsOpen
        {
            get => _isOpen;
            set => SetProperty(ref _isOpen, value);
        }
    }
}
