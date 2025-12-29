using System.Windows;
using AudioProcessorAndStreamer.ViewModels;

namespace AudioProcessorAndStreamer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SaveStreamsToConfig();
            vm.Dispose();
        }
    }
}
