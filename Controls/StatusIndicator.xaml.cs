using System.Windows;
using System.Windows.Controls;

namespace AudioProcessorAndStreamer.Controls;

public partial class StatusIndicator : UserControl
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(StatusIndicator),
            new PropertyMetadata(false));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public StatusIndicator()
    {
        InitializeComponent();
    }
}
