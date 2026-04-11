using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Winslopr.Helpers;

namespace Winslopr.Views
{
    public sealed partial class FeaturesPage : Page, IMainActions, ISearchable, IView
    {
        // Full tree (source of truth). Never modified by filtering.
        private ObservableCollection<FeatureTreeItem> _rootItems;

        // What the TreeView actually displays is a filtered subset of _rootItems.
        // Needed because WinUI 3 crashes when TreeViewItem.Visibility is changed at runtime.
        // Instead we control which items appear via these two collections.
        private readonly ObservableCollection<FeatureTreeItem> _filteredRootItems = new();

        private bool _treeChecked;
        private FeatureTreeItem _contextMenuItem;
        private MenuFlyout _contextMenuFlyout;
        private string _lastSearchQuery = "";

        // Used by MainWindow to pass all items to the log action buttons.
        public ObservableCollection<FeatureTreeItem> RootItems => _rootItems;

        public FeaturesPage()
        {
            InitializeComponent();
            _contextMenuFlyout = CreateContextMenu();
            WireProfileComboBox();
            Loaded += FeaturesPage_Loaded;
        }

        private async void FeaturesPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_rootItems == null)
                await InitializeAppState();
        }

        public async Task InitializeAppState()
        {
            // Await the task started in App.OnLaunched which is already running since app start.
            var (features, plugins) = await App.PreloadTask;

            _rootItems = new ObservableCollection<FeatureTreeItem>(features);
            if (plugins.Children.Count > 0)
                _rootItems.Add(plugins);

            treeFeatures.ItemsSource = _filteredRootItems;
            RebuildFilteredTree(); // populate display + expand all roots
        }

        // -- IMainActions -----------------------------------------

        public async Task AnalyzeAsync()
        {
            Logger.Clear();
            await FeatureNodeManager.AnalyzeAll(_rootItems);
            await PluginManager.AnalyzeAllPlugins(_rootItems);

            // Unlock issues filter once we have actual results.
            chkIssuesOnly.IsEnabled = true;

            // Pull counters and compute totals.
            int featTotal = FeatureNodeManager.TotalChecked;
            int featIssues = FeatureNodeManager.IssuesFound;
            int plugTotal = PluginManager.TotalChecked;
            int plugIssues = PluginManager.IssuesFound;
            int total = featTotal + plugTotal;
            int issues = featIssues + plugIssues;
            int ok = total - issues;

            // Log summary
            Logger.Log(string.Empty);
            Logger.Log($"🔎 {Localizer.Get("Analysis_Complete")}", LogLevel.Info);
            Logger.Log(new string('=', 45), LogLevel.Info);
            Logger.Log($"  {Localizer.Get("Analysis_WindowsFeatures")} : {featTotal - featIssues} / {featTotal} OK" +
                       (featIssues > 0 ? $"  ({featIssues} {Localizer.Get("Analysis_Issues")})" : "  ✔"),
                       featIssues > 0 ? LogLevel.Warning : LogLevel.Info);
            Logger.Log($"  {Localizer.Get("Analysis_Plugins")} : {plugTotal - plugIssues} / {plugTotal} OK" +
                       (plugIssues > 0 ? $"  ({plugIssues} {Localizer.Get("Analysis_Issues")})" : "  ✔"),
                       plugIssues > 0 ? LogLevel.Warning : LogLevel.Info);
            Logger.Log(new string('-', 45), LogLevel.Info);
            Logger.Log($"  {Localizer.Get("Analysis_Total")} : {ok} / {total} OK" +
                       (issues > 0 ? $"  ({issues} {Localizer.Get("Analysis_Issues")})" : "  ✔"),
                       issues > 0 ? LogLevel.Error : LogLevel.Info);

            // Show Dialog, details stay in the logger
            string title = issues > 0 ? Localizer.Get("Analysis_TitleAttention") : Localizer.Get("Analysis_Complete");
            string message = issues > 0
                ? $"⚠ {Localizer.GetFormat("Analysis_IssuesFound", issues, total)}\n{Localizer.Get("Analysis_DetailsInLog")}"
                : $"✔ {Localizer.GetFormat("Analysis_AllGoodCount", total)}";

            await new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
                RequestedTheme = App.CurrentTheme
            }.ShowAsync();
        }

        public async Task FixAsync()
        {
            FeatureNodeManager.ResetFixRestore();
            PluginManager.ResetFixRestore();

            foreach (var item in _rootItems)
                await FeatureNodeManager.FixChecked(item);

            foreach (var item in _rootItems)
                await PluginManager.FixChecked(item);

            await ShowSummaryDialog("🔧", "Fix", "Summary_Fixed");
        }

        // -- Selection --------------------------------------------

        public void ToggleSelection()
        {
            foreach (var item in FeatureTreeItem.Flatten(_rootItems))
                item.IsChecked = _treeChecked;

            _treeChecked = !_treeChecked;
        }

        public async void RestoreSelection()
        {
            int checkedCount = FeatureTreeItem.CountCheckedLeaves(_rootItems);

            if (checkedCount == 0)
            {
                await new ContentDialog
                {
                    Title = Localizer.Get("Restore_Title"),
                    Content = Localizer.Get("Restore_NoItems"),
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                RequestedTheme = App.CurrentTheme
                }.ShowAsync();
                return;
            }

            var confirm = new ContentDialog
            {
                Title = Localizer.Get("Restore_ConfirmTitle"),
                Content = Localizer.GetFormat("Restore_ConfirmBody", checkedCount),
                PrimaryButtonText = Localizer.Get("Common_Yes"),
                CloseButtonText = Localizer.Get("Common_No"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            Logger.Clear();
            FeatureNodeManager.ResetFixRestore();
            PluginManager.ResetFixRestore();

            foreach (var item in _rootItems)
                FeatureNodeManager.RestoreChecked(item);

            await ShowSummaryDialog("↩️", "Restore", "Summary_Restored");
        }

        // -- Summary dialog ---------------------------------------

        // successEmoji and mode are passed by the caller (FixAsync / RestoreSelection)
        // so this one method handles both Fix and Restore summaries.
        private async Task ShowSummaryDialog(string successEmoji, string mode, string okLabelKey)
        {
            // Merge results from both managers: features + PS plugins
            int ok      = FeatureNodeManager.FixedCount   + PluginManager.FixedCount;
            int skipped = FeatureNodeManager.SkippedCount + PluginManager.SkippedCount;
            int failed  = FeatureNodeManager.FailedCount  + PluginManager.FailedCount;

            // Nothing happened and lets skip dialog
            if (ok + skipped + failed == 0) return;

            // Write one-line summary into the logger for the log panel
            Logger.Log(string.Empty);
            Logger.Log($"📊 {Localizer.GetFormat($"Summary_{mode}", ok, skipped, failed)}", LogLevel.Info);

            // Title changes to a warning if anything failed
            string title = failed > 0
                ? Localizer.Get($"Summary_{mode}TitleWarning")
                : Localizer.Get($"Summary_{mode}TitleDone");

            // Build the dialog body
            string message =
                $"{successEmoji} {Localizer.Get(okLabelKey)} : {ok}\n" +
                $"ℹ️ {Localizer.Get("Summary_Skipped")} : {skipped}\n" +
                $"❌ {Localizer.Get("Summary_Failed")} : {failed}\n" +
                $"─────────────────────────────\n" +
                (failed > 0
                    ? $"⚠ {Localizer.Get($"Summary_{mode}HasErrors")}"
                    : $"✔ {Localizer.Get($"Summary_{mode}AllGood")}");

            await new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
                RequestedTheme = App.CurrentTheme
            }.ShowAsync();
        }

        // -- Context menu -----------------------------------------

        // Built in C# because MenuFlyoutItem.Text needs Localizer at runtime,
        // and XAML x:Uid would require renaming all Context_* resw keys to Context_*.Text.
        private MenuFlyout CreateContextMenu()
        {
            var flyout = new MenuFlyout();

            void Add(string key, string glyph, RoutedEventHandler handler)
            {
                var item = new MenuFlyoutItem { Text = Localizer.Get(key), Icon = new FontIcon { Glyph = glyph } };
                item.Click += handler;
                flyout.Items.Add(item);
            }

            Add("Context_Analyze", "\uE773", ContextAnalyze_Click);
            Add("Context_Fix", "\uE90F", ContextFix_Click);
            Add("Context_Restore", "\uE72C", ContextRestore_Click);
            flyout.Items.Add(new MenuFlyoutSeparator());
            Add("Context_Help", "\uE897", ContextHelp_Click);

            return flyout;
        }

        private void ItemMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is FeatureTreeItem item)
            {
                _contextMenuItem = item;
                _contextMenuFlyout.ShowAt(sender as FrameworkElement, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
            }
        }

        private async void ContextAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_contextMenuItem == null) return;
            Logger.Clear();
            Logger.Log($"🔎 Analyzing: {_contextMenuItem.Name}", LogLevel.Info);

            if (_contextMenuItem.Children.Count == 0)
                await PluginManager.AnalyzePlugin(_contextMenuItem);
            else
                await PluginManager.AnalyzeAll(_contextMenuItem);

            await FeatureNodeManager.AnalyzeFeature(_contextMenuItem);
        }

        private async void ContextFix_Click(object sender, RoutedEventArgs e)
        {
            if (_contextMenuItem == null) return;
            Logger.Clear();
            Logger.Log($"🔧 Fixing: {_contextMenuItem.Name}", LogLevel.Info);

            if (_contextMenuItem.Children.Count == 0)
            {
                // Leaf node: fix the single item (plugin or feature)
                if (PluginManager.IsPluginNode(_contextMenuItem))
                    await PluginManager.FixPlugin(_contextMenuItem);
                else
                    await FeatureNodeManager.FixFeature(_contextMenuItem);
            }
            else
            {
                // Category: fix all checked descendants
                await FeatureNodeManager.FixFeature(_contextMenuItem);
                await PluginManager.FixChecked(_contextMenuItem);
            }
        }

        private async void ContextRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_contextMenuItem == null) return;
            Logger.Clear();
            Logger.Log($"↩️ Restoring: {_contextMenuItem.Name}", LogLevel.Info);

            if (_contextMenuItem.Children.Count == 0)
            {
                // Leaf node
                if (PluginManager.IsPluginNode(_contextMenuItem))
                    await PluginManager.RestorePlugin(_contextMenuItem);
                else
                    FeatureNodeManager.RestoreFeature(_contextMenuItem);
            }
            else
            {
                // Category: restore all checked descendants
                FeatureNodeManager.RestoreFeature(_contextMenuItem);
                await PluginManager.RestoreChecked(_contextMenuItem);
            }
        }

        private void ContextHelp_Click(object sender, RoutedEventArgs e)
        {
            if (_contextMenuItem == null) return;
            FeatureNodeManager.ShowHelp(_contextMenuItem);
        }

        // -- Filter -----------------------------------------------

        private void chkIssuesOnly_Changed(object sender, RoutedEventArgs e)
            => ApplySearch(_lastSearchQuery);

        // ISearchable: called by the global search bar in MainWindow.
        public void ApplySearch(string query)
        {
            if (_rootItems == null) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                _lastSearchQuery = (query ?? string.Empty).Trim();
                bool issuesOnly = chkIssuesOnly.IsChecked == true;

                foreach (var root in _rootItems)
                    ApplyFilter(root, _lastSearchQuery, issuesOnly);

                RebuildFilteredTree();
            });
        }

        // Recursively marks each node visible/invisible based on search text and filter.
        // A category is visible if any child matches, or if it matches itself.
        private static bool ApplyFilter(FeatureTreeItem item, string query, bool issuesOnly)
        {
            bool childVisible = false;
            foreach (var child in item.Children)
                childVisible |= ApplyFilter(child, query, issuesOnly);

            bool nameMatch = query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool issueMatch = !issuesOnly || item.Status == AnalysisStatus.NeedsFix;

            item.IsVisible = childVisible || (nameMatch && issueMatch);
            return item.IsVisible;
        }

        // Rebuilds the display collections after each filter pass.
        // See field comments for why we need _filteredRootItems instead of _rootItems directly.
        private void RebuildFilteredTree()
        {
            _filteredRootItems.Clear();
            foreach (var root in _rootItems)
            {
                if (root.IsVisible)
                {
                    _filteredRootItems.Add(root);
                    root.RefreshFilteredChildren();
                }
            }

            // ContainerFromItem only works after layout — run at low priority.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                foreach (var root in _filteredRootItems)
                    if (treeFeatures.ContainerFromItem(root) is TreeViewItem c)
                        c.IsExpanded = true;
            });
        }

        // -- IView ------------------------------------------------

        public async void RefreshView()
        {
            chkIssuesOnly.IsChecked = false;
            chkIssuesOnly.IsEnabled = false;
            _lastSearchQuery = "";
            Logger.Clear();
            await InitializeAppState(); // rebuilds _rootItems + calls RebuildFilteredTree
        }

        // -- Export / Import --------------------------------------

        public void ExportSelection(string filePath)
            => TreeSelectionTransferV1.ExportChecked(filePath, _rootItems);

        public int ImportSelection(string filePath)
        {
            TreeSelectionTransferV1.ImportChecked(filePath, _rootItems, clearFirst: true);
            return FeatureTreeItem.CountCheckedLeaves(_rootItems);
        }

        // -- Profiles ---------------------------------------------

        private void WireProfileComboBox()
        {
            var profileDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");

            comboProfiles.Items.Clear();
            comboProfiles.Items.Add(Localizer.Get("Profile_Export"));
            comboProfiles.Items.Add(Localizer.Get("Profile_Import"));

            if (Directory.Exists(profileDir))
                foreach (var file in Directory.GetFiles(profileDir, "*.sel"))
                    comboProfiles.Items.Add(Path.GetFileNameWithoutExtension(file));

            comboProfiles.Items.Add(Localizer.Get("Profile_OpenFolder"));
            comboProfiles.SelectionChanged += ComboProfiles_SelectionChanged;
        }

        private async void ComboProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (comboProfiles.SelectedItem is not string selected) return;

            var profileDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
            string exportLabel = Localizer.Get("Profile_Export");
            string importLabel = Localizer.Get("Profile_Import");
            string folderLabel = Localizer.Get("Profile_OpenFolder");

            if (selected == exportLabel)
            {
                var path = NativeMethods.ShowSaveDialog();
                if (path != null)
                {
                    ExportSelection(path);
                    Logger.Log($"✅ {Localizer.GetFormat("Profile_ExportedTo", Path.GetFileName(path))}", LogLevel.Info);
                }
            }
            else if (selected == importLabel)
            {
                var path = NativeMethods.ShowOpenDialog();
                if (path != null)
                {
                    int count = ImportSelection(path);
                    Logger.Log($"✅ {Localizer.GetFormat("Profile_ImportedFrom", count, Path.GetFileName(path))}", LogLevel.Info);
                }
            }
            else if (selected == folderLabel)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = profileDir,
                    UseShellExecute = true
                });
            }
            else
            {
                var profilePath = Path.Combine(profileDir, selected + ".sel");
                if (File.Exists(profilePath))
                {
                    int count = ImportSelection(profilePath);
                    Logger.Log($"✅ {Localizer.GetFormat("Profile_Loaded", selected, count)}", LogLevel.Info);
                }
            }

            comboProfiles.SelectedIndex = -1;
        }
    }
}