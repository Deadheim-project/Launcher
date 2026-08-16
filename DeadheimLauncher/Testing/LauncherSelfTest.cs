using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Diagnostics;
using DeadheimLauncher.Models;
using DeadheimLauncher.Services;

namespace DeadheimLauncher.Testing;

/// <summary>
/// Verificação headless do launcher, no espírito do ServerSelfTestRunner do mod:
/// roda a pilha real (perfis, manifest, resolução de versão, download, extração,
/// sincronização com a pasta do jogo) e reporta PASS/FAIL, sem ninguém precisar
/// abrir a janela e clicar.
///
/// Roda com:  DeadheimLauncher.exe --selftest          (inclui rede)
///            DeadheimLauncher.exe --selftest --offline (só o que não depende de rede)
///
/// Toda a persistência é redirecionada para uma pasta temporária via
/// AppPaths.UseRoot, então rodar o self-test nunca mexe nos perfis reais do
/// jogador. Sai com código 0 se tudo passou, 1 se algo falhou.
/// </summary>
public static class LauncherSelfTest
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;
    private static bool _fullInstall;
    private static readonly StringBuilder Log = new();

    public static async Task<int> RunAsync(bool includeNetwork, bool fullInstall = false, string? sandboxRoot = null)
    {
        AttachConsoleIfPossible();

        var sandbox = Path.Combine(sandboxRoot ?? Path.GetTempPath(),
            "DeadheimLauncher-selftest-" + Guid.NewGuid().ToString("N"));
        AppPaths.UseRoot(sandbox);
        Directory.CreateDirectory(sandbox);

        Write($"SELFTEST: sandbox em {sandbox}");
        Write($"SELFTEST: testes de rede {(includeNetwork ? "HABILITADOS" : "desabilitados (--offline)")}");
        if (fullInstall) Write("SELFTEST: instalação completa do perfil HABILITADA (--full)");
        Write("");
        _fullInstall = fullInstall;

        try
        {
            RunSettingsChecks();
            RunProfileChecks();
            await RunManifestChecks();
            RunMarkOfTheWebChecks();
            RunGameSyncChecks();
            RunConfigRoutingChecks();
            RunUpdateRuleChecks();
            RunAutoUpdateChecks();
            RunCleanupChecks();

            if (includeNetwork)
            {
                await RunNetworkChecks();
            }
            else
            {
                Skip("Thunderstore: resolve a versão mais recente");
                Skip("Thunderstore: baixa e instala pacote real");
                Skip("GitHub: resolve o release mais recente");
            }

            await RunUiChecks();
        }
        catch (Exception ex)
        {
            Check("self-test roda até o fim sem exceção não tratada", false, ex.ToString());
        }
        finally
        {
            TryDelete(sandbox);
        }

        return Report();
    }

    // -------------------------------------------------------------- atualizacao

    /// <summary>
    /// O launcher rebusca o manifest antes de jogar e só rebaixa o que mudou de
    /// versão. Duas formas de errar, ambas ruins: dizer "precisa" sempre volta a
    /// baixar 40 mods a cada partida; dizer "não precisa" quando mudou deixa o
    /// jogador numa versão diferente da do servidor, que é desync na certa.
    /// </summary>
    private static void RunUpdateRuleChecks()
    {
        Check("atualizar: versão igual à do servidor não rebaixa",
            !ViewModels.MainViewModel.PrecisaAtualizar("2.29.0", "2.29.0", estaNoDisco: true));

        Check("atualizar: versão diferente da do servidor rebaixa",
            ViewModels.MainViewModel.PrecisaAtualizar("2.28.0", "2.29.0", estaNoDisco: true));

        Check("atualizar: comparação de versão ignora caixa",
            !ViewModels.MainViewModel.PrecisaAtualizar("1.0.0-RC1", "1.0.0-rc1", estaNoDisco: true));

        // Perfil dizendo que tem o mod, mas a pasta sumiu (jogador limpou, antivírus
        // removeu): tem que reinstalar, senão entra no servidor sem o mod.
        Check("atualizar: arquivos ausentes no disco forçam reinstalação",
            ViewModels.MainViewModel.PrecisaAtualizar("2.29.0", "2.29.0", estaNoDisco: false));

        Check("atualizar: mod nunca instalado é baixado",
            ViewModels.MainViewModel.PrecisaAtualizar(null, "2.29.0", estaNoDisco: true));

        Check("atualizar: mod sem versão fixada é sempre rebaixado",
            ViewModels.MainViewModel.PrecisaAtualizar("2.29.0", null, estaNoDisco: true));
    }

    // ------------------------------------------------- atualizacao do launcher

    /// <summary>
    /// Comparar versão como texto é a armadilha clássica: "1.0.10" &lt; "1.0.9" em
    /// ordem alfabética, então a atualização pararia de funcionar exatamente
    /// depois do décimo lançamento — sem erro nenhum aparecer.
    /// </summary>
    private static void RunAutoUpdateChecks()
    {
        Check("launcher: versão maior é oferecida",
            AutoAtualizacaoService.EhMaisNova("v1.0.8", "1.0.7"));

        Check("launcher: versão igual não é oferecida",
            !AutoAtualizacaoService.EhMaisNova("v1.0.7", "1.0.7"));

        Check("launcher: versão anterior não é oferecida",
            !AutoAtualizacaoService.EhMaisNova("v1.0.6", "1.0.7"));

        Check("launcher: 1.0.10 é reconhecida como maior que 1.0.9",
            AutoAtualizacaoService.EhMaisNova("v1.0.10", "1.0.9"));

        Check("launcher: salto de versão maior é reconhecido",
            AutoAtualizacaoService.EhMaisNova("v2.0.0", "1.9.9"));

        Check("launcher: prefixo v é opcional",
            AutoAtualizacaoService.EhMaisNova("1.1.0", "1.0.0"));

        Check("launcher: sufixo de pré-lançamento não quebra a comparação",
            AutoAtualizacaoService.EhMaisNova("v1.1.0-beta", "1.0.0"));

        Check("launcher: tag sem formato de versão é ignorada",
            !AutoAtualizacaoService.EhMaisNova("ultima", "1.0.0"));

        Check("launcher: versão do binário é legível",
            System.Text.RegularExpressions.Regex.IsMatch(AutoAtualizacaoService.VersaoAtual, @"^\d+\.\d+\.\d+$"),
            AutoAtualizacaoService.VersaoAtual);
    }

    // --------------------------------------------------------------- limpeza

    /// <summary>
    /// Quando o servidor tira um mod do pack, ele tem que sumir do disco do
    /// jogador. Ficar para trás não é inofensivo: o BepInEx carrega tudo que
    /// está em plugins, então o mod removido continua ativo — e pode ser
    /// justamente o que o servidor mandou tirar.
    /// </summary>
    private static void RunCleanupChecks()
    {
        const string perfil = "LimpezaTest";
        var service = new ProfileService();
        var profile = service.LoadOrCreate(perfil);

        var plugins = AppPaths.ProfilePluginsDir(perfil);
        foreach (var id in new[] { "raidsystem", "modremovido" })
        {
            Directory.CreateDirectory(Path.Combine(plugins, id));
            File.WriteAllText(Path.Combine(plugins, id, $"{id}.dll"), "dll");
            profile.InstalledVersions[id] = "1.0.0";
            profile.EnabledModIds.Add(id);
        }

        // A árvore do BepInEx vive na raiz de jogo do perfil e não é um mod. A
        // limpeza não pode encostar nela — os nomes ali (core, config) nunca vão
        // constar do manifest.
        var core = Path.Combine(AppPaths.ProfileBepInExDir(perfil), "core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "BepInEx.Preloader.dll"), "preloader");
        File.WriteAllText(Path.Combine(AppPaths.ProfileGameDir(perfil), "winhttp.dll"), "injetor");
        service.Save(profile);

        var removidos = service.RemoverModsForaDoManifest(profile, new[] { "raidsystem", "bepinexpack-valheim" });

        Check("limpeza: mod fora do manifest é apagado do disco",
            !Directory.Exists(Path.Combine(plugins, "modremovido")), string.Join(", ", removidos));
        Check("limpeza: mod que continua no manifest é preservado",
            File.Exists(Path.Combine(plugins, "raidsystem", "raidsystem.dll")));
        Check("limpeza: a árvore do BepInEx não é apagada por engano",
            File.Exists(Path.Combine(core, "BepInEx.Preloader.dll")) &&
            File.Exists(Path.Combine(AppPaths.ProfileGameDir(perfil), "winhttp.dll")));

        var recarregado = service.LoadOrCreate(perfil);
        Check("limpeza: perfil deixa de listar o mod removido",
            !recarregado.InstalledVersions.ContainsKey("modremovido")
            && !recarregado.EnabledModIds.Contains("modremovido"));
        Check("limpeza: perfil mantém o mod que ficou",
            recarregado.InstalledVersions.ContainsKey("raidsystem"));

        // Nada mudou no manifest: não pode sair apagando por precaução.
        var semMudanca = service.RemoverModsForaDoManifest(recarregado, new[] { "raidsystem", "bepinexpack-valheim" });
        Check("limpeza: sem mudança no manifest, nada é apagado", semMudanca.Count == 0);
    }

    // ------------------------------------------------------------------ config

    /// <summary>
    /// Um pacote que traz config/ está entregando a configuração do servidor.
    /// Ela precisa chegar em BepInEx/config; parando em plugins/&lt;mod&gt;/config/ o
    /// BepInEx ignora e o jogador roda com regras diferentes das do servidor,
    /// sem nenhum erro aparecer. Daí o teste.
    /// </summary>
    private static void RunConfigRoutingChecks()
    {
        const string perfil = "ConfigTest";
        new ProfileService().LoadOrCreate(perfil);

        // Simula um pacote já extraído: dll na raiz e config/ junto.
        var modDir = Path.Combine(AppPaths.ProfilePluginsDir(perfil), "raidsystem");
        Directory.CreateDirectory(Path.Combine(modDir, "config", "RaidSystem"));
        File.WriteAllText(Path.Combine(modDir, "RaidSystem.dll"), "dll");
        File.WriteAllText(Path.Combine(modDir, "config", "Detalhes.RaidSystem.cfg"), "dano=99");
        File.WriteAllText(Path.Combine(modDir, "config", "RaidSystem", "RaidSystem.Default.cfg"), "padrao=1");

        typeof(ModInstallerService)
            .GetMethod("MoverConfigParaOPerfil", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { modDir, perfil });

        var configPerfil = AppPaths.ProfileConfigDir(perfil);
        Check("config: sai de dentro da pasta do mod",
            !Directory.Exists(Path.Combine(modDir, "config")) && File.Exists(Path.Combine(modDir, "RaidSystem.dll")));
        Check("config: subpastas do config são preservadas",
            File.Exists(Path.Combine(configPerfil, "Detalhes.RaidSystem.cfg")) &&
            File.Exists(Path.Combine(configPerfil, "RaidSystem", "RaidSystem.Default.cfg")));

        // O config agora fica dentro do BepInEx do perfil, que é a árvore que o
        // jogo carrega. Não passa mais pela pasta do Valheim.
        Check("config: fica dentro do BepInEx do perfil",
            configPerfil.StartsWith(AppPaths.ProfileBepInExDir(perfil), StringComparison.OrdinalIgnoreCase),
            configPerfil);

        var jogo = Path.Combine(AppPaths.Root, "ConfigValheim");
        var configJogo = Path.Combine(jogo, "BepInEx", "config");
        Directory.CreateDirectory(configJogo);
        File.WriteAllText(Path.Combine(configJogo, "MinhasTeclas.cfg"), "tecla=F5");

        var coreDoPerfil = Path.Combine(AppPaths.ProfileBepInExDir(perfil), "core");
        Directory.CreateDirectory(coreDoPerfil);
        File.WriteAllText(Path.Combine(coreDoPerfil, "BepInEx.Preloader.dll"), "preloader");
        File.WriteAllText(Path.Combine(jogo, "valheim.exe"), "jogo");

        new ValheimLaunchService().PrepararJogo(jogo, perfil);

        Check("config: o do jogador continua intacto",
            File.ReadAllText(Path.Combine(configJogo, "MinhasTeclas.cfg")) == "tecla=F5");
        Check("config: o do servidor não é despejado na pasta do jogo",
            !File.Exists(Path.Combine(configJogo, "Detalhes.RaidSystem.cfg")));
    }

    // ---------------------------------------------------------------------- ui

    /// <summary>
    /// Abre a janela principal de verdade, fora da tela, e deixa o ciclo de vida
    /// do WPF rodar (InitializeComponent, Loaded, InitializeAsync, layout).
    ///
    /// Todo o resto do self-test é headless e nunca tocaria nisto: erro de XAML,
    /// binding para propriedade que não existe, recurso estático faltando —
    /// nada disso quebra a compilação, só aparece quando a janela abre. Um
    /// binding quebrado é silencioso em produção (o campo fica vazio), então
    /// aqui os avisos de binding do WPF são capturados e viram falha.
    /// </summary>
    private static async Task RunUiChecks()
    {
        // Abrir janela exige sessao interativa com desktop. Runner de CI nao tem,
        // entao aqui isso e SKIP declarado - e nao um PASS falso nem um FAIL que
        // travaria o release por algo que a maquina simplesmente nao consegue rodar.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            Skip("UI: janela principal abre sem erro de XAML (CI sem desktop interativo)");
            Skip("UI: lista de mods é populada (CI sem desktop interativo)");
            Skip("UI: nenhum binding quebrado (CI sem desktop interativo)");
            return;
        }

        var errosDeBinding = new List<string>();
        var listener = new BindingErrorListener(errosDeBinding);

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            Views.MainWindow? janela = null;
            try
            {
                janela = new Views.MainWindow
                {
                    // Fora da área visível: o teste não deve piscar janela na cara
                    // de quem rodou, mas precisa de um Show() real para o WPF
                    // fazer measure/arrange e avaliar os bindings.
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false
                };
                janela.Show();

                Check("UI: janela principal abre sem erro de XAML", true);

                // Dá tempo do Loaded disparar e do InitializeAsync popular a lista.
                await Task.Delay(2500);
                janela.UpdateLayout();

                var itens = (janela.DataContext as ViewModels.MainViewModel)?.Mods.Count ?? 0;
                Check("UI: lista de mods é populada", itens > 0, $"{itens} itens");
            }
            finally
            {
                janela?.Close();
            }
        }
        catch (Exception ex)
        {
            Check("UI: janela principal abre sem erro de XAML", false, ex.Message);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }

        Check("UI: nenhum binding quebrado", errosDeBinding.Count == 0,
            errosDeBinding.Count == 0 ? "" : string.Join(" | ", errosDeBinding.Take(4)));

    }


    /// <summary>Captura os avisos que o WPF emite quando um binding não resolve.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly List<string> _destino;
        public BindingErrorListener(List<string> destino) => _destino = destino;

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _destino.Add(message.Trim());
        }
    }

    // ---------------------------------------------------------------- settings

    private static void RunSettingsChecks()
    {
        var service = new SettingsService();

        var created = service.Load();
        Check("settings.json é criado na primeira execução", File.Exists(AppPaths.SettingsFile));
        Check("settings novo tem perfil ativo padrão", created.LastActiveProfile == "Default", created.LastActiveProfile);

        created.ValheimPath = @"C:\Fake\Valheim";
        created.LastActiveProfile = "Hardcore";
        service.Save(created);

        var reloaded = service.Load();
        Check("settings sobrevivem a um round-trip de disco",
            reloaded.ValheimPath == @"C:\Fake\Valheim" && reloaded.LastActiveProfile == "Hardcore",
            $"path={reloaded.ValheimPath} profile={reloaded.LastActiveProfile}");

        File.WriteAllText(AppPaths.SettingsFile, "{ isto não é json válido");
        var recovered = service.Load();
        Check("settings corrompidos caem no padrão em vez de crashar", recovered.LastActiveProfile == "Default");

        service.Save(new LauncherSettings());
    }

    // ---------------------------------------------------------------- perfis

    private static void RunProfileChecks()
    {
        var service = new ProfileService();

        var def = service.LoadOrCreate("Default");
        Check("perfil novo cria profile.json e pasta plugins",
            File.Exists(AppPaths.ProfileFile("Default")) && Directory.Exists(AppPaths.ProfilePluginsDir("Default")));
        Check("perfil novo começa sem mods habilitados", def.EnabledModIds.Count == 0);

        def.EnabledModIds.Add("npcs");
        def.InstalledVersions["npcs"] = "1.0.0";
        service.Save(def);

        var reloaded = service.LoadOrCreate("Default");
        Check("mods habilitados e versões persistem no perfil",
            reloaded.EnabledModIds.Contains("npcs") && reloaded.InstalledVersions["npcs"] == "1.0.0");

        // Um arquivo de mod de mentira, pra provar que duplicar copia os plugins junto.
        var fakeModDir = Path.Combine(AppPaths.ProfilePluginsDir("Default"), "npcs");
        Directory.CreateDirectory(fakeModDir);
        File.WriteAllText(Path.Combine(fakeModDir, "Npcs.dll"), "conteudo de teste");

        service.Duplicate(reloaded, "Hardcore");
        Check("duplicar copia a lista de mods",
            service.LoadOrCreate("Hardcore").EnabledModIds.Contains("npcs"));
        Check("duplicar copia os arquivos de plugin do disco",
            File.Exists(Path.Combine(AppPaths.ProfilePluginsDir("Hardcore"), "npcs", "Npcs.dll")));

        Check("listar perfis enxerga os dois",
            service.ListProfiles().Contains("Default") && service.ListProfiles().Contains("Hardcore"));

        service.Rename("Hardcore", "Hardcore2");
        var renamed = service.LoadOrCreate("Hardcore2");
        Check("renomear move a pasta e corrige o nome interno",
            renamed.Name == "Hardcore2" && !Directory.Exists(AppPaths.ProfileDir("Hardcore")));
        Check("renomear preserva os plugins",
            File.Exists(Path.Combine(AppPaths.ProfilePluginsDir("Hardcore2"), "npcs", "Npcs.dll")));

        service.Delete("Hardcore2");
        Check("excluir remove a pasta do perfil", !Directory.Exists(AppPaths.ProfileDir("Hardcore2")));
        Check("excluir não afeta os outros perfis", service.ListProfiles().Contains("Default"));
    }

    // ---------------------------------------------------------------- manifest

    private static async Task RunManifestChecks()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var service = new ManifestService(http);

        // URL inexistente de propósito: exercita a cadeia de fallback.
        const string bogusUrl = "https://invalid.deadheim.example/manifest.json";

        var fromSample = await service.GetManifestAsync(bogusUrl);
        Check("manifest cai no sample embutido quando a URL falha", fromSample.AllMods.Any(),
            $"{fromSample.AllMods.Count()} mods");
        Check("sample traz pelo menos um mod obrigatório", fromSample.AllMods.Any(m => m.Required));
        Check("sample traz pelo menos um mod opcional", fromSample.AllMods.Any(m => !m.Required));
        Check("sample tem um mod de fonte GitHub", fromSample.OwnMods.Any(m => m.Source == ModSource.GitHub));
        Check("sample tem um mod de fonte Thunderstore",
            fromSample.ThunderstoreMods.Any(m => m.Source == ModSource.Thunderstore));

        var ownMod = fromSample.OwnMods.FirstOrDefault();
        Check("mod próprio traz owner/repo do GitHub preenchidos",
            ownMod is not null && !string.IsNullOrWhiteSpace(ownMod.GitHubOwner) && !string.IsNullOrWhiteSpace(ownMod.GitHubRepo),
            ownMod is null ? "nenhum mod próprio" : $"{ownMod.GitHubOwner}/{ownMod.GitHubRepo}");

        var tsMod = fromSample.ThunderstoreMods.FirstOrDefault();
        Check("mod Thunderstore traz namespace/nome preenchidos",
            tsMod is not null && !string.IsNullOrWhiteSpace(tsMod.ThunderstoreNamespace) && !string.IsNullOrWhiteSpace(tsMod.ThunderstoreName));

        // Cache tem prioridade sobre o sample quando a rede está fora.
        AppPaths.EnsureDirs();
        File.WriteAllText(AppPaths.ManifestCacheFile, """
            { "ownMods": [], "thunderstoreMods": [
              { "id": "do-cache", "name": "DoCache", "required": false,
                "source": "Thunderstore", "thunderstoreNamespace": "X", "thunderstoreName": "Y" } ] }
            """);
        var fromCache = await service.GetManifestAsync(bogusUrl);
        Check("manifest usa o cache local quando a rede está fora",
            fromCache.AllMods.Any(m => m.Id == "do-cache"));
        File.Delete(AppPaths.ManifestCacheFile);
    }

    // ---------------------------------------------------------------- MOTW

    private static void RunMarkOfTheWebChecks()
    {
        var dir = Path.Combine(AppPaths.Root, "motw");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Fake.dll");
        const string payload = "conteudo binario de mentira";
        File.WriteAllText(file, payload);

        // Simula o que um navegador faz ao baixar: grava o alternate data stream.
        var marked = TryWriteZoneIdentifier(file);

        var touched = MarkOfTheWeb.UnblockDirectory(dir);
        Check("desbloqueio percorre os arquivos da pasta", touched == 1, $"{touched} arquivos");
        Check("desbloqueio não altera o conteúdo do arquivo", File.ReadAllText(file) == payload);

        if (marked)
        {
            Check("Zone.Identifier some depois do desbloqueio", !ZoneIdentifierExists(file));
        }
        else
        {
            Skip("Zone.Identifier some depois do desbloqueio (sistema de arquivos sem ADS)");
        }
    }

    // ---------------------------------------------------------------- sync com o jogo

    private static void RunGameSyncChecks()
    {
        var launch = new ValheimLaunchService();
        var profiles = new ProfileService();
        profiles.LoadOrCreate("SyncTest");

        var modDir = Path.Combine(AppPaths.ProfilePluginsDir("SyncTest"), "npcs");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "Npcs.dll"), "dll");
        Directory.CreateDirectory(Path.Combine(modDir, "config"));
        File.WriteAllText(Path.Combine(modDir, "config", "npc.cfg"), "cfg");

        var fakeGame = Path.Combine(AppPaths.Root, "FakeValheim");
        Directory.CreateDirectory(fakeGame);
        File.WriteAllText(Path.Combine(fakeGame, "valheim.exe"), "jogo");

        // Sem o BepInEx baixado no perfil, preparar tem que reclamar em vez de
        // deixar o jogador abrir o jogo sem carregador nenhum.
        var threw = false;
        try { launch.PrepararJogo(fakeGame, "SyncTest"); }
        catch (BepInExNotFoundException) { threw = true; }
        Check("preparar sem o BepInEx do perfil dá erro claro", threw);

        // BepInEx do perfil, com os arquivos que o Doorstop precisa.
        var coreDoPerfil = Path.Combine(AppPaths.ProfileBepInExDir("SyncTest"), "core");
        Directory.CreateDirectory(coreDoPerfil);
        File.WriteAllText(Path.Combine(coreDoPerfil, "BepInEx.Preloader.dll"), "preloader");
        File.WriteAllText(Path.Combine(AppPaths.ProfileGameDir("SyncTest"), "winhttp.dll"), "injetor");

        // Mods que o JOGADOR instalou por conta própria: não podem ser tocados.
        var doJogador = Path.Combine(fakeGame, "BepInEx", "plugins", "ModDoJogador");
        Directory.CreateDirectory(doJogador);
        File.WriteAllText(Path.Combine(doJogador, "Dele.dll"), "nao mexa");

        launch.PrepararJogo(fakeGame, "SyncTest");

        Check("preparar não instala mod dentro do Valheim",
            !Directory.Exists(Path.Combine(fakeGame, "BepInEx", "plugins", "npcs")));
        Check("preparar preserva os mods que o jogador instalou",
            File.Exists(Path.Combine(doJogador, "Dele.dll")));
        Check("preparar leva o injetor para junto do valheim.exe",
            File.Exists(Path.Combine(fakeGame, "winhttp.dll")));

        // Sem isso, abrir pela Steam carregaria os mods do servidor sem o
        // jogador ter pedido.
        var doorstopIni = Path.Combine(fakeGame, "doorstop_config.ini");
        Check("preparar deixa o carregamento desligado por padrão",
            File.Exists(doorstopIni) && File.ReadAllText(doorstopIni).Contains("enabled=false"));

        var argumentos = ValheimLaunchService.MontarArgumentosDeInicializacao("SyncTest");
        Check("argumentos ligam o Doorstop apontando para o perfil",
            argumentos.Contains("--doorstop-enabled true")
            && argumentos.Contains(AppPaths.ProfileBepInExDir("SyncTest"))
            && argumentos.Contains("BepInEx.Preloader.dll"),
            argumentos);

        var settings = new LauncherSettings { ValheimPath = fakeGame };
        Check("caminho do Valheim configurado à mão é respeitado",
            launch.ResolveValheimPath(settings) == fakeGame);

        var missing = new LauncherSettings { ValheimPath = Path.Combine(AppPaths.Root, "NaoExiste") };
        var resolveThrew = false;
        try { launch.ResolveValheimPath(missing); }
        catch (ValheimNotFoundException) { resolveThrew = true; }
        catch (Exception) { /* Steam instalado na máquina: achou por detecção, tudo bem */ }
        Check("caminho inválido cai na detecção automática ou erra explicitamente",
            resolveThrew || Directory.Exists(launch.ResolveValheimPath(new LauncherSettings())));
    }

    // ---------------------------------------------------------------- rede

    private static async Task RunNetworkChecks()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var thunderstore = new ThunderstoreService(http);
        var github = new GitHubReleaseService(http);
        var installer = new ModInstallerService(http, github, thunderstore);

        // Jötunn é o pacote Thunderstore mais estável da comunidade Valheim:
        // serve de alvo confiável pra provar que a resolução e o download funcionam.
        var jotunn = new ModEntry
        {
            Id = "jotunn",
            Name = "Jotunn",
            Source = ModSource.Thunderstore,
            ThunderstoreNamespace = "ValheimModding",
            ThunderstoreName = "Jotunn"
        };

        ResolvedModVersion? resolved = null;
        try
        {
            resolved = await thunderstore.GetLatestAsync(jotunn);
            Check("Thunderstore: resolve a versão mais recente",
                !string.IsNullOrWhiteSpace(resolved.Version) && resolved.DownloadUrl.StartsWith("https://"),
                $"v{resolved.Version} -> {resolved.DownloadUrl}");
        }
        catch (Exception ex)
        {
            Check("Thunderstore: resolve a versão mais recente", false, ex.Message);
        }

        if (resolved is not null)
        {
            try
            {
                new ProfileService().LoadOrCreate("NetTest");
                var version = await installer.InstallAsync(jotunn, "NetTest");
                var installedDir = Path.Combine(AppPaths.ProfilePluginsDir("NetTest"), "jotunn");
                var dlls = Directory.Exists(installedDir)
                    ? Directory.GetFiles(installedDir, "*.dll", SearchOption.AllDirectories)
                    : Array.Empty<string>();

                Check("Thunderstore: baixa e instala pacote real",
                    version == resolved.Version && dlls.Length > 0,
                    $"v{version}, {dlls.Length} dll(s): {string.Join(", ", dlls.Select(Path.GetFileName).Take(5))}");
            }
            catch (Exception ex)
            {
                Check("Thunderstore: baixa e instala pacote real", false, ex.Message);
            }
        }
        else
        {
            Skip("Thunderstore: baixa e instala pacote real");
        }

        // Prova o caminho do GitHub Releases contra um repositório público que
        // sabidamente publica assets. O repo do Deadheim entra aqui assim que
        // tiver o primeiro release.
        var githubMod = new ModEntry
        {
            Id = "bepinex",
            Name = "BepInEx",
            Source = ModSource.GitHub,
            GitHubOwner = "BepInEx",
            GitHubRepo = "BepInEx",
            AssetPattern = ".zip"
        };

        try
        {
            var ghResolved = await github.GetLatestAsync(githubMod);
            Check("GitHub: resolve o release mais recente",
                !string.IsNullOrWhiteSpace(ghResolved.Version) && ghResolved.DownloadUrl.StartsWith("https://"),
                $"{ghResolved.Version} -> {ghResolved.FileName}");

            // O release do BepInEx traz Windows, Linux e macOS no mesmo lote:
            // é o caso exato que quebrava quando pegávamos o primeiro asset.
            Check("GitHub: não escolhe asset de outra plataforma",
                !ghResolved.FileName.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                !ghResolved.FileName.Contains("macos", StringComparison.OrdinalIgnoreCase),
                ghResolved.FileName);
        }
        catch (Exception ex)
        {
            Check("GitHub: resolve o release mais recente", false, ex.Message);
            Skip("GitHub: não escolhe asset de outra plataforma");
        }

        await RunDeadheimRepoCheck(github);
        await RunRealManifestChecks(http, installer);
    }

    /// <summary>
    /// O teste que de fato responde "o launcher funciona pro Deadheim?": pega o
    /// manifest real do servidor e resolve cada mod contra a API de verdade, com
    /// as versões fixadas do pack. Um mod que saiu do ar, uma versão despublicada
    /// ou um namespace errado aparecem aqui, e não na máquina do jogador.
    /// </summary>
    private static async Task RunRealManifestChecks(HttpClient http, ModInstallerService installer)
    {
        var manifestService = new ManifestService(http);
        var manifest = await manifestService.GetManifestAsync("https://invalid.deadheim.example/manifest.json");

        var thunderstore = manifest.ThunderstoreMods;
        Check("manifest real do servidor carrega", thunderstore.Count >= 30,
            $"{manifest.OwnMods.Count} próprio(s) + {thunderstore.Count} Thunderstore");

        var pinned = thunderstore.Count(m => !string.IsNullOrWhiteSpace(m.Version));
        Check("mods do pack vêm com versão fixada", pinned >= 30, $"{pinned} de {thunderstore.Count} pinados");

        var failures = new List<string>();
        var mismatches = new List<string>();
        var unreachable = new List<string>();

        foreach (var mod in thunderstore)
        {
            ResolvedModVersion resolved;
            try
            {
                resolved = await installer.ResolveLatestAsync(mod);
                if (!string.IsNullOrWhiteSpace(mod.Version) && resolved.Version != mod.Version)
                    mismatches.Add($"{mod.Id}: pedido {mod.Version}, veio {resolved.Version}");
            }
            catch (Exception ex)
            {
                failures.Add($"{mod.ThunderstoreNamespace}/{mod.ThunderstoreName}" +
                             (string.IsNullOrWhiteSpace(mod.Version) ? "" : $"@{mod.Version}") +
                             $" -> {ex.Message.Trim()}");
                continue;
            }

            // Versão fixada não passa mais pela API (é URL previsível), então
            // resolver sozinho não prova nada. Um HEAD confirma que o arquivo
            // existe mesmo — é o que pega uma versão despublicada no pack.
            try
            {
                using var head = await HttpRetry.SendAsync(http,
                    () => new HttpRequestMessage(HttpMethod.Head, resolved.DownloadUrl));
                if (!head.IsSuccessStatusCode)
                    unreachable.Add($"{mod.Id}@{resolved.Version} -> HTTP {(int)head.StatusCode}");
            }
            catch (Exception ex)
            {
                unreachable.Add($"{mod.Id}@{resolved.Version} -> {ex.Message.Trim()}");
            }
        }

        Check($"todos os {thunderstore.Count} mods do manifest resolvem",
            failures.Count == 0,
            failures.Count == 0 ? "" : string.Join(" | ", failures));

        Check("versão entregue é exatamente a versão fixada",
            mismatches.Count == 0,
            mismatches.Count == 0 ? "" : string.Join(" | ", mismatches));

        Check($"todos os {thunderstore.Count} downloads existem no Thunderstore",
            unreachable.Count == 0,
            unreachable.Count == 0 ? "" : string.Join(" | ", unreachable));

        // Mods de autoria própria dependem dos repositórios do Deadheim estarem
        // públicos e com release. Enquanto não estiverem, isso é SKIP com motivo.
        foreach (var own in manifest.OwnMods)
        {
            try
            {
                var resolved = await installer.ResolveLatestAsync(own);
                Check($"mod próprio '{own.Id}' resolve no GitHub", true, $"{resolved.Version} -> {resolved.FileName}");
            }
            catch (Exception ex)
            {
                Skip($"mod próprio '{own.Id}' resolve no GitHub ({ex.Message.Trim()})");
            }
        }

        await RunBepInExInstallCheck(manifest, installer);

        if (_fullInstall)
        {
            await RunFullProfileInstallCheck(manifest, installer);
        }
        else
        {
            Skip("perfil completo do Deadheim instala de ponta a ponta (use --full)");
        }
    }

    /// <summary>
    /// A prova final: monta o perfil inteiro do servidor como um jogador faria ao
    /// clicar em Jogar — baixa e instala todos os mods obrigatórios, sincroniza
    /// para um Valheim limpo e confere o que chegou lá. Pesado (centenas de MB),
    /// por isso fica atrás de --full.
    /// </summary>
    private static async Task RunFullProfileInstallCheck(ModManifest manifest, ModInstallerService installer)
    {
        const string profile = "DeadheimFull";
        new ProfileService().LoadOrCreate(profile);

        var required = manifest.ThunderstoreMods.Where(m => m.Required).ToList();
        var failed = new List<string>();
        var installedCount = 0;

        foreach (var mod in required)
        {
            try
            {
                await installer.InstallAsync(mod, profile);
                installedCount++;
            }
            catch (Exception ex)
            {
                failed.Add($"{mod.Id} -> {ex.Message.Trim()}");
            }
        }

        Check($"instala os {required.Count} mods obrigatórios do pack",
            failed.Count == 0,
            failed.Count == 0 ? $"{installedCount} instalados" : string.Join(" | ", failed));

        var pluginsRoot = AppPaths.ProfilePluginsDir(profile);
        var dllCount = Directory.Exists(pluginsRoot)
            ? Directory.GetFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories).Length
            : 0;
        Check("perfil acumula as DLLs dos mods", dllCount >= 30, $"{dllCount} dlls");

        var game = Path.Combine(AppPaths.Root, "FullValheim");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "valheim.exe"), "jogo");

        // Mod que o jogador instalou por conta própria antes de usar o launcher.
        var doJogador = Path.Combine(game, "BepInEx", "plugins", "ModDoJogador");
        Directory.CreateDirectory(doJogador);
        File.WriteAllText(Path.Combine(doJogador, "Dele.dll"), "nao mexa");

        new ValheimLaunchService().PrepararJogo(game, profile);

        Check("o BepInEx do perfil tem o carregador",
            File.Exists(Path.Combine(AppPaths.ProfileBepInExDir(profile), "core", "BepInEx.Preloader.dll")));

        Check("preparar o perfil completo não instala nada em BepInEx/plugins do jogo",
            Directory.GetDirectories(Path.Combine(game, "BepInEx", "plugins")).Length == 1);

        Check("o mod do jogador sobrevive ao perfil completo",
            File.Exists(Path.Combine(doJogador, "Dele.dll")));

        // Nomes que o servidor exige de fato: se um destes faltar no perfil, o
        // jogador é recusado ou desincroniza ao entrar.
        string[] criticos = { "Jotunn.dll", "ServerCharacters.dll", "AzuAntiCheat.dll" };
        var todosPresentes = criticos.All(n =>
            Directory.GetFiles(pluginsRoot, n, SearchOption.AllDirectories).Length > 0);
        Check("mods críticos do servidor estão no perfil", todosPresentes, string.Join(", ", criticos));
    }

    /// <summary>
    /// O BepInEx é o único pacote que não é um plugin: ele é o carregador e vive
    /// na raiz do jogo. Instalar ele dentro de plugins/ resultaria num jogo sem
    /// mod nenhum carregado — e sem erro visível. Vale um teste próprio.
    /// </summary>
    private static async Task RunBepInExInstallCheck(ModManifest manifest, ModInstallerService installer)
    {
        var bepinex = manifest.ThunderstoreMods.FirstOrDefault(m => m.Target == InstallTarget.GameRoot);
        if (bepinex is null)
        {
            Skip("BepInEx: instala na raiz do jogo (nenhum pacote GameRoot no manifest)");
            return;
        }

        try
        {
            new ProfileService().LoadOrCreate("BepInExTest");
            await installer.InstallAsync(bepinex, "BepInExTest");

            // Funde na raiz de jogo do perfil, sem virar subpasta: é essa árvore
            // que o jogo carrega.
            var installedDir = AppPaths.ProfileGameDir("BepInExTest");

            Check("BepInEx: funde na raiz do perfil e não vira mais um plugin",
                Directory.Exists(installedDir) &&
                !Directory.Exists(Path.Combine(AppPaths.ProfilePluginsDir("BepInExTest"), bepinex.Id)));

            // O zip vem embrulhado em BepInExPack_Valheim/; se o desembrulho falhar,
            // o winhttp.dll acaba um nível fundo demais e o jogo não carrega nada.
            Check("BepInEx: winhttp.dll fica na raiz do pacote (zip desembrulhado)",
                File.Exists(Path.Combine(installedDir, "winhttp.dll")),
                string.Join(", ", Directory.GetFileSystemEntries(installedDir).Select(Path.GetFileName).Take(8)));

            Check("BepInEx: traz a pasta BepInEx/core",
                Directory.Exists(Path.Combine(installedDir, "BepInEx", "core")));

            // Valheim limpo: só o injetor deve chegar lá, nada de BepInEx.
            var cleanGame = Path.Combine(AppPaths.Root, "CleanValheim");
            Directory.CreateDirectory(cleanGame);
            File.WriteAllText(Path.Combine(cleanGame, "valheim.exe"), "jogo");
            new ValheimLaunchService().PrepararJogo(cleanGame, "BepInExTest");

            Check("BepInEx: só o injetor vai para o Valheim",
                File.Exists(Path.Combine(cleanGame, "winhttp.dll")) &&
                !Directory.Exists(Path.Combine(cleanGame, "BepInEx")));
        }
        catch (Exception ex)
        {
            Check("BepInEx: instala na raiz do jogo", false, ex.Message);
        }
    }

    /// <summary>
    /// Estado real do repositório do servidor. Enquanto ele estiver privado ou sem
    /// release publicado, isso aparece como SKIP com o motivo — é o que falta pra
    /// virar distribuição de verdade, não um defeito de código.
    /// </summary>
    private static async Task RunDeadheimRepoCheck(GitHubReleaseService github)
    {
        var deadheim = new ModEntry
        {
            Id = "deadheim-launcher",
            Name = "Deadheim Launcher",
            Source = ModSource.GitHub,
            GitHubOwner = "Deadheim-project",
            GitHubRepo = "Launcher",
            AssetPattern = ".zip"
        };

        try
        {
            var resolved = await github.GetLatestAsync(deadheim);
            Check("Deadheim-project/Launcher: release acessível publicamente",
                !string.IsNullOrWhiteSpace(resolved.Version), $"{resolved.Version} -> {resolved.FileName}");
        }
        catch (Exception ex)
        {
            Skip($"Deadheim-project/Launcher: release acessível publicamente ({ex.Message.Trim()})");
        }
    }

    // ---------------------------------------------------------------- utilidades

    private static bool TryWriteZoneIdentifier(string file)
    {
        try
        {
            File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            return ZoneIdentifierExists(file);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ZoneIdentifierExists(string file)
    {
        try { return File.Exists(file + ":Zone.Identifier"); }
        catch (Exception) { return false; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* sandbox temporária: o SO limpa depois */ }
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Write($"SELFTEST PASS: {name}{(string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]")}");
        }
        else
        {
            _failed++;
            Write($"SELFTEST FAIL: {name}{(string.IsNullOrEmpty(detail) ? "" : "  -- " + detail)}");
        }
    }

    private static void Skip(string name)
    {
        _skipped++;
        Write($"SELFTEST SKIP: {name}");
    }

    private static int Report()
    {
        Write("");
        Write($"SELFTEST: {_passed} passed, {_failed} failed, {_skipped} skipped");

        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "DeadheimLauncher-selftest.log");
            // Com BOM: sem ele o PowerShell 5.1 lê o arquivo como ANSI e os acentos saem trocados.
            File.WriteAllText(logPath, Log.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Write($"SELFTEST: log em {logPath}");
        }
        catch (Exception) { /* log em arquivo é conveniência, não requisito */ }

        return _failed == 0 ? 0 : 1;
    }

    private static void Write(string line)
    {
        Log.AppendLine(line);
        Console.WriteLine(line);
    }

    // Um app WPF não tem console próprio; sem isso a saída some quando o
    // self-test é chamado de um terminal.
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void AttachConsoleIfPossible()
    {
        try { AttachConsole(AttachParentProcess); }
        catch (Exception) { /* sem console: o log em arquivo ainda registra tudo */ }
    }
}
