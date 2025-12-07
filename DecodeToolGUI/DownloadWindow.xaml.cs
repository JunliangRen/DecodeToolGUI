using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DecodeToolLib;

namespace DecodeToolGUI
{
    public partial class DownloadWindow : Window
    {
        // Cancellation token source for download cancellation.
        private CancellationTokenSource? _cts;

        public DownloadWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Start the download when the user clicks the Download button.
        /// Provides more detailed status updates during the process.
        /// </summary>
        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            StatusText.Text = "Connecting to GitHub...";
            DownloadProgressBar.Value = 0;

            _cts = new CancellationTokenSource();

            // Progress reporter updates UI with numeric progress and stage-aware status text.
            var progress = new Progress<double>(p =>
            {
                // Update progress bar
                DownloadProgressBar.Value = p;

                // Show more granular status based on progress value
                if (p <= 0.0)
                {
                    StatusText.Text = "Connecting to GitHub...";
                }
                else if (p < 100.0)
                {
                    StatusText.Text = $"Downloading asset... {p:F2}%";
                }
                else
                {
                    // When download reaches 100%, extraction inside the library may follow.
                    StatusText.Text = "Download complete. Extracting files...";
                }
            });

            try
            {
                // Call the library method to download and extract. It returns the release version string.
                // Initial status is already "Connecting...". The progress reporter will update during download.
                string version = await ReleaseDownloader.DownloadAndExtractLatestAsync(
                    assetKeyword: "decode_suite_full",
                    targetFolderName: "binary",
                    progress: progress,
                    cancellationToken: _cts.Token);

                // Show final completion message on UI thread
                DownloadProgressBar.Value = 100;
                StatusText.Text = "Finished.";
                MessageBox.Show(this, $"Download and extraction completed. Version: {version}", "Completed", MessageBoxButton.OK, MessageBoxImage.Information);

                this.Close();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Download cancelled.";
                MessageBox.Show(this, "Download cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error during download.";
                MessageBox.Show(this, $"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                // Allow retry
                StartButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>
        /// Cancel or close the download window.
        /// Requests cancellation if a download is active; otherwise closes the window.
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                // Request cancellation
                _cts.Cancel();
                CancelButton.IsEnabled = false;
                StatusText.Text = "Cancelling...";
            }
            else
            {
                this.Close();
            }
        }
    }
}