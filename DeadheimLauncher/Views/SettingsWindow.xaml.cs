using System.Windows;
using Microsoft.Win32;
using DeadheimLauncher.Models;
using DeadheimLauncher.Services;

namespace DeadheimLauncher.Views;

public partial class SettingsWindow : Window
{
    private readonly LauncherSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ProfileService _profileService;
    private readonly Profile _profile;

    /// <summary>
    /// Fica true quando o jogador desinstala os mods. A janela principal precisa
    /// saber para redesenhar a lista: as versões instaladas sumiram do perfil e
    /// continuar mostrando "instalado" seria mentira.
    /// </summary>
    public bool ModsForamRemovidos { get; private set; }

    public SettingsWindow(LauncherSettings settings, SettingsService settingsService,
        ProfileService profileService, Profile profile)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        _profileService = profileService;
        _profile = profile;

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

    /// <summary>
    /// Apaga os mods instalados. Pede confirmação porque é irreversível do ponto
    /// de vista do jogador — mesmo que o próximo Jogar baixe tudo de novo, são
    /// centenas de MB e alguns minutos de espera que ninguém quer por engano.
    /// </summary>
    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var resposta = MessageBox.Show(this,
            "Isso apaga os mods instalados pelo launcher. Nada da sua pasta do Valheim " +
            "e nenhum personagem é afetado, e o próximo Jogar baixa tudo de novo.\n\n" +
            "Feche o Valheim antes de continuar.\n\nDesinstalar agora?",
            "Desinstalar mods", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (resposta != MessageBoxResult.Yes) return;

        try
        {
            UninstallButton.IsEnabled = false;
            var quantidade = _profileService.RemoverModsInstalados(_profile);
            ModsForamRemovidos = true;

            MessageBox.Show(this,
                quantidade == 0
                    ? "Não havia mod instalado. Está tudo limpo."
                    : $"Pronto: {quantidade} mod(s) removidos. Clique em Jogar para instalar de novo.",
                "Desinstalar mods", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // O caso real é o Valheim aberto segurando as .dll. ErroAmigavel já
            // traduz "sendo usado por outro processo" em algo acionável.
            MessageBox.Show(this, ErroAmigavel.Descrever(ex, "desinstalar os mods"),
                "Desinstalar mods", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UninstallButton.IsEnabled = true;
        }
    }
}
