using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeadheimLauncher.Models;
using DeadheimLauncher.Services;

namespace DeadheimLauncher.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly HttpClient _http = new();
    private readonly SettingsService _settingsService = new();
    private readonly ProfileService _profileService = new();
    private readonly ManifestService _manifestService;
    private readonly ModInstallerService _installerService;
    private readonly ValheimLaunchService _launchService = new();

    private LauncherSettings _settings = new();
    private ModManifest _manifest = new();
    private Profile _activeProfile = new();

    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<ModListItemViewModel> Mods { get; } = new();

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private string _statusText = "Iniciando...";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Erro mostrado dentro da janela, num aviso que o jogador fecha quando
    /// quiser. Popup é pior aqui: rouba o foco, some com um Enter distraído
    /// levando a mensagem junto, e some também com o texto que dizia o que
    /// fazer. Aqui a mensagem fica na tela até ser lida.
    /// </summary>
    [ObservableProperty]
    private string? _erroTexto;

    /// <summary>Detalhe longo (lista de mods que falharam), recolhido por padrão.</summary>
    [ObservableProperty]
    private string? _erroDetalhe;

    public bool MostrarErro => !string.IsNullOrWhiteSpace(ErroTexto);
    public bool MostrarErroDetalhe => !string.IsNullOrWhiteSpace(ErroDetalhe);

    partial void OnErroTextoChanged(string? value) => OnPropertyChanged(nameof(MostrarErro));
    partial void OnErroDetalheChanged(string? value) => OnPropertyChanged(nameof(MostrarErroDetalhe));

    [RelayCommand]
    private void LimparErro()
    {
        ErroTexto = null;
        ErroDetalhe = null;
    }

    /// <summary>Quantos mods já foram processados nesta instalação.</summary>
    [ObservableProperty]
    private int _progressoAtual;

    /// <summary>Total de mods a processar. Zero enquanto não há instalação em curso.</summary>
    [ObservableProperty]
    private int _progressoTotal;

    /// <summary>
    /// Instalar ~40 mods leva minutos. Sem barra, a janela parece travada e o
    /// jogador fecha no meio, deixando o perfil pela metade.
    /// </summary>
    public bool MostrarProgresso => ProgressoTotal > 0;

    public string ProgressoTexto => ProgressoTotal > 0 ? $"{ProgressoAtual} de {ProgressoTotal}" : "";

    partial void OnProgressoAtualChanged(int value) => OnPropertyChanged(nameof(ProgressoTexto));

    partial void OnProgressoTotalChanged(int value)
    {
        OnPropertyChanged(nameof(MostrarProgresso));
        OnPropertyChanged(nameof(ProgressoTexto));
    }

    public MainViewModel()
    {
        _manifestService = new ManifestService(_http);
        _installerService = new ModInstallerService(_http, new GitHubReleaseService(_http), new ThunderstoreService(_http));
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusText = "Carregando configurações...";
        try
        {
            _settings = _settingsService.Load();

            var profiles = _profileService.ListProfiles();
            if (profiles.Count == 0)
            {
                _profileService.LoadOrCreate("Default");
                profiles = _profileService.ListProfiles();
            }

            Profiles.Clear();
            foreach (var p in profiles) Profiles.Add(p);

            var startProfile = profiles.Contains(_settings.LastActiveProfile) ? _settings.LastActiveProfile : profiles[0];

            StatusText = "Baixando lista de mods do servidor...";
            _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);

            await SwitchProfileAsync(startProfile);
            StatusText = "Pronto.";
        }
        catch (Exception ex)
        {
            StatusText = ErroAmigavel.Descrever(ex, "carregar a lista de mods do servidor");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (value is not null && value != _activeProfile.Name)
        {
            _ = SwitchProfileAsync(value);
        }
    }

    private async Task SwitchProfileAsync(string profileName)
    {
        _activeProfile = _profileService.LoadOrCreate(profileName);
        SelectedProfile = profileName;
        _settings.LastActiveProfile = profileName;
        _settingsService.Save(_settings);

        Mods.Clear();
        foreach (var entry in _manifest.AllMods)
        {
            var enabled = entry.Required || _activeProfile.EnabledModIds.Contains(entry.Id);
            Mods.Add(new ModListItemViewModel(entry, enabled));
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void CreateProfile()
    {
        var name = PromptForName("Nome do novo perfil:", "Novo Perfil");
        if (string.IsNullOrWhiteSpace(name) || Profiles.Contains(name)) return;

        _profileService.LoadOrCreate(name);
        Profiles.Add(name);
        SelectedProfile = name;
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null) return;
        var name = PromptForName("Nome do perfil duplicado:", $"{SelectedProfile} - cópia");
        if (string.IsNullOrWhiteSpace(name) || Profiles.Contains(name)) return;

        _profileService.Duplicate(_activeProfile, name);
        Profiles.Add(name);
        SelectedProfile = name;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1) return;
        var toDelete = SelectedProfile;

        var result = MessageBox.Show($"Excluir o perfil '{toDelete}'? Essa ação não pode ser desfeita.",
            "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _profileService.Delete(toDelete);
        Profiles.Remove(toDelete);
        SelectedProfile = Profiles[0];
    }

    [RelayCommand]
    private async Task RefreshManifestAsync()
    {
        IsBusy = true;
        StatusText = "Atualizando lista de mods...";
        try
        {
            _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);
            await SwitchProfileAsync(_activeProfile.Name);
            StatusText = "Lista de mods atualizada.";
        }
        catch (Exception ex)
        {
            StatusText = ErroAmigavel.Descrever(ex, "atualizar a lista de mods");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        IsBusy = true;
        LimparErro();
        try
        {
            // Rebusca o manifest antes de tudo: é assim que uma versão nova do
            // modpack publicada pelo servidor chega ao jogador sem ele fazer
            // nada. Se a rede falhar aqui, seguimos com a lista que já temos em
            // vez de impedir o jogo de abrir.
            StatusText = "Verificando a versão do servidor...";
            var versaoAnterior = _manifest.PackVersion;
            try
            {
                _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);
                await SwitchProfileAsync(_activeProfile.Name);

                if (!string.IsNullOrWhiteSpace(_manifest.PackVersion) && _manifest.PackVersion != versaoAnterior)
                    StatusText = $"Servidor atualizado para {_manifest.PackVersion}. Aplicando...";

                // Mod que o servidor tirou do pack precisa sair do disco também.
                // Se ficar, o BepInEx carrega assim mesmo e o jogador pode ser
                // recusado pelo anticheat ou quebrar com os que ficaram.
                var removidos = _profileService.RemoverModsForaDoManifest(
                    _activeProfile, _manifest.AllMods.Select(m => m.Id));

                if (removidos.Count > 0)
                    StatusText = $"{removidos.Count} mod(s) removido(s) pelo servidor: {string.Join(", ", removidos.Take(3))}"
                                 + (removidos.Count > 3 ? "..." : "");
            }
            catch (Exception ex)
            {
                StatusText = "Sem contato com o servidor — usando a lista já baixada. " + ErroAmigavel.Descrever(ex);
            }

            var falhas = await InstallEnabledModsAsync();

            var obrigatoriosQueFalharam = falhas.Where(f => f.Obrigatorio).ToList();
            if (obrigatoriosQueFalharam.Count > 0)
            {
                // Entrar sem um mod que o servidor exige = ser recusado na porta,
                // ou desincronizar depois. Melhor parar aqui e dizer qual foi.
                ErroTexto = $"{obrigatoriosQueFalharam.Count} mod(s) obrigatório(s) não instalaram. " +
                            "Entrar no servidor sem eles não vai funcionar.";
                ErroDetalhe = string.Join("\n", obrigatoriosQueFalharam.Select(f => $"• {f.Nome}: {f.Motivo}"));
                StatusText = "Instalação incompleta.";
                return;
            }

            if (falhas.Count > 0)
            {
                StatusText = $"{falhas.Count} mod(s) opcional(is) não instalaram — seguindo sem eles.";
            }

            StatusText = "Sincronizando mods com o Valheim...";
            var valheimPath = _launchService.ResolveValheimPath(_settings);
            _launchService.SyncProfileToGame(valheimPath, _activeProfile.Name);

            StatusText = "Iniciando o Valheim...";
            _launchService.LaunchGame();
            StatusText = "Valheim iniciado.";
        }
        catch (Exception ex)
        {
            ErroTexto = ErroAmigavel.Descrever(ex);
            StatusText = "Não foi possível iniciar o Valheim.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Decide se o mod precisa ser baixado de novo.
    ///
    /// Versão fixada pelo manifest é comparação direta com o que o perfil
    /// registrou ter instalado. Sem versão fixada ("sempre a mais recente") não
    /// dá para saber sem consultar a origem, então reinstala — é o preço de não
    /// pinar. Arquivos sumidos do disco também forçam reinstalação, senão o
    /// perfil ficaria dizendo que tem um mod que não está mais lá.
    /// </summary>
    private bool PrecisaAtualizar(ModListItemViewModel modVm)
    {
        var pastaDoMod = Path.Combine(AppPaths.ProfilePluginsDir(_activeProfile.Name), modVm.Entry.Id);
        var pastaNaRaiz = Path.Combine(AppPaths.ProfileGameRootDir(_activeProfile.Name), modVm.Entry.Id);
        var estaNoDisco = Directory.Exists(pastaDoMod) || Directory.Exists(pastaNaRaiz);

        _activeProfile.InstalledVersions.TryGetValue(modVm.Entry.Id, out var instalada);

        return PrecisaAtualizar(instalada, modVm.Entry.Version, estaNoDisco);
    }

    /// <summary>
    /// Regra pura, separada para poder ser testada: errar para "não precisa"
    /// significa jogador preso numa versão velha sem nenhum aviso, que é o pior
    /// tipo de defeito aqui.
    /// </summary>
    public static bool PrecisaAtualizar(string? versaoInstalada, string? versaoDoManifest, bool estaNoDisco)
    {
        if (!estaNoDisco) return true;
        if (string.IsNullOrWhiteSpace(versaoInstalada)) return true;

        // Sem versão fixada não dá para saber se mudou sem consultar a origem.
        if (string.IsNullOrWhiteSpace(versaoDoManifest)) return true;

        return !string.Equals(versaoInstalada, versaoDoManifest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Instala os mods marcados e devolve o que falhou. O chamador decide o que
    /// fazer: mod obrigatório que não instalou significa entrar no servidor e
    /// ser recusado (ou desincronizar), então isso não pode passar batido.
    /// </summary>
    private async Task<List<(string Nome, bool Obrigatorio, string Motivo)>> InstallEnabledModsAsync()
    {
        var falhas = new List<(string, bool, string)>();

        // Persiste quais mods ficaram habilitados/desabilitados neste perfil.
        _activeProfile.EnabledModIds = Mods.Where(m => m.IsEnabled).Select(m => m.Entry.Id).ToList();
        _profileService.Save(_activeProfile);

        // Só entra na fila o que está faltando ou fora da versão que o servidor
        // pede. Reinstalar os ~40 mods a cada clique em Jogar levaria minutos e
        // centenas de MB à toa — e é o que fazia antes.
        var habilitados = Mods.Where(m => m.IsEnabled).Where(PrecisaAtualizar).ToList();

        if (habilitados.Count == 0)
        {
            StatusText = "Mods já estão na versão do servidor.";
            return falhas;
        }

        ProgressoAtual = 0;
        ProgressoTotal = habilitados.Count;

        foreach (var modVm in habilitados)
        {
            StatusText = $"Atualizando {modVm.Entry.Name}...";
            var progress = new Progress<ModInstallProgress>(p => modVm.StatusText = p.Status);
            try
            {
                var installedVersion = await _installerService.InstallAsync(modVm.Entry, _activeProfile.Name, progress);
                _activeProfile.InstalledVersions[modVm.Entry.Id] = installedVersion;
                _profileService.Save(_activeProfile);
            }
            catch (Exception ex)
            {
                modVm.StatusText = ErroAmigavel.Descrever(ex);
                falhas.Add((modVm.Entry.Name, modVm.Entry.Required, ErroAmigavel.Descrever(ex)));
            }
            finally
            {
                // Avança mesmo em falha: a barra mede quanto da fila já passou,
                // não quantos deram certo. Travar aqui daria sensação de pendurado.
                ProgressoAtual++;
            }
        }

        ProgressoTotal = 0;

        // Remove do disco mods que foram desabilitados neste perfil.
        foreach (var modVm in Mods.Where(m => !m.IsEnabled))
        {
            var dir = Path.Combine(AppPaths.ProfilePluginsDir(_activeProfile.Name), modVm.Entry.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            _activeProfile.InstalledVersions.Remove(modVm.Entry.Id);
        }
        _profileService.Save(_activeProfile);

        return falhas;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings, _settingsService)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
        _settings = _settingsService.Load();
    }

    /// <summary>
    /// Pede o nome do perfil já validando dentro do diálogo. Antes, nome vazio
    /// ou repetido fazia o chamador dar return em silêncio: a janela fechava e
    /// nada acontecia, sem dizer por quê.
    /// </summary>
    private string? PromptForName(string message, string defaultValue)
    {
        var dialog = new Views.InputDialog(
            message,
            defaultValue,
            hint: "Cada perfil guarda sua própria seleção de mods.",
            validar: nome =>
            {
                var erro = Views.InputDialog.ValidarNomeDePerfil(nome);
                if (erro is not null) return erro;

                return Profiles.Contains(nome, StringComparer.OrdinalIgnoreCase)
                    ? $"Já existe um perfil chamado “{nome}”."
                    : null;
            })
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.ResponseText : null;
    }
}
