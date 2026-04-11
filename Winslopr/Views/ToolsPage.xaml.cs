using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Winslopr;
using Winslopr.Helpers;
using Winslopr.Tools;

namespace Winslopr.Views
{
    public sealed partial class ToolsPage : Page, ISearchable, IView
    {
        // Full list loaded from scripts
        private readonly List<ToolsDefinition> _allTools = new();

        // Filtered list shown in the ListView
        private readonly ObservableCollection<ToolsDefinition> _visibleTools = new();

        // Currently selected tool shown in the details panel
        private ToolsDefinition _selectedTool;

        private ToolsCategory _category = ToolsCategory.All;
        private string _searchQuery = "";

        private const string DefaultPlaceholder = "Enter input (e.g., IDs or raw arguments)";

        public ToolsPage()
        {
            InitializeComponent();
            listTools.ItemsSource = _visibleTools;

            // Setup filter dropdown
            comboFilter.Items.Clear();
            comboFilter.Items.Add("All");
            comboFilter.Items.Add("Tool");
            comboFilter.Items.Add("Pre");
            comboFilter.Items.Add("Mid");
            comboFilter.Items.Add("Post");
            comboFilter.SelectedIndex = 0;

            // Start with empty details
            ClearDetails();

            // Load scripts
            LoadToolsAsync();
        }

        // ---------------- ISearchable ----------------

        public void ApplySearch(string query)
        {
            _searchQuery = query ?? "";
            ApplyFilterAndSearch();
        }

        // ---------------- IView ----------------

        public void RefreshView()
        {
            Logger.Clear();
            LoadToolsAsync();
        }

        // ---------------- Loading ----------------

        private async void LoadToolsAsync()
        {
            lblStatus.Text = Localizer.Get("Tools_Loading");

            _allTools.Clear();
            _visibleTools.Clear();
            ClearDetails();

            string scriptDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");

            if (!Directory.Exists(scriptDirectory))
            {
                Directory.CreateDirectory(scriptDirectory);
                lblStatus.Text = Localizer.Get("Tools_FolderCreated");
                return;
            }

            string[] scriptFiles = await Task.Run(() => Directory.GetFiles(scriptDirectory, "*.ps1"));

            var loadedTools = await Task.Run(() =>
            {
                var list = new List<ToolsDefinition>();

                foreach (var scriptPath in scriptFiles)
                {
                    string title = Path.GetFileNameWithoutExtension(scriptPath);
                    var meta = ReadMetadataFromScript(scriptPath);
                    list.Add(new ToolsDefinition(title, PickIconForScript(title), scriptPath, meta));
                }

                return list;
            });

            _allTools.AddRange(loadedTools);
            ApplyFilterAndSearch();

            lblStatus.Text = Localizer.GetFormat("Tools_Loaded", _allTools.Count);

            // Auto-select first item
            if (_visibleTools.Count > 0)
                listTools.SelectedIndex = 0;
        }

        // ---------------- Filter / Search ----------------

        private void comboFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _category = (comboFilter.SelectedItem?.ToString()) switch
            {
                "Tool" => ToolsCategory.Tool,
                "Pre" => ToolsCategory.Pre,
                "Mid" => ToolsCategory.Mid,
                "Post" => ToolsCategory.Post,
                _ => ToolsCategory.All
            };

            ApplyFilterAndSearch();
        }

        private void ApplyFilterAndSearch()
        {
            string q = (_searchQuery ?? "").Trim().ToLowerInvariant();

            var filtered = _allTools
                .Where(t =>
                    (_category == ToolsCategory.All || t.Category == _category) &&
                    (string.IsNullOrEmpty(q) ||
                     (t.Title ?? "").ToLowerInvariant().Contains(q) ||
                     (t.Description ?? "").ToLowerInvariant().Contains(q)))
                .OrderBy(t => t.Title)
                .ToList();

            _visibleTools.Clear();
            foreach (var t in filtered)
                _visibleTools.Add(t);

            if (_visibleTools.Count == 0)
                ClearDetails();
        }

        // ---------------- Selection ----------------

        private void listTools_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tool = listTools.SelectedItem as ToolsDefinition;
            SetTool(tool);
        }

        // ---------------- Details panel ----------------

        private void SetTool(ToolsDefinition tool)
        {
            _selectedTool = tool;

            if (tool == null)
            {
                ClearDetails();
                return;
            }

            panelPlaceholder.Visibility = Visibility.Collapsed;
            scrollDetails.Visibility = Visibility.Visible;

            lblIcon.Text = tool.Icon ?? "";
            lblTitle.Text = tool.Title ?? "";
            lblDescription.Text = tool.Description ?? "";

            progressRing.IsActive = false;

            // Options dropdown
            comboOptions.Items.Clear();
            if (tool.Options != null && tool.Options.Count > 0)
            {
                comboOptions.Visibility = Visibility.Visible;
                foreach (var opt in tool.Options)
                    comboOptions.Items.Add(opt);
                comboOptions.SelectedIndex = 0;
            }
            else
            {
                comboOptions.Visibility = Visibility.Collapsed;
            }

            // Input textbox
            if (tool.SupportsInput)
            {
                textInput.Visibility = Visibility.Visible;
                textInput.PlaceholderText = !string.IsNullOrWhiteSpace(tool.InputPlaceholder)
                    ? tool.InputPlaceholder
                    : DefaultPlaceholder;
                textInput.Text = "";
            }
            else
            {
                textInput.Visibility = Visibility.Collapsed;
            }

            // Powered-by link
            if (!string.IsNullOrWhiteSpace(tool.PoweredByText) &&
                !string.IsNullOrWhiteSpace(tool.PoweredByUrl))
            {
                linkPoweredBy.Content = tool.PoweredByText.Trim();
                linkPoweredBy.Tag = tool.PoweredByUrl.Trim();
                linkPoweredBy.Visibility = Visibility.Visible;
            }
            else
            {
                linkPoweredBy.Visibility = Visibility.Collapsed;
            }

            // Help hint (show if any option contains "help", case-insensitive)
            bool hasHelp = tool.Options.Any(o => o.Contains("help", StringComparison.OrdinalIgnoreCase));
            infoHelp.Title = Localizer.Get("Tools_HelpAvailable");
            btnShowHelp.Content = Localizer.Get("Tools_ShowHelp");
            infoHelp.IsOpen = hasHelp;

            // Show action buttons
            btnRun.Visibility = Visibility.Visible;
            btnUninstall.Visibility = Visibility.Visible;
        }

        private void ClearDetails()
        {
            _selectedTool = null;

            lblIcon.Text = "";
            lblTitle.Text = "";
            lblDescription.Text = "";

            comboOptions.Visibility = Visibility.Collapsed;
            comboOptions.Items.Clear();

            textInput.Visibility = Visibility.Collapsed;
            textInput.Text = "";

            linkPoweredBy.Visibility = Visibility.Collapsed;
            infoHelp.IsOpen = false;

            progressRing.IsActive = false;

            btnRun.Visibility = Visibility.Collapsed;
            btnUninstall.Visibility = Visibility.Collapsed;

            scrollDetails.Visibility = Visibility.Collapsed;
            panelPlaceholder.Visibility = Visibility.Visible;
        }

        // ---------------- Button handlers ----------------

        private async void btnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTool == null) return;

            if (!File.Exists(_selectedTool.ScriptPath))
            {
                lblStatus.Text = "Script not found: " + _selectedTool.ScriptPath;
                return;
            }

            btnRun.IsEnabled = false;
            btnUninstall.IsEnabled = false;
            progressRing.IsActive = true;
            lblStatus.Text = Localizer.Get("Tools_Running");

            try
            {
                bool useConsole = _selectedTool.UseConsole;
                bool useLog = _selectedTool.UseLog;

                // Selected option text (may carry host-suffix overrides)
                string optionArg = null;
                if (comboOptions.Visibility == Visibility.Visible && comboOptions.SelectedItem != null)
                {
                    optionArg = comboOptions.SelectedItem.ToString();

                    if (optionArg.EndsWith(" (console)", StringComparison.Ordinal))
                    {
                        useConsole = true; useLog = false;
                        optionArg = optionArg[..^" (console)".Length].Trim();
                    }
                    else if (optionArg.EndsWith(" (silent)", StringComparison.Ordinal))
                    {
                        useConsole = false; useLog = false;
                        optionArg = optionArg[..^" (silent)".Length].Trim();
                    }
                    else if (optionArg.EndsWith(" (log)", StringComparison.Ordinal))
                    {
                        useLog = true; useConsole = false;
                        optionArg = optionArg[..^" (log)".Length].Trim();
                    }
                }

                // Optional free text input
                string inputArg = null;
                if (_selectedTool.SupportsInput && textInput.Visibility == Visibility.Visible)
                {
                    var t = (textInput.Text ?? "").Trim();
                    if (!string.IsNullOrEmpty(t))
                        inputArg = t;
                }

                // Build positional argument string
                var extraArgs = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(optionArg))
                    extraArgs.Append(' ').Append(QuoteForPs(optionArg));
                if (!string.IsNullOrWhiteSpace(inputArg))
                    extraArgs.Append(' ').Append(QuoteForPs(inputArg));

                if (useLog)
                    Logger.BeginSection($"Running {_selectedTool.Title ?? _selectedTool.ScriptPath}");

                var output = await RunScriptAsync(_selectedTool.ScriptPath, extraArgs.ToString(), useConsole);

                lblStatus.Text = useConsole ? Localizer.Get("Tools_OpenedConsole")
                                     : useLog ? Localizer.Get("Tools_CompletedLog")
                                              : Localizer.Get("Tools_Done");

                if (!string.IsNullOrWhiteSpace(output))
                    Logger.Log(output, LogLevel.Info);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                Logger.Log("ERROR: " + ex.Message, LogLevel.Error);
            }
            finally
            {
                progressRing.IsActive = false;
                btnRun.IsEnabled = true;
                btnUninstall.IsEnabled = true;
            }
        }

        private async void btnUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTool == null) return;

            if (!File.Exists(_selectedTool.ScriptPath))
            {
                lblStatus.Text = "File already missing.";
                ClearDetails();
                LoadToolsAsync();
                return;
            }

            // Ask for confirmation before deleting
            var dialog = new ContentDialog
            {
                Title = Localizer.Get("Tools_UninstallTitle"),
                Content = Localizer.GetFormat("Tools_UninstallConfirm", _selectedTool.Title),
                PrimaryButtonText = Localizer.Get("Tools_UninstallYes"),
                CloseButtonText = Localizer.Get("Common_Cancel"),
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            try
            {
                File.Delete(_selectedTool.ScriptPath);
                Logger.Log($"Deleted extension: {_selectedTool.Title}", LogLevel.Info);
                ClearDetails();
                LoadToolsAsync();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Could not delete: " + ex.Message;
            }
        }

        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string scriptDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
            Directory.CreateDirectory(scriptDirectory);

            try
            {
                Process.Start("explorer.exe", scriptDirectory);
            }
            catch (Exception ex)
            {
                Logger.Log("Could not open folder: " + ex.Message, LogLevel.Error);
            }
        }

        // Runs the script with its help option and logs the output
        private async void btnShowHelp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTool == null) return;

            var helpOpt = _selectedTool.Options
                .FirstOrDefault(o => o.Contains("help", StringComparison.OrdinalIgnoreCase));
            if (helpOpt == null) return;

            Logger.BeginSection($"{_selectedTool.Title} — Help");
            await RunScriptAsync(_selectedTool.ScriptPath, " " + QuoteForPs(helpOpt), false);
        }

        private void linkPoweredBy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = linkPoweredBy.Tag?.ToString();
                if (string.IsNullOrWhiteSpace(url)) return;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Log("Could not open link: " + ex.Message, LogLevel.Error);
            }
        }

        // ---------------- Script execution ----------------

        private static Task<string> RunScriptAsync(string scriptPath, string positionalArgs, bool useConsole)
        {
            return Task.Run(() =>
            {
                var argsForPs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"{positionalArgs}";

                if (useConsole)
                {
                    var psi = new ProcessStartInfo("powershell.exe", "-NoExit " + argsForPs)
                    {
                        UseShellExecute = true,
                        CreateNoWindow = false
                    };
                    Process.Start(psi);
                    return "Launched in external console.";
                }
                else
                {
                    var psi = new ProcessStartInfo("powershell.exe", argsForPs)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    var sb = new StringBuilder();
                    using (var p = new Process { StartInfo = psi })
                    {
                        p.OutputDataReceived += (s, ev) =>
                        {
                            if (!string.IsNullOrEmpty(ev.Data))
                                Logger.Log(ev.Data, LogLevel.Info);
                        };

                        p.ErrorDataReceived += (s, ev) =>
                        {
                            if (!string.IsNullOrEmpty(ev.Data))
                            {
                                sb.AppendLine("ERROR: " + ev.Data);
                                Logger.Log("ERROR: " + ev.Data, LogLevel.Error);
                            }
                        };

                        p.Start();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        p.WaitForExit();
                    }

                    return sb.ToString();
                }
            });
        }

        private static string QuoteForPs(string value)
        {
            if (value == null) return "\"\"";
            var escaped = value.Replace("\"", "\\\"");
            return "\"" + escaped + "\"";
        }

        // ---------------- Metadata parsing ----------------

        // Reads the first 15 lines of a .ps1 script for # key: value headers
        private static ScriptMeta ReadMetadataFromScript(string scriptPath)
        {
            string description = "No description available.";
            var options = new List<string>();
            var category = ToolsCategory.All;
            bool useConsole = false, useLog = false, inputEnabled = false;
            string inputPh = "", poweredByText = "", poweredByUrl = "";

            try
            {
                foreach (var line in File.ReadLines(scriptPath).Take(15))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("# Description:", StringComparison.OrdinalIgnoreCase))
                        description = line[14..].Trim();
                    else if (line.StartsWith("# Category:", StringComparison.OrdinalIgnoreCase))
                        category = line[11..].Trim().ToLowerInvariant() switch
                        {
                            "pre" => ToolsCategory.Pre,
                            "mid" => ToolsCategory.Mid,
                            "tool" => ToolsCategory.Tool,
                            "post" => ToolsCategory.Post,
                            _ => ToolsCategory.All
                        };
                    else if (line.StartsWith("# Options:", StringComparison.OrdinalIgnoreCase))
                        options = line[10..].Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    else if (line.StartsWith("# Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = line[7..].Trim().ToLowerInvariant();
                        useConsole = raw == "console";
                        useLog = raw == "log";
                    }
                    else if (line.StartsWith("# Input:", StringComparison.OrdinalIgnoreCase))
                        inputEnabled = line[8..].Trim().ToLowerInvariant() is "true" or "yes" or "1";
                    else if (line.StartsWith("# InputPlaceholder:", StringComparison.OrdinalIgnoreCase))
                        inputPh = line[19..].Trim();
                    else if (line.StartsWith("# PoweredBy:", StringComparison.OrdinalIgnoreCase))
                        poweredByText = line[12..].Trim();
                    else if (line.StartsWith("# PoweredUrl:", StringComparison.OrdinalIgnoreCase))
                        poweredByUrl = line[13..].Trim();
                    else if (line.StartsWith('#') && description == "No description available.")
                        description = line.TrimStart('#').Trim();
                }
            }
            catch { }

            return new ScriptMeta
            {
                Description = description,
                Options = options,
                Category = category,
                UseConsole = useConsole,
                UseLog = useLog,
                SupportsInput = inputEnabled,
                InputPlaceholder = inputPh,
                PoweredByText = poweredByText,
                PoweredByUrl = poweredByUrl
            };
        }

        // Maps keywords in script names to emoji icons
        private static readonly Dictionary<string, string> _iconMap = new()
        {
            ["debloat"] = "🧹",      ["network"] = "🌐",
            ["explorer"] = "📂",     ["update"] = "🔄",
            ["context"] = "📋",      ["backup"] = "💾",
            ["security"] = "🛡️",    ["performance"] = "⚡",
            ["privacy"] = "🔒",      ["app"] = "📦",
            ["setup"] = "⚙️",       ["restore"] = "♻️",
            ["cache"] = "🗑️",       ["defender"] = "🛡️",
            ["power"] = "🔌",        ["install"] = "📥",
            ["boot"] = "🚀",         ["clean"] = "🧼"
        };

        private static string PickIconForScript(string name)
        {
            name = (name ?? "").ToLowerInvariant();
            foreach (var kv in _iconMap)
                if (name.Contains(kv.Key)) return kv.Value;
            return "🔧";
        }
    }
}
