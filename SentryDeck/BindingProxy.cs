using System.Windows;

namespace SentryDeck;

/// <summary>
/// Carries the DataContext into resource-scoped bindings (e.g. a grouping
/// <see cref="System.Windows.Data.CollectionViewSource"/> in Window.Resources).
/// Being a <see cref="Freezable"/> lets it inherit the DataContext, which a plain
/// resource cannot.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
