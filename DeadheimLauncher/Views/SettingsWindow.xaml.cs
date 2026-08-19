using System.Windows;
using Microsoft.Win32;
using DeadheimLauncher.Models;
using DeadheimLauncher.Services;

namespace DeadheimLauncher.Views;

public partial class SettingsWindow : Window
{
    private readonly LauncherSettings _settings;
    private readonly SettingsService _settingsService;

    public SettingsWindow(LauncherSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;

        ValheimPathBox.Text = settings.ValheimPath ?? "";
        ManifestUrlBox.Text = settings.ManifestUrl;
        ServerHostBox.Text = settings.ServerHost;
        ServerPortBox.Text = settings.ServerPort.ToString();
        ServerPasswordBox.Password = settings.ServerPassword ?? "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta do Valheim" };
        if (dialog.ShowDialog() == true)
        {
            ValheimPathBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.ValheimPath = string.IsNullOrWhiteSpace(ValheimPathBox.Text) ? null : ValheimPathBox.Text;
        _settings.ManifestUrl = ManifestUrlBox.Text.Trim();
        _settings.ServerHost = ServerHostBox.Text.Trim();
        if (!int.TryParse(ServerPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Informe uma porta válida entre 1 e 65535.", "Configurações");
            return;
        }
        _settings.ServerPort = port;
        _settings.ServerPassword = ServerPasswordBox.Password;
        _settingsService.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
