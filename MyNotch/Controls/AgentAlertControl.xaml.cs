using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyNotch.Models;
using MyNotch.ViewModels;

namespace MyNotch.Controls;

public sealed partial class AgentAlertControl : UserControl
{
    public AgentNotificationViewModel ViewModel { get; } = new();

    /// <summary>Raised (on the UI thread) when the alert is clicked; payload may carry pid/hwnd to focus.</summary>
    public event EventHandler<AgentPayload>? AlertClicked;

    public AgentAlertControl()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>Must be called on the UI thread.</summary>
    public void Apply(AgentPayload payload) => ViewModel.Apply(payload);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentNotificationViewModel.Status) or nameof(AgentNotificationViewModel.Latest) or null)
        {
            StatusDot.Fill = UiBrushes.ForStatus(ViewModel.Status);
            StatusLabel.Text = UiBrushes.StatusLabel(ViewModel.Status);
        }
        Bindings.Update();
    }

    private void Alert_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = false;

    private void Alert_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.Latest is { } payload) AlertClicked?.Invoke(this, payload);
        e.Handled = true;
    }
}
