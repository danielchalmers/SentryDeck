using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SentryReplay.Data;

namespace SentryReplay;

/// <summary>
/// Interaction logic for SidebarView.xaml
/// </summary>
public partial class SidebarView : UserControl
{
    public static readonly DependencyProperty FilterTextProperty = DependencyProperty.Register(
        nameof(FilterText),
        typeof(string),
        typeof(SidebarView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty FilteredClipsProperty = DependencyProperty.Register(
        nameof(FilteredClips),
        typeof(IEnumerable),
        typeof(SidebarView));

    public static readonly DependencyProperty SelectedClipProperty = DependencyProperty.Register(
        nameof(SelectedClip),
        typeof(CamClip),
        typeof(SidebarView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty OpenFolderCommandProperty = DependencyProperty.Register(
        nameof(OpenFolderCommand),
        typeof(ICommand),
        typeof(SidebarView));

    public static readonly DependencyProperty ToggleAboutCommandProperty = DependencyProperty.Register(
        nameof(ToggleAboutCommand),
        typeof(ICommand),
        typeof(SidebarView));

    public static readonly DependencyProperty OpenClipFolderCommandProperty = DependencyProperty.Register(
        nameof(OpenClipFolderCommand),
        typeof(ICommand),
        typeof(SidebarView));

    public static readonly DependencyProperty CopyClipPathCommandProperty = DependencyProperty.Register(
        nameof(CopyClipPathCommand),
        typeof(ICommand),
        typeof(SidebarView));

    public static readonly DependencyProperty CopyClipNameCommandProperty = DependencyProperty.Register(
        nameof(CopyClipNameCommand),
        typeof(ICommand),
        typeof(SidebarView));

    public SidebarView()
    {
        InitializeComponent();
        Root.DataContext = this;
    }

    public string FilterText
    {
        get => (string)GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    public IEnumerable FilteredClips
    {
        get => (IEnumerable)GetValue(FilteredClipsProperty);
        set => SetValue(FilteredClipsProperty, value);
    }

    public CamClip SelectedClip
    {
        get => (CamClip)GetValue(SelectedClipProperty);
        set => SetValue(SelectedClipProperty, value);
    }

    public ICommand OpenFolderCommand
    {
        get => (ICommand)GetValue(OpenFolderCommandProperty);
        set => SetValue(OpenFolderCommandProperty, value);
    }

    public ICommand ToggleAboutCommand
    {
        get => (ICommand)GetValue(ToggleAboutCommandProperty);
        set => SetValue(ToggleAboutCommandProperty, value);
    }

    public ICommand OpenClipFolderCommand
    {
        get => (ICommand)GetValue(OpenClipFolderCommandProperty);
        set => SetValue(OpenClipFolderCommandProperty, value);
    }

    public ICommand CopyClipPathCommand
    {
        get => (ICommand)GetValue(CopyClipPathCommandProperty);
        set => SetValue(CopyClipPathCommandProperty, value);
    }

    public ICommand CopyClipNameCommand
    {
        get => (ICommand)GetValue(CopyClipNameCommandProperty);
        set => SetValue(CopyClipNameCommandProperty, value);
    }

    public void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
