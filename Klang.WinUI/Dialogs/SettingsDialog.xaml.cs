using System;
using Microsoft.UI.Xaml.Controls;

namespace Klang_WinUI.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
    public event Action<string>? BackdropChanged;

    public SettingsDialog()
    {
        InitializeComponent();
    }

    private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackdropComboBox == null) return;

        if (BackdropComboBox.SelectedIndex == 0)
        {
            BackdropChanged?.Invoke("mica");
        }
        else if (BackdropComboBox.SelectedIndex == 1)
        {
            BackdropChanged?.Invoke("acrylic");
        }
        else
        {
            BackdropChanged?.Invoke("solid");
        }
    }
}
