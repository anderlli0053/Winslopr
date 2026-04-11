using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using Winslopr.Helpers;
using Winslopr.Services;
using Winsloprr.Services;

namespace Winslopr.Views
{
    public sealed partial class SettingsPage : Page
    {
        private bool _suppressLangChange;
        private bool _suppressThemeChange;
        private bool _suppressBackdropChange;
        private bool _suppressCompactChange;

        public SettingsPage()
        {
            InitializeComponent();
            txtVersion.Text = AppInfo.VersionString;
            PopulateLanguages();
            PopulateThemes();
            PopulateBackdrops();
            PopulateCompact();
            PopulateSystemInfo();
        }

        // -- Language ------------------------------------------------

        private void PopulateLanguages()
        {
            _suppressLangChange = true;
            cboLanguage.Items.Clear();
            string current = Localizer.CurrentLanguage;
            int selectedIndex = 0;

            int i = 0;
            foreach (string tag in Localizer.GetAvailableLanguages())
            {
                string label;
                try
                {
                    var ci = new CultureInfo(tag);
                    label = ci.NativeName;
                    if (label.Length > 0)
                        label = char.ToUpper(label[0]) + label[1..];
                }
                catch { label = tag; }

                cboLanguage.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
                if (tag.Equals(current, StringComparison.OrdinalIgnoreCase))
                    selectedIndex = i;
                i++;
            }

            cboLanguage.SelectedIndex = selectedIndex;
            _suppressLangChange = false;

            // Update translator credit shown in the Language expander
            string name    = Localizer.Get("Language_TranslatorName");
            string website = Localizer.Get("Language_TranslatorWebsite");
            bool hasCredit = !string.IsNullOrWhiteSpace(name) && name != "Language_TranslatorName";
            lnkTranslator.Content     = hasCredit ? name : "—";
            lnkTranslator.NavigateUri = hasCredit && !string.IsNullOrWhiteSpace(website)
                                        && website != "Language_TranslatorWebsite"
                                        //uri parsing is a bit strict, but we want to avoid malformed links here
                                        && Uri.TryCreate(website, UriKind.Absolute, out var translatorUri)
                ? translatorUri : null;
        }

        private async void CboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLangChange) return;
            if (cboLanguage.SelectedItem is ComboBoxItem item && item.Tag is string langCode)
            {
                if (langCode.Equals(Localizer.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                    return;
                await Localizer.SwitchLanguageAsync(langCode, this.XamlRoot);
            }
        }

        // -- Theme ---------------------------------------------------

        private void PopulateThemes()
        {
            _suppressThemeChange = true;
            string current = SettingsHelper.Get("theme") ?? "System";

            cboTheme.Items.Add(new ComboBoxItem { Content = Localizer.Get("Settings_ThemeSystem"), Tag = "System" });
            cboTheme.Items.Add(new ComboBoxItem { Content = Localizer.Get("Settings_ThemeLight"), Tag = "Light" });
            cboTheme.Items.Add(new ComboBoxItem { Content = Localizer.Get("Settings_ThemeDark"), Tag = "Dark" });

            cboTheme.SelectedIndex = current switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };
            _suppressThemeChange = false;
        }

        private void CboTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressThemeChange) return;
            if (cboTheme.SelectedItem is ComboBoxItem item && item.Tag is string theme)
            {
                var value = theme == "System" ? null : theme;
                SettingsHelper.Set("theme", value);
                if (App.MainWindow is Window window)
                    App.ApplyTheme(window, value);
            }
        }

        // -- Backdrop ------------------------------------------------

        private void PopulateBackdrops()
        {
            _suppressBackdropChange = true;
            string current = SettingsHelper.Get("backdrop") ?? "BaseAlt";

            cboBackdrop.Items.Add(new ComboBoxItem { Content = "Mica", Tag = "Base" });
            cboBackdrop.Items.Add(new ComboBoxItem { Content = "Mica Alt", Tag = "BaseAlt" });

            cboBackdrop.SelectedIndex = current == "Base" ? 0 : 1;
            _suppressBackdropChange = false;
        }

        private void CboBackdrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressBackdropChange) return;
            if (cboBackdrop.SelectedItem is ComboBoxItem item && item.Tag is string kind)
            {
                var micaKind = kind == "Base" ? MicaKind.Base : MicaKind.BaseAlt;
                if (App.MainWindow is Window window)
                    window.SystemBackdrop = new MicaBackdrop { Kind = micaKind };
                SettingsHelper.Set("backdrop", kind);
            }
        }

        // -- Compact spacing -----------------------------------------

        private void PopulateCompact()
        {
            _suppressCompactChange = true;
            toggleCompact.IsOn = SettingsHelper.HasFlag("compactSpacing");
            _suppressCompactChange = false;
        }

        private void ToggleCompact_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressCompactChange) return;
            SettingsHelper.SetFlag("compactSpacing", toggleCompact.IsOn);
            lblCompactHint.Text = toggleCompact.IsOn ? "Takes effect after restart" : "Reduce space between UI elements";
        }

        // -- Compatibility -------------------------------------------

        private void PopulateSystemInfo()
        {
            bool compatible = WindowsVersion.IsWindows11OrLater();
            txtCompatSubtext.Text = WindowsVersion.GetDisplayString();
            if (compatible)
            {
                icoCompat.Glyph = "\uE73E";  // checkmark
                icoCompat.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 15, 123, 15));
                txtCompatTitle.Text = Localizer.Get("Settings_CompatOk");
            }
            else
            {
                icoCompat.Glyph = "\uE7BA";  // warning
                icoCompat.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 194, 136, 0));
                txtCompatTitle.Text = Localizer.Get("Settings_CompatWarn");
            }
        }

        // -- Plugins -------------------------------------------------

        private async void BtnPlugins_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PluginsDialog { XamlRoot = this.XamlRoot, RequestedTheme = App.CurrentTheme };
            await dialog.ShowAsync();
        }

        private void BtnLegacy_Click(object sender, RoutedEventArgs e)
        {
            var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var exe = System.IO.Path.Combine(dir, "Winslopr.Legacy.exe");
            if (System.IO.File.Exists(exe))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }

        // -- About ---------------------------------------------------

        private void BtnGitHub_Click(object sender, RoutedEventArgs e)
            => ExternalLinks.OpenGitHub();

        private void BtnUpdates_Click(object sender, RoutedEventArgs e)
            => ExternalLinks.OpenUpdateCheck(AppInfo.RawVersion);

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
            => ExternalLinks.OpenHelp();

        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
            => ExternalLinks.OpenFeedback();
    }
}
