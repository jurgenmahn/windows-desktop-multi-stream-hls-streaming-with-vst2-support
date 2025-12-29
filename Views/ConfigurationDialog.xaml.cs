using System.Windows;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.ViewModels;

namespace AudioProcessorAndStreamer.Views;

public partial class ConfigurationDialog : Window
{
    private readonly ConfigurationViewModel _viewModel;

    public AppConfiguration? ResultConfiguration { get; private set; }
    public List<StreamConfiguration>? ResultStreams { get; private set; }

    public ConfigurationDialog()
    {
        InitializeComponent();
        _viewModel = new ConfigurationViewModel();
        DataContext = _viewModel;
    }

    public void LoadConfiguration(AppConfiguration config, IEnumerable<StreamConfiguration> streams)
    {
        _viewModel.LoadConfiguration(config, streams);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var (config, streams) = _viewModel.GetConfiguration();
        ResultConfiguration = config;
        ResultStreams = streams;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
