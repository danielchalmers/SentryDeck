using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SentryDeck.Tests;

/// <summary>
/// The camera tiles must switch the camera however they are chosen, because only a click ran their command before.
/// </summary>
public sealed class CameraTileButtonTests
{
    [Fact]
    public void Select_ThroughUiAutomation_RunsTheCommand()
    {
        StaThread.Run(() =>
        {
            string selectedView = null;
            var tile = new CameraTileButton
            {
                Command = new RelayCommand<string>(view => selectedView = view),
                CommandParameter = CameraNames.Back,
            };

            var selectionItem = (ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(tile).GetPattern(PatternInterface.SelectionItem);
            selectionItem.Select();

            selectedView.ShouldBe(CameraNames.Back);
            tile.IsChecked.ShouldBe(true);
            selectionItem.IsSelected.ShouldBeTrue();
        });
    }

    [Fact]
    public void Select_ThroughUiAutomationWhileDisabled_Refuses()
    {
        StaThread.Run(() =>
        {
            var ran = false;
            var tile = new CameraTileButton
            {
                Command = new RelayCommand(() => ran = true),
                IsEnabled = false,
            };

            var selectionItem = (ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(tile).GetPattern(PatternInterface.SelectionItem);

            Should.Throw<ElementNotEnabledException>(selectionItem.Select);
            ran.ShouldBeFalse();
        });
    }

    [Fact]
    public void Enter_OnTheFocusedTile_RunsTheCommand()
    {
        StaThread.Run(() =>
        {
            string selectedView = null;
            var tile = new CameraTileButton
            {
                Command = new RelayCommand<string>(view => selectedView = view),
                CommandParameter = CameraNames.LeftRepeater,
            };

            var enter = KeyInput.Raise(tile, Key.Enter, Keyboard.KeyDownEvent);

            enter.Handled.ShouldBeTrue();
            selectedView.ShouldBe(CameraNames.LeftRepeater);
            tile.IsChecked.ShouldBe(true);
        });
    }
}
