using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Taview
{
    public partial class OptionsWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private ThemeOption _selectedTheme;

        public OptionsWindow()
        {
            InitializeComponent();

            // Set dark title bar if needed
            SourceInitialized += (s, e) =>
            {
                UpdateTitleBarTheme();
            };

            // Load current settings
            _selectedTheme = AppSettings.Instance.Theme;
            ThemeComboBox.SelectedIndex = (int)_selectedTheme;
        }

        private void UpdateTitleBarTheme()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int value = AppSettings.Instance.ShouldUseDarkMode() ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }

        private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selectedTheme = (ThemeOption)ThemeComboBox.SelectedIndex;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.Theme = _selectedTheme;
            AppSettings.Instance.Save();

            // Apply theme
            ThemeManager.ApplyTheme(_selectedTheme);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

