using System;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Dialogs
{
    public sealed partial class PermissionRequestDialog : ContentDialog
    {
        private readonly ResourceLoader _loader = new ResourceLoader();

        public PermissionRequestDialog(string origin, string permissionKind, bool isUserInitiated)
        {
            this.InitializeComponent();

            PermissionMessageText.Text = string.Format(
                GetString("PermissionRequestMessageFormat"),
                GetPermissionDisplayName(permissionKind));
            PermissionOriginText.Text = string.Format(
                GetString("PermissionRequestOriginFormat"),
                GetOriginDisplayName(origin));
            PermissionGestureText.Text = isUserInitiated
                ? GetString("PermissionRequestUserInitiated")
                : GetString("PermissionRequestNotUserInitiated");
        }

        public bool RememberDecision => RememberDecisionCheckBox.IsChecked == true;

        private string GetPermissionDisplayName(string permissionKind)
        {
            var key = "PermissionKind_" + permissionKind;
            var value = GetString(key);
            return value == key ? permissionKind : value;
        }

        private static string GetOriginDisplayName(string origin)
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }

            return origin;
        }

        private string GetString(string key)
        {
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
    }
}
