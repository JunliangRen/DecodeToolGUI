using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace DecodeToolGUI
{
    /// <summary>
    /// Interaction logic for MainWindow.
    /// </summary>
    public partial class MainWindow : Window
    {
        public ICommand OpenDownloadCommand { get; }

        // TBC export related commands and state
        public ICommand AddTbcFilesCommand { get; }
        public ICommand RemoveSelectedTbcCommand { get; }
        public ICommand StartExportCommand { get; }
        public ICommand CancelExportCommand { get; }

        public ObservableCollection<string> TbcFiles { get; } = new ObservableCollection<string>();
        public string? SelectedTbcFile { get; set; }

        private CancellationTokenSource? _exportCts;
        private Process? _runningProcess;

        public MainWindow()
        {
            InitializeComponent();

            DataContext = this;

            OpenDownloadCommand = new AsyncRelayCommand(ExecuteOpenDownloadAsync, _ => true);

            AddTbcFilesCommand = new RelayCommand(_ => ExecuteAddTbcFiles());
            RemoveSelectedTbcCommand = new RelayCommand(_ => ExecuteRemoveSelectedTbc(), _ => SelectedTbcFile != null);
            StartExportCommand = new AsyncRelayCommand(_ => ExecuteStartExportAsync(), _ => TbcFiles.Count > 0 && _exportCts == null);
            CancelExportCommand = new RelayCommand(_ => ExecuteCancelExport(), _ => _exportCts != null);

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            await UpdateLocalVersionLabelAsync().ConfigureAwait(false);
        }

        private async Task ExecuteOpenDownloadAsync(object? _)
        {
            var win = new DownloadWindow { Owner = this };

            try
            {
                IsEnabled = false;
                var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                void ClosedHandler(object? s, EventArgs e) => tcs.TrySetResult(null);

                win.Closed += ClosedHandler;
                win.Show();
                await tcs.Task.ConfigureAwait(false);
                win.Closed -= ClosedHandler;
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => IsEnabled = true);
            }

            await UpdateLocalVersionLabelAsync().ConfigureAwait(false);
        }

        private async Task UpdateLocalVersionLabelAsync()
        {
            try
            {
                var version = await Task.Run(GetLatestLocalVersion).ConfigureAwait(false) ?? "null";
                await Dispatcher.InvokeAsync(() => LocalVersionMenuItem.Header = $"Decode Suite Version: {version}");
            }
            catch
            {
                await Dispatcher.InvokeAsync(() => LocalVersionMenuItem.Header = "Decode Suite Version: null");
            }
        }

        private static string? GetLatestLocalVersion()
        {
            var cwd = Directory.GetCurrentDirectory();
            var binaryDir = Path.Combine(cwd, "binary");
            if (!Directory.Exists(binaryDir))
                return null;

            var di = new DirectoryInfo(binaryDir);
            var subdirs = di.GetDirectories();
            if (subdirs == null || subdirs.Length == 0)
                return null;

            var latest = subdirs.OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();
            return latest?.Name;
        }

        #region TBC Export command handlers

        private void ExecuteAddTbcFiles()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select .tbc files",
                Filter = "TBC files (*.tbc)|*.tbc|All files (*.*)|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog(this) == true)
            {
                foreach (var f in dlg.FileNames)
                {
                    if (!TbcFiles.Contains(f))
                        TbcFiles.Add(f);
                }
            }
        }

        private void ExecuteRemoveSelectedTbc()
        {
            if (SelectedTbcFile != null && TbcFiles.Contains(SelectedTbcFile))
            {
                TbcFiles.Remove(SelectedTbcFile);
                SelectedTbcFile = null;
            }
        }

        private void ExecuteCancelExport()
        {
            if (_exportCts != null && !_exportCts.IsCancellationRequested)
            {
                _exportCts.Cancel();
                try { _runningProcess?.Kill(entireProcessTree: true); } catch { }
            }
        }

        private async Task ExecuteStartExportAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                ExportStatusText.Text = "Preparing export...";
                ExportProgressBar.IsIndeterminate = true;
            });

            string outputPath = string.Empty;
            await Dispatcher.InvokeAsync(() => outputPath = OutputPathTextBox.Text?.Trim() ?? string.Empty);

            if (string.IsNullOrEmpty(outputPath))
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                outputPath = Path.Combine(desktop, $"tbc_export_{DateTime.Now:yyyyMMdd_HHmmss}.mkv");
                await Dispatcher.InvokeAsync(() => OutputPathTextBox.Text = outputPath);
            }

            // Read selected options from UI (only from predefined lists)
            string profile = string.Empty;
            string chromaDecoder = string.Empty;
            string chromaGain = string.Empty;
            string chromaPhase = string.Empty;
            string lumaNr = string.Empty;
            string transformThreshold = string.Empty;
            bool ntscPhaseComp = false;
            bool lumaOnly = false;
            bool includeAudio = false;

            await Dispatcher.InvokeAsync(() =>
            {
                profile = (ProfileComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "default";
                chromaDecoder = (ChromaDecoderComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "auto";
                chromaGain = (ChromaGainComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? string.Empty;
                chromaPhase = (ChromaPhaseComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? string.Empty;
                lumaNr = (LumaNrComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? string.Empty;
                transformThreshold = (TransformThresholdComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? string.Empty;
                ntscPhaseComp = NtscPhaseCompCheckBox.IsChecked ?? false;
                lumaOnly = LumaOnlyCheckBox.IsChecked ?? false;
                includeAudio = AddAudioCheckBox.IsChecked ?? true;
            });

            var exePath = await Task.Run(FindTbcVideoExportExe).ConfigureAwait(false);
            if (exePath == null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ExportStatusText.Text = "tbc-video-export.exe not found under ./binary.";
                    ExportProgressBar.IsIndeterminate = false;
                });
                MessageBox.Show(this, "tbc-video-export.exe not found in local binary folders. Please download the Decode Suite first.", "Executable not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var argsList = new System.Collections.Generic.List<string>();

            // profile
            if (!string.IsNullOrWhiteSpace(profile) && !string.Equals(profile, "default", StringComparison.OrdinalIgnoreCase))
            {
                argsList.Add("--profile");
                argsList.Add(profile);
            }

            // chroma decoder
            if (!string.IsNullOrWhiteSpace(chromaDecoder) && !string.Equals(chromaDecoder, "auto", StringComparison.OrdinalIgnoreCase))
            {
                argsList.Add("--chroma-decoder");
                argsList.Add(chromaDecoder);
            }

            // chroma-gain
            if (!string.IsNullOrWhiteSpace(chromaGain))
            {
                argsList.Add("--chroma-gain");
                argsList.Add(chromaGain);
            }

            // chroma-phase
            if (!string.IsNullOrWhiteSpace(chromaPhase))
            {
                argsList.Add("--chroma-phase");
                argsList.Add(chromaPhase);
            }

            // luma-nr
            if (!string.IsNullOrWhiteSpace(lumaNr))
            {
                argsList.Add("--luma-nr");
                argsList.Add(lumaNr);
            }

            // transform-threshold
            if (!string.IsNullOrWhiteSpace(transformThreshold))
            {
                argsList.Add("--transform-threshold");
                argsList.Add(transformThreshold);
            }

            // flags
            if (ntscPhaseComp) argsList.Add("--ntsc-phase-comp");
            if (lumaOnly) argsList.Add("--luma-only");

            // audio
            argsList.Add(includeAudio ? "--audio" : "--no-audio");

            // output
            argsList.Add("-o");
            argsList.Add(outputPath);

            // inputs
            argsList.AddRange(TbcFiles.Select(f => f));

            var args = string.Join(" ", argsList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            _exportCts = new CancellationTokenSource();

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ExportStatusText.Text = "Starting export...";
                    ExportProgressBar.IsIndeterminate = true;
                });

                var exitCode = await RunProcessAsync(exePath, args, _exportCts.Token).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    ExportProgressBar.IsIndeterminate = false;
                    ExportProgressBar.Value = 100;
                    ExportStatusText.Text = exitCode == 0 ? "Export completed." : $"Export failed (code {exitCode}).";
                });

                if (exitCode == 0)
                {
                    MessageBox.Show(this, $"Export completed: {outputPath}", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this, $"Export failed with exit code {exitCode}. See console output for details.", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ExportStatusText.Text = "Export cancelled.";
                    ExportProgressBar.IsIndeterminate = false;
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ExportStatusText.Text = "Error during export.";
                    ExportProgressBar.IsIndeterminate = false;
                });
                MessageBox.Show(this, $"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _exportCts?.Dispose();
                _exportCts = null;
                _runningProcess = null;
            }
        }

        #endregion

        #region Helpers

        private static string? FindTbcVideoExportExe()
        {
            var cwd = Directory.GetCurrentDirectory();
            var binaryDir = Path.Combine(cwd, "binary");
            if (!Directory.Exists(binaryDir))
                return null;

            var files = Directory.EnumerateFiles(binaryDir, "tbc-video-export.exe", SearchOption.AllDirectories);
            return files.FirstOrDefault();
        }

        private async Task<int> RunProcessAsync(string exePath, string args, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo(exePath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _runningProcess = process;

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Debug.WriteLine(e.Data);
                    Dispatcher.InvokeAsync(() => ExportStatusText.Text = e.Data);
                }
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Debug.WriteLine(e.Data);
                    Dispatcher.InvokeAsync(() => ExportStatusText.Text = e.Data);
                }
            };

            process.Exited += (s, e) =>
            {
                tcs.TrySetResult(process.ExitCode);
            };

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to start process.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                }))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    process.CancelOutputRead();
                    process.CancelErrorRead();
                    process.Dispose();
                }
                catch { }
            }
        }

        #endregion

        #region RelayCommand / AsyncRelayCommand

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Predicate<object?>? _canExecute;

            public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
            public void Execute(object? parameter) => _execute(parameter);
            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }
            public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
        }

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<object?, Task> _executeAsync;
            private readonly Predicate<object?>? _canExecute;
            private bool _isExecuting;

            public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

            public async void Execute(object? parameter)
            {
                if (!CanExecute(parameter)) return;
                try
                {
                    _isExecuting = true;
                    RaiseCanExecuteChanged();
                    await _executeAsync(parameter).ConfigureAwait(false);
                }
                finally
                {
                    _isExecuting = false;
                    RaiseCanExecuteChanged();
                }
            }

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            private void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
        }

        #endregion
    }
}