using System.Windows;

namespace AudioProcessorAndStreamer.Views;

public partial class DownloadProgressDialog : Window
{
    public DownloadProgressDialog()
    {
        InitializeComponent();
    }

    public void UpdateProgress(double percentage)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = percentage;
            PercentageText.Text = $"{percentage:F0}%";

            if (percentage >= 100)
            {
                StatusText.Text = "Download complete!";
            }
        });
    }

    public void SetStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = status;
        });
    }
}
