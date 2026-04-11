using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Winslopr.Helpers;

namespace Winslopr
{
    // Represents a single entry from the remote manifest.
    // Entries are either native plugins (installed to Plugins/) or tool scripts (installed to Scripts/).
    public class PluginEntry : INotifyPropertyChanged
    {
        private bool _isChecked;
        private string _installedText = "";
        private SolidColorBrush _installedColor = new(Colors.Gray);

        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Url { get; set; } = "";

        // Display label shown in the Type column: "Plugin", "Tool", or "NX".
        public string Type { get; set; } = "";

        // Install destination, derived from the manifest's type= field:
        //   type=tool       :   "Scripts"  --> appears in the Tools tab
        //   (anything else) :   "Plugins" --> appears in the feature tree
        public string TargetFolder { get; set; } = "Plugins";

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        public string InstalledText
        {
            get => _installedText;
            set { if (_installedText != value) { _installedText = value; OnPropertyChanged(); } }
        }

        public SolidColorBrush InstalledColor
        {
            get => _installedColor;
            set { if (_installedColor != value) { _installedColor = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Unified extension manager dialog.
    //
    // Handles both native plugins (Plugins/ folder--> feature tree) and
    // tool scripts (Scripts/ folder --> Tools tab) through one shared UI.
    // Which folder an entry targets is declared in the remote manifest via type=tool.
    public sealed partial class PluginsDialog : ContentDialog
    {
        // Remote manifest listing all available plugins and scripts.
        // Each entry declares name, description, download url, and optional type.
        private const string ManifestUrl =
            "https://raw.githubusercontent.com/builtbybel/Winslop/main/plugins/plugins_manifest.txt";

        private static readonly HttpClient _http = new();

        private readonly string _pluginsFolder;
        private readonly string _scriptsFolder;

        private List<PluginEntry> _allPlugins = new();
        private readonly List<PluginEntry> _visiblePlugins = new();

        public PluginsDialog()
        {
            InitializeComponent();
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _pluginsFolder = Path.Combine(baseDir, "Plugins");
            _scriptsFolder = Path.Combine(baseDir, "Scripts");

            Loaded += async (_, _) => await LoadPluginsAsync();
        }

        // -- Load -------------------------------------------------

        private async Task LoadPluginsAsync()
        {
            try
            {
                string content = await _http.GetStringAsync(ManifestUrl);
                _allPlugins = ParseManifest(content);
            }
            catch (Exception ex)
            {
                _allPlugins = new List<PluginEntry>();
                txtDescription.Text = "Error loading manifest: " + ex.Message;
            }
            UpdateList();
        }

        // -- Manifest parser --------------------------------------

        // Parses the remote manifest into PluginEntry objects.
        //
        // Manifest format (INI-style, one entry per [Name] block):
        //
        //   [My Plugin]
        //   description=What it does
        //   url=https://example.com/plugin.ps1
        //   type=plugin          <-- optional; "tool" routes to Scripts/, anything else to Plugins/
        //
        // The type= field determines where the file is installed:
        //   type=tool    : Scripts/  --> loaded by ToolsPage, shown in the Tools tab
        //   type=plugin  : Plugins/  --> loaded by PluginManager, shown in the feature tree
        //   (absent)     : Plugins/  --> default, same as type=plugin
        //
        // NX entries (sandbox scripts) are identified by "(NX)" in the name;
        // they always go to Plugins/ and get their own "NX" type badge.
        private static List<PluginEntry> ParseManifest(string content)
        {
            var result = new List<PluginEntry>();
            PluginEntry current = null;
            string currentKey = null;

            foreach (var line in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var t = line.Trim();

                // [Name] starts a new entry
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    if (current != null) result.Add(current);
                    current = new PluginEntry { Name = t[1..^1].Trim() };
                    currentKey = null;
                }
                else if (!string.IsNullOrWhiteSpace(t) && current != null)
                {
                    if (t.Contains('='))
                    {
                        var parts = t.Split('=', 2);
                        switch (parts[0].Trim())
                        {
                            case "description":
                                current.Description = parts[1].Trim();
                                currentKey = "description"; // allow multi-line continuation
                                break;

                            case "url":
                                current.Url = parts[1].Trim();
                                currentKey = "url";
                                break;

                            case "type":
                                // "tool" --> Scripts folder (Tools tab)
                                // anything else --> Plugins folder (feature tree)
                                current.TargetFolder = parts[1].Trim().ToLowerInvariant() == "tool"
                                    ? "Scripts" : "Plugins";
                                currentKey = null;
                                break;

                            default:
                                currentKey = null;
                                break;
                        }
                    }
                    else if (currentKey == "description")
                    {
                        // Continuation line for multi-line descriptions
                        current.Description += "\n" + t;
                    }
                }
            }

            if (current != null) result.Add(current);
            return result;
        }

        // -- Update list ------------------------------------------

        // Rebuilds the visible list applying the current search query.
        // Also resolves installed state and the Type badge for each entry.
        private void UpdateList(string query = "")
        {
            _visiblePlugins.Clear();

            foreach (var p in _allPlugins)
            {
                if (query.Length > 0 &&
                    !p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !(p.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                // Check installed state by looking directly in the entry's different target folder (e.g. one Plugin, one Tool).
                var fileName = Path.GetFileName(p.Url);
                var targetDir = p.TargetFolder == "Scripts" ? _scriptsFolder : _pluginsFolder;
                bool installed = File.Exists(Path.Combine(targetDir, fileName));
                p.InstalledText = installed ? Localizer.Get("Plugins_Installed") : "";
                p.InstalledColor = Brush(installed ? Microsoft.UI.Colors.Green : Microsoft.UI.Colors.Gray);
                p.IsChecked = false; // always start unchecked; installed state shown via InstalledText

                // Type badge priority:
                //   NX   --> sandbox script (name ends with "(NX)"), always in Plugins/
                //   Tool --> tool script (TargetFolder == "Scripts"), shown in Tools tab
                //   Plugin --> native plugin (TargetFolder == "Plugins"), shown in feature tree
                p.Type = p.Name.EndsWith("(NX)", StringComparison.OrdinalIgnoreCase) ? "NX"
                       : p.TargetFolder == "Scripts" ? "Tool"
                       : "Plugin";

                _visiblePlugins.Add(p);
            }

            listPlugins.ItemsSource = null;
            listPlugins.ItemsSource = _visiblePlugins;
        }

        private static SolidColorBrush Brush(Windows.UI.Color color) => new(color);

        // -- Install / Update / Remove ----------------------------

        // Downloads all checked entries to their respective target folder.
        // force=true re-downloads files that are already present (used by Update All).
        private async Task InstallPlugins(bool force)
        {
            var toInstall = _visiblePlugins.Where(p => p.IsChecked).ToList();
            if (toInstall.Count == 0) return;

            progressBar.Visibility = Visibility.Visible;
            progressBar.Maximum = toInstall.Count;
            progressBar.Value = 0;

            foreach (var p in toInstall)
            {
                // Route to the correct folder based on the manifest type= field
                var targetDir = p.TargetFolder == "Scripts" ? _scriptsFolder : _pluginsFolder;
                Directory.CreateDirectory(targetDir);

                var fileName = Path.GetFileName(p.Url);
                var filePath = Path.Combine(targetDir, fileName);

                if (!force && File.Exists(filePath)) { progressBar.Value++; continue; }

                try
                {
                    var data = await _http.GetByteArrayAsync(p.Url);
                    await File.WriteAllBytesAsync(filePath, data);
                    p.InstalledText = Localizer.Get("Plugins_Installed");
                    p.InstalledColor = Brush(Microsoft.UI.Colors.Green);
                }
                catch { /* skip failed downloads silently */ }

                progressBar.Value++;
            }

            progressBar.Visibility = Visibility.Collapsed;

            // Rebuild the list; UpdateList re-checks File.Exists per entry so installed
            // state is accurate, and checkboxes reset so Install is immediately usable again.
            UpdateList(txtSearch.Text.Trim());
        }

        private async Task UpdateAll()
        {
            foreach (var p in _visiblePlugins) p.IsChecked = true;
            await InstallPlugins(force: true);
        }

        // Deletes the file from whichever folder it was installed into,
        // then refreshes the list so the installed state reflects the deletion.
        private void RemoveChecked()
        {
            foreach (var p in _visiblePlugins.Where(p => p.IsChecked).ToList())
            {
                var fileName = Path.GetFileName(p.Url);
                var pluginPath = Path.Combine(_pluginsFolder, fileName);
                var scriptPath = Path.Combine(_scriptsFolder, fileName);

                if (File.Exists(pluginPath)) File.Delete(pluginPath);
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }

            UpdateList(txtSearch.Text.Trim());
        }

        // -- UI handlers ------------------------------------------

        private void txtSearch_TextChanged(object sender, TextChangedEventArgs e)
            => UpdateList(txtSearch.Text.Trim());

        private void listPlugins_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (listPlugins.SelectedItem is PluginEntry p)
                txtDescription.Text = p.Description ?? "";
        }

        private async void btnInstall_Click(object sender, RoutedEventArgs e) => await InstallPlugins(force: false);

        private async void btnUpdateAll_Click(object sender, RoutedEventArgs e) => await UpdateAll();

        private void btnRemove_Click(object sender, RoutedEventArgs e) => RemoveChecked();

        // Opens the installed file in Notepad, searching both folders
        private void btnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (listPlugins.SelectedItem is not PluginEntry plugin) return;
            var fileName = Path.GetFileName(plugin.Url);
            var pluginPath = Path.Combine(_pluginsFolder, fileName);
            var scriptPath = Path.Combine(_scriptsFolder, fileName);
            var path = File.Exists(pluginPath) ? pluginPath
                     : File.Exists(scriptPath) ? scriptPath
                     : null;
            if (path == null) return;
            OpenProcess("notepad.exe", $"\"{path}\"");
        }

        // Opens the folder of the selected entry in Explorer.
        // Falls back to Plugins/ when nothing is selected
        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = _pluginsFolder;
            if (listPlugins.SelectedItem is PluginEntry p && p.TargetFolder == "Scripts")
                folder = _scriptsFolder;
            Directory.CreateDirectory(folder);
            OpenProcess(folder);
        }

        private void btnSubmit_Click(object sender, RoutedEventArgs e)
            => OpenProcess("https://github.com/builtbybel/Winslop/blob/main/plugins/plugins_manifest.txt");

        private static void OpenProcess(string target, string args = null)
            => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                Arguments = args ?? "",
                UseShellExecute = true
            });
    }
}