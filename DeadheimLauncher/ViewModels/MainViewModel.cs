using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _gameProcessTimer;

    private LauncherSettings _settings = new();
    private ModManifest _manifest = new();
    private Profile _activeProfile = new();
    private bool _alterandoSelecao;

    /// <summary>
    /// Nome fixo do único perfil. O launcher atende um servidor só, então manter
    /// mais de um conjunto de mods não serviria para nada. Segue sendo "Default"
    /// para quem já usou versões anteriores não perder o que estava instalado.
    /// </summary>
    private const string PerfilUnico = "Default";

    public ObservableCollection<ModListItemViewModel> Mods { get; } = new();

    /// <summary>Mods do servidor: o que é preciso para jogar.</summary>
    public ObservableCollection<ModListItemViewModel> ModsDoServidor { get; } = new();

    /// <summary>Melhorias que o jogador escolhe se quer.</summary>
    public ObservableCollection<ModListItemViewModel> ModsOpcionais { get; } = new();

    /// <summary>
    /// Ferramentas de administração, em aba separada. Ficam fora do caminho de
    /// quem só quer entrar e jogar, e nenhuma é obrigatória.
    /// </summary>
    public ObservableCollection<ModListItemViewModel> ModsDeAdmin { get; } = new();

    /// <summary>
    /// Marca ou desmarca de uma vez a aba inteira. Obrigatórios são pulados: o
    /// servidor exige, e desmarcar levaria a ser recusado na entrada.
    /// </summary>
    [RelayCommand]
    private void AlternarTodos(string categoria)
    {
        var lista = categoria switch
        {
            "Opcional" => ModsOpcionais,
            "Admin" => ModsDeAdmin,
            _ => ModsDoServidor
        };

        var opcionais = lista.Where(m => !m.IsRequired).ToList();
        if (opcionais.Count == 0) return;

        // Se algum está desmarcado, marca todos; se já estão todos marcados,
        // desmarca. Um botão só, que faz o que a lista pede no momento.
        var marcarTodos = opcionais.Any(m => !m.IsEnabled);
        _alterandoSelecao = true;
        try
        {
            foreach (var mod in opcionais) mod.IsEnabled = marcarTodos;
        }
        finally
        {
            _alterandoSelecao = false;
        }
        PersistirSelecaoAtual();
    }

    [ObservableProperty]
    private string _statusText = "Iniciando...";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Jogar só libera depois que a lista de mods foi carregada e conferida.
    ///
    /// Não basta olhar IsBusy: se o manifest falhar ao carregar, a janela para
    /// de estar ocupada mas fica sem lista nenhuma — e apertar Jogar ali abriria
    /// o Valheim sem os mods do servidor, que é justamente o que o launcher
    /// existe para evitar.
    /// </summary>
    public bool PodeJogar => !IsBusy && !IsGameRunning && Mods.Count > 0;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(PodeJogar));

    [ObservableProperty]
    private bool _isGameRunning;

    partial void OnIsGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(PodeJogar));
        FecharJogoCommand.NotifyCanExecuteChanged();
    }

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

    // ---- atualização do próprio launcher ----

    private AtualizacaoDisponivel? _atualizacao;

    [ObservableProperty]
    private string? _avisoDeAtualizacao;

    public bool MostrarAtualizacao => !string.IsNullOrWhiteSpace(AvisoDeAtualizacao);

    partial void OnAvisoDeAtualizacaoChanged(string? value) => OnPropertyChanged(nameof(MostrarAtualizacao));

    /// <summary>
    /// Procura versão nova em segundo plano. Falha aqui é silenciosa de
    /// propósito: sem internet, o launcher tem que abrir e funcionar com o que
    /// já está no disco, não travar avisando que não conseguiu se atualizar.
    /// </summary>
    private async Task ProcurarAtualizacaoAsync()
    {
        try
        {
            _atualizacao = await new AutoAtualizacaoService(_http).ProcurarAsync();
            if (_atualizacao is not null)
                AvisoDeAtualizacao = $"Versão {_atualizacao.Versao} disponível " +
                                     $"(você está na {AutoAtualizacaoService.VersaoAtual}).";
        }
        catch
        {
            // Sem rede ou GitHub fora do ar: segue com a versão atual.
        }
    }

    [RelayCommand]
    private async Task AtualizarLauncherAsync()
    {
        if (_atualizacao is null) return;

        IsBusy = true;
        LimparErro();
        try
        {
            StatusText = "Baixando a atualização...";
            ProgressoTotal = 100;
            var progresso = new Progress<double>(p => ProgressoAtual = (int)p);

            var instalador = await new AutoAtualizacaoService(_http)
                .BaixarInstaladorAsync(_atualizacao, progresso);

            StatusText = "Instalando. O launcher vai reabrir sozinho.";
            AutoAtualizacaoService.ExecutarInstalador(instalador);

            // O instalador precisa substituir o executável em uso, então saímos.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ErroTexto = ErroAmigavel.Descrever(ex, "atualizar o launcher");
            StatusText = "Não foi possível atualizar.";
        }
        finally
        {
            ProgressoTotal = 0;
            IsBusy = false;
        }
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

        _gameProcessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gameProcessTimer.Tick += (_, _) => AtualizarEstadoDoJogo();
        AtualizarEstadoDoJogo();
        _gameProcessTimer.Start();
    }

    private void AtualizarEstadoDoJogo()
    {
        try { IsGameRunning = _launchService.IsGameRunning(); }
        catch { IsGameRunning = false; }
    }

    [RelayCommand(CanExecute = nameof(IsGameRunning))]
    private void FecharJogo()
    {
        try
        {
            StatusText = _launchService.RequestGameClose()
                ? "Fechando o Valheim com segurança..."
                : "O Valheim está aberto, mas não respondeu ao pedido para fechar.";
        }
        catch (Exception ex)
        {
            StatusText = ErroAmigavel.Descrever(ex, "fechar o Valheim");
        }

        AtualizarEstadoDoJogo();
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusText = "Carregando configurações...";
        try
        {
            _settings = _settingsService.Load();
            FastLinkCleanupService.RemoveLegacyFiles(PerfilUnico);

            StatusText = "Baixando lista de mods do servidor...";
            _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);

            CarregarPerfil();
            StatusText = "Pronto.";

            // Depois de a janela já estar utilizável: checar atualização não
            // pode atrasar a abertura.
            _ = ProcurarAtualizacaoAsync();
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

    /// <summary>
    /// Carrega o perfil único e monta a lista de mods.
    ///
    /// O launcher atende um servidor só, então não existe motivo para o jogador
    /// manter mais de um conjunto de mods. A pasta de perfil continua existindo
    /// porque é ela que mantém tudo fora da instalação do Valheim — mas é uma só
    /// e não aparece na interface.
    /// </summary>
    private void CarregarPerfil()
    {
        _activeProfile = _profileService.LoadOrCreate(PerfilUnico);

        foreach (var itemAntigo in Mods)
            itemAntigo.PropertyChanged -= AoAlterarSelecaoDoMod;
        Mods.Clear();
        ModsDoServidor.Clear();
        ModsOpcionais.Clear();
        ModsDeAdmin.Clear();

        foreach (var entry in _manifest.AllMods)
        {
            var enabled = entry.Required || _activeProfile.EnabledModIds.Contains(entry.Id);
            var item = new ModListItemViewModel(entry, enabled);
            item.PropertyChanged += AoAlterarSelecaoDoMod;

            // Mods fica com tudo: é a lista que instala e sincroniza. As três
            // coleções por categoria existem só para a interface.
            Mods.Add(item);

            var destino = entry.Category switch
            {
                ModCategory.Admin => ModsDeAdmin,
                ModCategory.Opcional => ModsOpcionais,
                _ => ModsDoServidor
            };
            destino.Add(item);
        }

        OnPropertyChanged(nameof(PodeJogar));
    }

    private void AoAlterarSelecaoDoMod(object? sender, PropertyChangedEventArgs e)
    {
        if (!_alterandoSelecao && e.PropertyName == nameof(ModListItemViewModel.IsEnabled))
            PersistirSelecaoAtual();
    }

    /// <summary>
    /// Checkboxes são configuração do perfil, não estado temporário da tela. Persistir no
    /// clique impede que a atualização do manifesto (feita antes de Jogar) reconstrua a lista
    /// com a seleção antiga e silenciosamente ignore opcionais ou ferramentas de admin.
    /// </summary>
    private void PersistirSelecaoAtual()
    {
        if (Mods.Count == 0 || string.IsNullOrWhiteSpace(_activeProfile.Name)) return;
        _activeProfile.EnabledModIds = Mods.Where(m => m.IsEnabled)
            .Select(m => m.Entry.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _profileService.Save(_activeProfile);
    }

    [RelayCommand]
    private async Task RefreshManifestAsync()
    {
        IsBusy = true;
        StatusText = "Atualizando lista de mods...";
        try
        {
            PersistirSelecaoAtual();
            _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);
            CarregarPerfil();
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
            PersistirSelecaoAtual();
            // Rebusca o manifest antes de tudo: é assim que uma versão nova do
            // modpack publicada pelo servidor chega ao jogador sem ele fazer
            // nada. Se a rede falhar aqui, seguimos com a lista que já temos em
            // vez de impedir o jogo de abrir.
            StatusText = "Verificando a versão do servidor...";
            var versaoAnterior = _manifest.PackVersion;
            try
            {
                _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);
                CarregarPerfil();

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

            StatusText = "Preparando o Valheim...";
            var valheimPath = _launchService.ResolveValheimPath(_settings);
            _launchService.PrepararJogo(valheimPath, _activeProfile.Name);

            StatusText = "Iniciando o Valheim...";
            _launchService.LaunchGame(valheimPath, _activeProfile.Name, _settings);
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
        _activeProfile.InstalledVersions.TryGetValue(modVm.Entry.Id, out var instalada);
        return PrecisaAtualizar(instalada, modVm.Entry.Version, EstaNoDisco(modVm.Entry));
    }

    /// <summary>
    /// Onde procurar o mod instalado.
    ///
    /// O carregador não vira pasta com o id dele: funde na raiz de jogo do
    /// perfil. Procurá-lo como se fosse um plugin dava "não está no disco" toda
    /// vez, e o BepInEx era rebaixado a cada partida — 49 MB por clique em Jogar.
    /// </summary>
    private bool EstaNoDisco(ModEntry entry)
    {
        if (entry.Target != InstallTarget.Plugins)
            return ModInstallerService.PacoteEstruturalEstaInstalado(entry, _activeProfile.Name);

        return Directory.Exists(Path.Combine(AppPaths.ProfilePluginsDir(_activeProfile.Name), entry.Id));
    }

    /// <summary>
    /// Confere se cada mod habilitado ainda está no disco. A digital sozinha não
    /// basta: o jogador pode ter limpado a pasta, ou um antivírus pode ter
    /// removido um arquivo, e aí o manifest "já aplicado" seria mentira.
    /// </summary>
    private bool TudoPresenteNoDisco()
        => Mods.Where(m => m.IsEnabled).All(m => EstaNoDisco(m.Entry));

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

        // Se o manifest é o mesmo da última vez e tudo continua no disco, não há
        // nada a fazer — nem uma consulta. É o que impede o launcher de
        // reconsultar a origem de todo mod sem versão fixada a cada partida, que
        // era o que estourava o limite da API do GitHub e fazia o download falhar.
        var digital = _manifest.CalcularDigitalDaInstalacao(
            Mods.Where(m => m.IsEnabled).Select(m => m.Entry.Id));
        if (_activeProfile.ManifestAplicado == digital && TudoPresenteNoDisco())
        {
            StatusText = "Mods já estão na versão do servidor.";
            return falhas;
        }

        // Fora isso, entra na fila só o que falta ou está fora da versão pedida.
        var habilitados = Mods.Where(m => m.IsEnabled).Where(PrecisaAtualizar).ToList();

        if (habilitados.Count == 0)
        {
            _activeProfile.ManifestAplicado = digital;
            _profileService.Save(_activeProfile);
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

        // Só registra a digital se tudo passou. Com falha no meio, a próxima
        // partida tem que tentar de novo em vez de dar o conjunto por aplicado.
        if (falhas.Count == 0)
        {
            _activeProfile.ManifestAplicado = digital;
        }

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

}
