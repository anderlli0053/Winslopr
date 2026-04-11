using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Winslopr.Helpers;

namespace Winslopr
{
    public static class DonationHelper
    {
        private const string DonateUrl =
            "https://www.paypal.com/donate/?hosted_button_id=BNVXAGPQ8CTR6";

        public static bool HasDonated() => SettingsHelper.HasFlag("donated");
        public static void SetDonationStatus(bool donated) => SettingsHelper.SetFlag("donated", donated);

        /// <summary>
        /// Shows a donation dialog with a dismiss checkbox.
        /// Call on app close; skips silently if the user opted out.
        /// </summary>
        public static async Task ShowDonationDialogAsync(XamlRoot xamlRoot)
        {
            var chk = new CheckBox
            {
                Content = Localizer.Get("Donation_AlreadyDonated")
            };

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = Localizer.Get("Donation_Body"),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(chk);

            var dialog = new ContentDialog
            {
                Title = Localizer.Get("Donation_Title"),
                Content = panel,
                PrimaryButtonText = Localizer.Get("Donation_Donate"),
                CloseButtonText = Localizer.Get("Donation_MaybeLater"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DonateUrl,
                    UseShellExecute = true
                });
            }

            if (chk.IsChecked == true)
                SetDonationStatus(true);
        }
    }
}
