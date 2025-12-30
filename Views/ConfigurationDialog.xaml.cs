using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
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

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Allow digits and decimal point (period or comma based on culture)
        var regex = new Regex(@"^[0-9.,]+$");
        e.Handled = !regex.IsMatch(e.Text);
    }
}
