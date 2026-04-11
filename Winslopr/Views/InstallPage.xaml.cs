using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Winslopr.Helpers;

namespace Winslopr.Views
{
    // -- InstallEntry ---------------------------------------------

    public class InstallEntry : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _isInstalled;
        private bool _hasUpgrade;

        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public string WingetId { get; set; } = "";

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        public bool IsInstalled
        {
            get => _isInstalled;
            set { if (_isInstalled != value) { _isInstalled = value; OnPropertyChanged(); OnPropertyChanged(nameof(InstalledLabel)); OnPropertyChanged(nameof(InstalledColor)); } }
        }

        public bool HasUpgrade
        {
            get => _hasUpgrade;
            set { if (_hasUpgrade != value) { _hasUpgrade = value; OnPropertyChanged(); OnPropertyChanged(nameof(UpdateLabel)); OnPropertyChanged(nameof(UpdateColor)); } }
        }

        // -- Display helpers for columns --------------------------

        public string InstalledLabel => IsInstalled ? "✔ Yes" : "✘ No";
        public string UpdateLabel => HasUpgrade ? "⬆ Available" : "";

        public SolidColorBrush InstalledColor => IsInstalled
            ? new SolidColorBrush(Colors.Green)
            : new SolidColorBrush(Colors.Red);

        public SolidColorBrush UpdateColor => HasUpgrade
            ? new SolidColorBrush(Colors.Orange)
            : new SolidColorBrush(Colors.Transparent);

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // -- InstallPage ----------------------------------------------

    public sealed partial class InstallPage : Page, IMainActions, ISearchable, IView
    {
        private const string AppsFileName = "winget-apps.ini";

        private readonly List<InstallEntry> _allApps = new();
        private readonly ObservableCollection<InstallEntry> _visibleApps = new();
        private readonly HashSet<string> _checkedIds = new(StringComparer.OrdinalIgnoreCase);

        private string _lastSearchQuery = "";
        private bool _isBusy;

        public InstallPage()
        {
            InitializeComponent();
            listApps.ItemsSource = _visibleApps;

            LoadAppsFromFile();
            PopulateCategories();
            ApplyFilter("");

            _ = AnalyzeAsync();
        }

        // -- IMainActions -----------------------------------------

        public async Task AnalyzeAsync()
        {
            if (_isBusy) return;
            SetBusy(true);
            try
            {
                Logger.BeginSection("Winget Analyze");
                Logger.Log("Running: winget list", LogLevel.Info);
                var listLines = await RunWingetCaptureLinesAsync("list --accept-source-agreements --disable-interactivity");

                Logger.Log("Running: winget upgrade", LogLevel.Info);
                var upgradeLines = await RunWingetCaptureLinesAsync("upgrade --accept-source-agreements --disable-interactivity");

                foreach (var a in _allApps)
                {
                    a.IsInstalled = listLines.Any(l => l.Contains(a.WingetId, StringComparison.OrdinalIgnoreCase));
                    a.HasUpgrade = upgradeLines.Any(l => l.Contains(a.WingetId, StringComparison.OrdinalIgnoreCase));
                }

                UpdateCheckboxCounts();
                Logger.Log("Analyze finished.", LogLevel.Info);

                Logger.BeginSection("Ready");
                int installedCount = _allApps.Count(x => x.IsInstalled);
                int updatesCount = _allApps.Count(x => x.HasUpgrade);
                Logger.Log($"Installed (from catalog): {installedCount}/{_allApps.Count}", LogLevel.Info);
                Logger.Log($"Updates available: {updatesCount}", updatesCount > 0 ? LogLevel.Warning : LogLevel.Info);
                Logger.Log("Tick apps and click 'Apply selected changes' to install.", LogLevel.Info);

                ApplyFilter(_lastSearchQuery);
            }
            catch (Exception ex) { Logger.Log("Analyze failed: " + ex.Message, LogLevel.Error); }
            finally { SetBusy(false); }
        }

        public async Task FixAsync()
        {
            var apps = GetSelectedApps();
            if (apps.Count == 0 || _isBusy) return;
            SetBusy(true);
            try
            {
                Logger.BeginSection("Winget Fix");
                foreach (var a in apps)
                {
                    if (!a.IsInstalled) await InstallOneAsync(a);
                    else if (a.HasUpgrade) await UpgradeOneAsync(a);
                    else Logger.Log($"Skip: {a.Name}", LogLevel.Info);
                }
            }
            catch (Exception ex) { Logger.Log("Fix failed: " + ex.Message, LogLevel.Error); }
            finally { SetBusy(false); await AnalyzeAsync(); }
        }

        public void ToggleSelection()
        {
            bool shouldCheck = _visibleApps.Any(a => !a.IsChecked);
            foreach (var app in _visibleApps) app.IsChecked = shouldCheck;
        }

        // -- ISearchable / IView ----------------------------------

        public void ApplySearch(string query) => ApplyFilter(query);

        public void RefreshView()
        { _ = AnalyzeAsync(); Logger.Clear(); }

        // -- Filter -----------------------------------------------

        private void comboCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ApplyFilter(_lastSearchQuery);

        private void chkFilter_Changed(object sender, RoutedEventArgs e)
            => ApplyFilter(_lastSearchQuery);

        private void UpdateCheckboxCounts()
        {
            chkInstalledOnly.Content = $"{Localizer.Get("Install_Installed")} ({_allApps.Count(a => a.IsInstalled)})";
            int u = _allApps.Count(a => a.HasUpgrade);
            chkUpgradesOnly.Content = u > 0 ? $"{Localizer.Get("Install_Upgradeable")} ({u})" : Localizer.Get("Install_Upgradeable");
        }

        private void ApplyFilter(string query)
        {
            _lastSearchQuery = (query ?? "").Trim();
            RememberCheckedState();

            var cat = comboCategory.SelectedItem as string ?? "All";
            IEnumerable<InstallEntry> q = _allApps;

            if (!string.Equals(cat, "All", StringComparison.OrdinalIgnoreCase))
                q = q.Where(a => string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase));
            if (chkInstalledOnly.IsChecked == true) q = q.Where(a => a.IsInstalled);
            if (chkUpgradesOnly.IsChecked == true) q = q.Where(a => a.HasUpgrade);
            if (!string.IsNullOrWhiteSpace(_lastSearchQuery))
                q = q.Where(a => a.Name.Contains(_lastSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                                 a.WingetId.Contains(_lastSearchQuery, StringComparison.OrdinalIgnoreCase));

            _visibleApps.Clear();
            foreach (var app in q.OrderBy(a => a.Name))
            {
                app.IsChecked = _checkedIds.Contains(app.WingetId);
                _visibleApps.Add(app);
            }
        }

        private void RememberCheckedState()
        {
            _checkedIds.Clear();
            foreach (var app in _visibleApps.Where(a => a.IsChecked))
                _checkedIds.Add(app.WingetId);
        }

        private List<InstallEntry> GetSelectedApps() => _visibleApps.Where(a => a.IsChecked).ToList();

        private void PopulateCategories()
        {
            var cats = _allApps.Select(a => a.Category)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c).ToList();

            comboCategory.Items.Clear();
            comboCategory.Items.Add("All");
            foreach (var c in cats) comboCategory.Items.Add(c);
            comboCategory.SelectedIndex = 0;
        }

        // -- Button handlers --------------------------------------

        private async void btnInstall_Click(object sender, RoutedEventArgs e)
        {
            var apps = GetSelectedApps();
            if (apps.Count == 0 || _isBusy) return;
            SetBusy(true);
            try { Logger.BeginSection("Install"); foreach (var a in apps) await InstallOneAsync(a); }
            catch (Exception ex) { Logger.Log("Install failed: " + ex.Message, LogLevel.Error); }
            finally { SetBusy(false); await AnalyzeAsync(); }
        }

        private async void btnUpgradeSelected_Click(object sender, RoutedEventArgs e)
        {
            var apps = GetSelectedApps();
            if (apps.Count == 0 || _isBusy) return;
            SetBusy(true);
            try { Logger.BeginSection("Upgrade Selected"); foreach (var a in apps) await UpgradeOneAsync(a); }
            catch (Exception ex) { Logger.Log("Upgrade failed: " + ex.Message, LogLevel.Error); }
            finally { SetBusy(false); await AnalyzeAsync(); }
        }

        private async void btnUpgradeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            SetBusy(true);
            try
            {
                Logger.BeginSection("Upgrade All");
                await RunWingetStreamingAsync("upgrade --all --accept-package-agreements --accept-source-agreements");
            }
            catch (Exception ex) { Logger.Log("Upgrade all failed: " + ex.Message, LogLevel.Error); }
            finally { SetBusy(false); await AnalyzeAsync(); }
        }

        private async void btnUninstall_Click(object sender, RoutedEventArgs e)
        {
            var apps = GetSelectedApps();
            if (apps.Count == 0 || _isBusy) return;
            SetBusy(true);
            try
            {
                Logger.BeginSection("Uninstall");
                foreach (var a in apps)
                {
                    Logger.Log($"Uninstall: {a.Name}", LogLevel.Warning);
                    await RunWingetStreamingAsync($"uninstall --id \"{a.WingetId}\" -e");
                }
            }
            catch (Exception ex) { Logger.Log("Uninstall failed: " + ex.Message, LogLevel.Error); }
            finally { SetBusy(false); await AnalyzeAsync(); }
        }

        private void listApps_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is InstallEntry entry)
                entry.IsChecked = !entry.IsChecked;
        }

        // -- File loading / parsing -------------------------------

        private void LoadAppsFromFile()
        {
            _allApps.Clear();
            _checkedIds.Clear();

            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", AppsFileName);

            if (!File.Exists(path))
            {
                Logger.Log($"Missing {AppsFileName} next to EXE: {path}", LogLevel.Error);
                return;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            _allApps.AddRange(ParseIniApps(text));

            Logger.BeginSection("Winget Apps");
            Logger.Log($"Loaded {_allApps.Count} apps from {AppsFileName}", LogLevel.Info);
        }

        /// <summary>
        /// Parses INI-like text:
        /// [Category]
        /// Name=Winget.Id
        /// Skips comments (# or ;) and Winget "na".
        /// </summary>
        private static List<InstallEntry> ParseIniApps(string iniText)
        {
            var list = new List<InstallEntry>();
            string currentCategory = "Uncategorized";

            using var sr = new StringReader(iniText ?? "");
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('#') || line.StartsWith(';')) continue;

                // Category header
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    var c = line[1..^1].Trim();
                    currentCategory = string.IsNullOrWhiteSpace(c) ? "Uncategorized" : c;
                    continue;
                }

                // Entry: Name=WingetId
                var eq = line.IndexOf('=');
                if (eq <= 0 || eq >= line.Length - 1) continue;

                var name = line[..eq].Trim();
                var winget = line[(eq + 1)..].Trim();

                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.IsNullOrWhiteSpace(winget)) continue;
                if (string.Equals(winget, "na", StringComparison.OrdinalIgnoreCase)) continue;

                list.Add(new InstallEntry
                {
                    Category = currentCategory,
                    Name = name,
                    WingetId = winget
                });
            }

            return list;
        }

        // -- Winget helpers ---------------------------------------

        private async Task InstallOneAsync(InstallEntry a)
        {
            Logger.Log($"Install: {a.Name} ({a.WingetId})", LogLevel.Info);
            await RunWingetStreamingAsync($"install --id \"{a.WingetId}\" -e --source winget --accept-package-agreements --accept-source-agreements");
        }

        private async Task UpgradeOneAsync(InstallEntry a)
        {
            Logger.Log($"Upgrade: {a.Name} ({a.WingetId})", LogLevel.Info);
            await RunWingetStreamingAsync($"upgrade --id \"{a.WingetId}\" -e --source winget --accept-package-agreements --accept-source-agreements");
        }

        private async Task<int> RunWingetStreamingAsync(string arguments)
            => await RunProcessStreamingAsync("winget", arguments, line => Logger.Log(line, LogLevel.Info));

        private static async Task<List<string>> RunWingetCaptureLinesAsync(string arguments)
        {
            var lines = new List<string>();
            await RunProcessStreamingAsync("winget", arguments, line => lines.Add(line));
            return lines;
        }

        private static async Task<int> RunProcessStreamingAsync(string fileName, string arguments, Action<string> onLine)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var tcs = new TaskCompletionSource<int>();
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            p.OutputDataReceived += (s, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
            p.Exited += (s, e) => { try { tcs.TrySetResult(p.ExitCode); } finally { p.Dispose(); } };

            if (!p.Start()) return -1;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return await tcs.Task.ConfigureAwait(false);
        }

        // -- Busy state -------------------------------------------

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            btnInstall.IsEnabled = !busy;
            btnUpgradeSelected.IsEnabled = !busy;
            btnUpgradeAll.IsEnabled = !busy;
            btnUninstall.IsEnabled = !busy;
            chkInstalledOnly.IsEnabled = !busy;
            chkUpgradesOnly.IsEnabled = !busy;
            comboCategory.IsEnabled = !busy;
        }
    }
}