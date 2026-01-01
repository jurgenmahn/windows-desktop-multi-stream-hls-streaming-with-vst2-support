using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioProcessorAndStreamer.Models;
using AudioProcessorAndStreamer.ViewModels;

namespace AudioProcessorAndStreamer.Views;

public partial class ConfigurationDialog : Window
{
    private readonly ConfigurationViewModel _viewModel;
    private bool _suppressFormatEvents;

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
        UpdateContainerFormatOptions();
    }

    private void UpdateContainerFormatOptions()
    {
        // Enable/disable MPEG-TS option based on current stream format
        if (_viewModel.SelectedStream != null)
        {
            MpegTsOption.IsEnabled = _viewModel.SelectedStream.StreamFormat != StreamFormat.Dash;
        }
    }

    private void StreamsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateContainerFormatOptions();
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

    private void StreamFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormatEvents || _viewModel.SelectedStream == null) return;

        // When DASH is selected, auto-select fMP4 (DASH requires fMP4)
        if (_viewModel.SelectedStream.StreamFormat == StreamFormat.Dash)
        {
            _suppressFormatEvents = true;
            _viewModel.SelectedStream.ContainerFormat = ContainerFormat.Fmp4;
            _suppressFormatEvents = false;
        }

        // Update container format options (enable/disable MPEG-TS based on stream format)
        UpdateContainerFormatOptions();
    }

    private void ContainerFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormatEvents || _viewModel.SelectedStream == null) return;

        // When MPEG-TS is selected and DASH was active, switch to HLS
        // (MPEG-TS is not compatible with DASH)
        if (_viewModel.SelectedStream.ContainerFormat == ContainerFormat.MpegTs &&
            _viewModel.SelectedStream.StreamFormat == StreamFormat.Dash)
        {
            _suppressFormatEvents = true;
            _viewModel.SelectedStream.StreamFormat = StreamFormat.Hls;
            _suppressFormatEvents = false;
        }
    }

    private void SegmentDuration_LostFocus(object sender, RoutedEventArgs e)
    {
        // Validate and clamp segment duration to valid range (1-30 seconds)
        if (int.TryParse(SegmentDurationTextBox.Text, out int value))
        {
            _viewModel.HlsSegmentDuration = Math.Clamp(value, 1, 30);
        }
        else
        {
            _viewModel.HlsSegmentDuration = 4; // Default value
        }
        SegmentDurationTextBox.Text = _viewModel.HlsSegmentDuration.ToString();
    }

    private void PlaylistSize_LostFocus(object sender, RoutedEventArgs e)
    {
        // Validate and clamp playlist size to valid range (2-30 segments)
        if (int.TryParse(PlaylistSizeTextBox.Text, out int value))
        {
            _viewModel.HlsPlaylistSize = Math.Clamp(value, 2, 30);
        }
        else
        {
            _viewModel.HlsPlaylistSize = 5; // Default value
        }
        PlaylistSizeTextBox.Text = _viewModel.HlsPlaylistSize.ToString();
    }
}
