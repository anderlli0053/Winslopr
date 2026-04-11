using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Winslopr;
using Winslopr.Helpers;

namespace Winslopr.Views
{
    // -- AppItem -------------------------------------------------
    // Replaces CheckedListBox items. WinUI needs a data object
    // for the ListView checkbox binding.

    public class AppItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        public string DisplayName { get; set; }
        public string FullName { get; set; }

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // -- AppsPage ------------------------------------------------

    public sealed partial class AppsPage : Page, IMainActions, ISearchable, IView
    {
        // Keeps the full unfiltered app list for search reset/filtering.
        private AppItem[] _allApps = Array.Empty<AppItem>();
        private readonly ObservableCollection<AppItem> _visibleApps = new();

        private enum AppDisplayMode
        {
            Recommended = 0,
            AllInstalled = 1,
            PluginOnly = 2,
            BuiltInOnly = 3
        }

        public AppsPage()
        {
            InitializeComponent();
            listApps.ItemsSource = _visibleApps;
            InitializeDisplayModeCombo();
        }


        private void InitializeDisplayModeCombo()
        {
            comboDisplayMode.Items.Clear();
            comboDisplayMode.Items.Add(Localizer.Get("ScanMode_Standard"));
            comboDisplayMode.Items.Add(Localizer.Get("ScanMode_Full"));
            comboDisplayMode.Items.Add(Localizer.Get("ScanMode_Community"));
            comboDisplayMode.Items.Add(Localizer.Get("ScanMode_BuiltIn"));
            comboDisplayMode.SelectedIndex = 0;
        }

        private AppDisplayMode SelectedMode
        {
            get
            {
                return comboDisplayMode.SelectedIndex switch
                {
                    1 => AppDisplayMode.AllInstalled,
                    2 => AppDisplayMode.PluginOnly,
                    3 => AppDisplayMode.BuiltInOnly,
                    _ => AppDisplayMode.Recommended
                };
            }
        }

        // ---------------- IMainActions ----------------

        public async Task AnalyzeAsync()
        {
            _visibleApps.Clear();
            scanSpinner.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            scanSpinner.IsActive = true;

            // Load plugin and built-in patterns once.
            var plugin = LoadExternalBloatwarePatterns();
            var builtIn = LoadBuiltInPatterns();

            string[] bloat;
            string[] white;
            bool scanAll;

            switch (SelectedMode)
            {
                case AppDisplayMode.AllInstalled:
                    bloat = plugin.bloatwarePatterns.Length > 0 ? plugin.bloatwarePatterns : builtIn.bloat;
                    white = plugin.whitelistPatterns.Length > 0 ? plugin.whitelistPatterns : builtIn.white;
                    scanAll = true;
                    Logger.Log(Localizer.Get("Apps_DisplayAll"), LogLevel.Info);
                    break;

                case AppDisplayMode.PluginOnly:
                    if (plugin.bloatwarePatterns.Length == 0 && !plugin.scanAll)
                    {
                        Logger.Log(Localizer.Get("Apps_NoPlugin"), LogLevel.Warning);
                        _allApps = Array.Empty<AppItem>();
                        _visibleApps.Clear();
                        return;
                    }
                    bloat = plugin.bloatwarePatterns;
                    white = plugin.whitelistPatterns;
                    scanAll = plugin.scanAll;
                    Logger.Log(Localizer.Get("Apps_DisplayPlugin"), LogLevel.Info);
                    break;

                case AppDisplayMode.BuiltInOnly:
                    bloat = builtIn.bloat;
                    white = builtIn.white;
                    scanAll = false;
                    Logger.Log(Localizer.Get("Apps_DisplayBuiltIn"), LogLevel.Info);
                    break;

                default: // Recommended
                    if (plugin.bloatwarePatterns.Length > 0 || plugin.scanAll)
                    {
                        bloat = plugin.bloatwarePatterns;
                        white = plugin.whitelistPatterns;
                        scanAll = plugin.scanAll;
                        Logger.Log(Localizer.Get("Apps_DisplayRecommendedPlugin"), LogLevel.Info);
                    }
                    else
                    {
                        bloat = builtIn.bloat;
                        white = builtIn.white;
                        scanAll = false;
                        Logger.Log(Localizer.Get("Apps_DisplayRecommendedBuiltIn"), LogLevel.Info);
                    }
                    break;
            }

            List<AppAnalysisResult> results = await AnalyzeAppsAsync(bloat, white, scanAll);
            LogAnalysisResults(results, scanAll);

            _allApps = results
                .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new AppItem { DisplayName = r.FullName, FullName = r.FullName })
                .ToArray();

            scanSpinner.IsActive = false;
            scanSpinner.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            FillList(_allApps);
        }

        public async Task FixAsync()
        {
            Logger.Clear();

            List<string> selected = _visibleApps
                .Where(a => a.IsChecked)
                .Select(a => a.FullName)
                .ToList();

            if (selected.Count == 0)
                return;

            List<string> removed = await UninstallSelectedAppsAsync(selected);

            // Remove uninstalled apps from the list
            foreach (string fullName in removed)
            {
                var item = _visibleApps.FirstOrDefault(a => a.FullName == fullName);
                if (item != null)
                    _visibleApps.Remove(item);
            }

            // Also remove from the full list
            _allApps = _allApps.Where(a => !removed.Contains(a.FullName)).ToArray();
        }

        public void ToggleSelection()
        {
            bool shouldCheck = _visibleApps.Any(a => !a.IsChecked);
            foreach (var app in _visibleApps)
                app.IsChecked = shouldCheck;
        }

        // ---------------- ISearchable ----------------

        public void ApplySearch(string query)
        {
            if (_allApps == null || _allApps.Length == 0)
                return;

            string q = (query ?? string.Empty).Trim();

            // Preserve checked state
            var checkedSet = new HashSet<string>(
                _visibleApps.Where(a => a.IsChecked).Select(a => a.FullName),
                StringComparer.OrdinalIgnoreCase);

            AppItem[] items;
            if (string.IsNullOrEmpty(q))
            {
                items = _allApps;
            }
            else
            {
                items = _allApps
                    .Where(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            _visibleApps.Clear();
            foreach (var app in items)
            {
                app.IsChecked = checkedSet.Contains(app.FullName);
                _visibleApps.Add(app);
            }
        }

        // ---------------- IView ----------------

        public void RefreshView()
        {
            _ = AnalyzeAsync();
            Logger.Clear();
        }

        // ---------------- Helpers ----------------

        private void FillList(AppItem[] items)
        {
            _visibleApps.Clear();
            foreach (var item in items)
                _visibleApps.Add(item);
        }

        // ---------------- Core logic ----------------

        private class AppAnalysisResult
        {
            public string AppName { get; set; }
            public string FullName { get; set; }
        }

        /// <summary>
        /// Loads all installed Store apps by calling powershell.exe as an external process.
        /// </summary>
        private static async Task<Dictionary<string, string>> LoadAppsAsync()
        {
            var dir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var lines = await RunPowerShellAsync(
                "Get-AppxPackage | ForEach-Object { $_.Name + '|' + $_.PackageFullName }");

            foreach (var line in lines)
            {
                var sep = line.IndexOf('|');
                if (sep <= 0) continue;

                string name = line[..sep];
                string fullName = line[(sep + 1)..];

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(fullName) && !dir.ContainsKey(name))
                    dir[name] = fullName;
            }

            Logger.Log($"(Checked against {dir.Count} apps from the system)");
            return dir;
        }

        private async Task<List<AppAnalysisResult>> AnalyzeAppsAsync(string[] bloatwarePatterns, string[] whitelistPatterns, bool scanAll)
        {
            Logger.Log($"\n🧩 {Localizer.Get("Apps_AnalysisTitle")}", LogLevel.Info);
            Logger.Log(new string('=', 45), LogLevel.Info);

            var apps = await LoadAppsAsync();

            string[] bloat = Normalize(bloatwarePatterns);
            string[] white = Normalize(whitelistPatterns);

            var results = new List<AppAnalysisResult>();

            foreach (var kvp in apps)
            {
                string name = kvp.Key;
                string full = kvp.Value;
                string lower = name.ToLowerInvariant();

                // Always skip whitelisted apps
                if (IsMatchAny(lower, white))
                    continue;

                // Show all if scanAll; otherwise only bloat matches
                if (scanAll || IsMatchAny(lower, bloat))
                {
                    results.Add(new AppAnalysisResult { AppName = name, FullName = full });
                }
            }

            return results;
        }

        private static bool IsMatchAny(string haystackLower, string[] patternsLower)
        {
            if (patternsLower == null || patternsLower.Length == 0)
                return false;

            for (int i = 0; i < patternsLower.Length; i++)
            {
                if (haystackLower.Contains(patternsLower[i]))
                    return true;
            }

            return false;
        }

        private static void LogAnalysisResults(List<AppAnalysisResult> results, bool scanAll)
        {
            if (scanAll)
            {
                Logger.Log(Localizer.GetFormat("Apps_ShowingAll", results.Count), LogLevel.Info);
                Logger.Log("");
                return;
            }

            if (results.Count > 0)
            {
                Logger.Log(Localizer.Get("Apps_BloatwareDetected"), LogLevel.Info);
                foreach (var a in results)
                    Logger.Log($"❌ [ Bloatware ] {a.AppName} ({a.FullName})", LogLevel.Warning);
            }
            else
            {
                Logger.Log($"✅ {Localizer.Get("Apps_NoBloatware")}", LogLevel.Info);
            }

            Logger.Log("");
        }

        /// <summary>
        /// Uninstalls selected apps by calling powershell.exe as an external process.
        /// </summary>
        private static async Task<List<string>> UninstallSelectedAppsAsync(List<string> selectedApps)
        {
            var removed = new List<string>();

            foreach (string fullName in selectedApps)
            {
                Logger.Log($"🗑️ Removing app: {fullName}...");

                try
                {
                    var output = await RunPowerShellAsync(
                        $"Remove-AppxPackage -Package '{fullName}'");

                    bool hasError = output.Any(l =>
                        l.StartsWith("Remove-AppxPackage", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("error", StringComparison.OrdinalIgnoreCase));

                    if (hasError)
                    {
                        foreach (var line in output.Where(l => l.Length > 0))
                            Logger.Log($"Failed to uninstall app {fullName}: {line}", LogLevel.Warning);
                    }
                    else
                    {
                        Logger.Log($"🗑️ Removed Store App: {fullName}");
                        removed.Add(fullName);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error uninstalling app {fullName}: {ex.Message}", LogLevel.Warning);
                }
            }

            var failed = selectedApps.Except(removed).ToList();
            foreach (string f in failed)
                Logger.Log($"⚠️ Failed to remove Store App: {f}", LogLevel.Warning);

            Logger.Log(Localizer.Get("Apps_CleanupComplete"));
            return removed;
        }

        // -- PowerShell process helper ----------------------------

        private static async Task<List<string>> RunPowerShellAsync(string command)
        {
            var lines = new List<string>();

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) lines.Add(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) lines.Add(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            return lines;
        }

        // ---------------- Pattern loading ----------------

        /// <summary>
        /// Loads external bloatware and whitelist patterns from Plugins\CFEnhancer.txt.
        /// Supports: "!" prefix => whitelist, "*" => scanAll wildcard, "#" comments.
        /// </summary>
        private static (string[] bloatwarePatterns, string[] whitelistPatterns, bool scanAll)
            LoadExternalBloatwarePatterns(string fileName = "CFEnhancer.txt")
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string fullPath = Path.Combine(exeDir, "Plugins", fileName);

                if (!File.Exists(fullPath))
                {
                    Logger.Log(
                        "⚠️ The bloatware radar stays basic for now 🧠. Get the enhanced detection list from Start > Manage plugins > CFEnhancer plugin",
                        LogLevel.Warning);

                    return (Array.Empty<string>(), Array.Empty<string>(), false);
                }

                var bloat = new List<string>();
                var white = new List<string>();
                bool scanAll = false;

                foreach (string raw in File.ReadLines(fullPath))
                {
                    string entry = raw.Split('#')[0].Trim();
                    if (string.IsNullOrWhiteSpace(entry))
                        continue;

                    if (entry == "*" || entry == "*.*")
                    {
                        scanAll = true;
                        continue;
                    }

                    if (entry.StartsWith("!"))
                    {
                        string w = entry.Substring(1).Trim().ToLowerInvariant();
                        if (!string.IsNullOrEmpty(w))
                            white.Add(w);
                    }
                    else
                    {
                        bloat.Add(entry.ToLowerInvariant());
                    }
                }

                return (Normalize(bloat), Normalize(white), scanAll);
            }
            catch (Exception ex)
            {
                Logger.Log("Error reading external bloatware file: " + ex.Message, LogLevel.Warning);
                return (Array.Empty<string>(), Array.Empty<string>(), false);
            }
        }

        /// <summary>
        /// Built-in bloatware list (replaces my old Winforms > Resources.PredefinedApps).
        /// </summary>
        private static (string[] bloat, string[] white) LoadBuiltInPatterns()
        {
            const string predefinedApps =
                "Solitaire,CandyCrush,Netflix,Facebook,Twitter,Instagram,TikTok,Spotify," +
                "Skype,OneNote,OneDrive,Mail,Calendar,Weather,News,Maps," +
                "Groove,Movies,TV,Phone,Camera,Feedback,FeedbackHub," +
                "GetHelp,GetStarted,Messaging,Office,Paint3D,Print3D," +
                "StickyNotes,Wallet,YourPhone,3DViewer,Alarms,VoiceRecorder," +
                "ToDo,Whiteboard,ZuneMusic,ZuneVideo,3DViewer,DevHome," +
                "Copilot,MicrosoftPCManager";

            string[] bloat = predefinedApps
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToArray();

            return (bloat, Array.Empty<string>());
        }

        private static string[] Normalize(IEnumerable<string> items)
        {
            if (items == null)
                return Array.Empty<string>();

            return items
                .Select(s => (s ?? string.Empty).Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToArray();
        }
    }
}
