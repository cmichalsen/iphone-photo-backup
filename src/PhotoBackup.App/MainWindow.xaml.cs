using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using PhotoBackup.Core;

namespace PhotoBackup.App;

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        DestinationBox.Text = _settings.Destination;
        RehashBox.IsChecked = _settings.RehashBeforeDelete;
    }

    // ---- buttons ----

    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<BackupProgress>(p =>
        {
            if (p.Total == 0)
            {
                StatusText.Text = $"Reading the phone's file list… {p.Done} found (a large library takes several minutes)";
                return;
            }
            OverallBar.Maximum = Math.Max(1, p.Total);
            OverallBar.Value = p.Done;
            FileBar.Maximum = Math.Max(1, p.CurrentSize);
            FileBar.Value = p.CurrentBytes;
            StatusText.Text = p.CurrentFile.Length > 0 ? $"Backing up {p.CurrentFile} ({Gb(p.CurrentSize)})" : StatusText.Text;
            CountsText.Text = $"{p.Done} of {p.Total} checked   •   {p.Copied} copied   •   {p.AlreadyBackedUp} already on NAS   •   {p.Failed} failed";
        });

        await RunWithPhone("Looking for the iPhone…", (phone, store, dest, ct) =>
        {
            var s = new BackupEngine(store, dest, Log).Run(phone, progress, ct);
            var text = $"Backup done: {s.Copied} copied, {s.AlreadyBackedUp} already on the NAS, {s.Failed} failed (of {s.Total}).";
            if (s.StoppedEarly is not null) text = $"Backup stopped early: {s.StoppedEarly}\n{text}";
            else if (s.Failed > 0) text += " Run it again to retry the failed ones; details are in the log.";
            return text;
        });
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        await RunWithPhone("Reading the phone's file list and checking the NAS (several minutes for a large library)…", (phone, store, dest, ct) =>
        {
            var all = phone.ListFiles().ToList();
            var safe = new DeleteEngine(store, dest, Log).Preview(phone, ct);
            foreach (var f in safe.Take(500)) Log($"would delete  {f.Path}  ({Gb(f.Size)})");
            if (safe.Count > 500) Log($"… and {safe.Count - 500} more");
            return $"Preview only, nothing deleted: {safe.Count} of {all.Count} files on the phone have a verified copy " +
                   $"and could be deleted, freeing {Gb(safe.Sum(f => f.Size))}.";
        });
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var rehash = RehashBox.IsChecked == true;
        var progress = new Progress<DeleteProgress>(p =>
        {
            OverallBar.Maximum = Math.Max(1, p.Total);
            OverallBar.Value = p.Done;
            FileBar.Value = 0;
            if (p.CurrentFile.Length > 0) StatusText.Text = $"Deleting {p.CurrentFile} from the phone";
            CountsText.Text = $"{p.Done} of {p.Total}";
        });

        await RunWithPhone("Reading the phone's file list and checking the NAS (several minutes for a large library)…", (phone, store, dest, ct) =>
        {
            var engine = new DeleteEngine(store, dest, Log);
            var safe = engine.Preview(phone, ct);
            if (safe.Count == 0) return "Nothing to delete: no file on the phone has a verified copy on the NAS yet. Run Back up now first.";

            var answer = Dispatcher.Invoke(() => MessageBox.Show(this,
                $"Delete {safe.Count} photos/videos ({Gb(safe.Sum(f => f.Size))}) from {phone.DeviceName}?\n\n" +
                $"Each one has a verified copy in {dest}. Files without a verified copy are left on the phone.\n\n" +
                "This cannot be undone from this app.",
                "Delete from phone", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel));
            if (answer != MessageBoxResult.OK) return "Delete cancelled. Nothing was removed from the phone.";

            var s = engine.Delete(phone, safe, rehash, progress, ct);
            var text = $"Deleted {s.Deleted} files from the phone, freeing {Gb(s.BytesFreed)}.";
            if (s.Skipped + s.Failed > 0) text += $" {s.Skipped} skipped and {s.Failed} could not be deleted; see the log.";
            return text;
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling after the current file…";
    }

    // ---- plumbing ----

    /// <summary>Runs <paramref name="work"/> on a background thread with the phone open, then shows its result text.</summary>
    private async Task RunWithPhone(string startText, Func<IPhoneSource, StateStore, string, CancellationToken, string> work)
    {
        SaveSettings();
        var dest = _settings.Destination;
        SetBusy(true);
        StatusText.Text = startText;
        OverallBar.Value = FileBar.Value = 0;
        CountsText.Text = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        string result;
        try
        {
            result = await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(dest) || !Directory.Exists(dest))
                    return $"Cannot reach {dest}. Open it once in File Explorer (tick \"Remember my credentials\"), then try again.";

                using var phone = WpdPhoneSource.TryOpen(out var problem);
                if (phone is null) return problem;

                using var store = new StateStore(AppFiles.StatePath);
                return work(phone, store, dest, ct);
            });
        }
        catch (Exception ex)
        {
            Log($"ERROR {ex}");
            result = $"Something went wrong: {ex.Message}";
        }

        StatusText.Text = result;
        Log(result);
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        BackupButton.IsEnabled = PreviewButton.IsEnabled = DeleteButton.IsEnabled = !busy;
        DestinationBox.IsEnabled = RehashBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void Log(string message)
    {
        AppFiles.AppendLog(message);
        Dispatcher.BeginInvoke(() =>
        {
            if (LogBox.LineCount > 2000) LogBox.Clear();
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void SaveSettings()
    {
        _settings.Destination = DestinationBox.Text.Trim();
        _settings.RehashBeforeDelete = RehashBox.IsChecked == true;
        try { _settings.Save(); } catch (IOException) { /* not fatal */ }
    }

    private void OnDestinationChanged(object sender, RoutedEventArgs e) => SaveSettings();

    private void OnOpenDestination(object sender, RoutedEventArgs e) => OpenFolder(DestinationBox.Text.Trim());

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppFiles.LogFolder);
        OpenFolder(AppFiles.LogFolder);
    }

    private void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } }); }
        catch (Exception ex) { StatusText.Text = $"Could not open {path}: {ex.Message}"; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!CancelButton.IsEnabled) return;
        e.Cancel = true;
        StatusText.Text = "Still working. Press Cancel and wait for it to stop before closing.";
    }

    private static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / (double)(1L << 20):F1} MB";
}

