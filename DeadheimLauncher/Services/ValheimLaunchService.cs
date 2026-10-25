using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DeadheimLauncher.Models;
using Microsoft.Win32;

namespace DeadheimLauncher.Services;

public sealed class ValheimNotFoundException : Exception
{
    public ValheimNotFoundException(string message) : base(message) { }
}

public sealed class BepInExNotFoundException : Exception
{
    public BepInExNotFoundException(string message) : base(message) { }
}

public sealed class ServerConnectionNotConfiguredException : Exception
{
    public ServerConnectionNotConfiguredException(string message) : base(message) { }
}

/// <summary>
/// Localiza a instalação do Valheim, sincroniza os plugins do perfil ativo
/// para BepInEx/plugins e inicia o jogo. Procura no caminho padrão do Steam,
/// com override em settings.json.
/// </summary>
public sealed class ValheimLaunchService
{
    private const string DefaultSteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Valheim";
    private const string ValheimSteamAppId = "892970";
    private const string ExecutavelWindows = "valheim.exe";
    private const string ExecutavelLinux = "valheim.x86_64";
    private const string ScriptLinux = "iniciar_valheim_linux.sh";

    /// <summary>Onde a Steam do Linux costuma ficar, relativo ao home (nativa, Flatpak e Snap).</summary>
    private static readonly string[] SteamLinuxRelativos =
    {
        ".steam/steam",
        ".steam/root",
        ".local/share/Steam",
        ".var/app/com.valvesoftware.Steam/.local/share/Steam",
        "snap/steam/common/.local/share/Steam"
    };

    private DateTime _ultimaVarreduraLinux = DateTime.MinValue;
    private bool _versaoLinuxRodando;

    /// <summary>O estado é consultado pelo launcher enquanto a janela está aberta para
    /// trocar a ação principal sem permitir iniciar uma segunda cópia do jogo.</summary>
    public bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("valheim");
        try
        {
            if (processes.Any(process => !process.HasExited)) return true;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return VersaoLinuxRodando();
    }

    /// <summary>
    /// O Valheim nativo de Linux não é processo do Wine, então GetProcessesByName
    /// não o enxerga; procura no /proc. Varre no máximo a cada 5 s porque o timer
    /// chama isto a cada segundo e cada leitura passa pelo Wine.
    /// </summary>
    private bool VersaoLinuxRodando()
    {
        if (!Wine.Ativo) return false;
        if (DateTime.UtcNow - _ultimaVarreduraLinux < TimeSpan.FromSeconds(5)) return _versaoLinuxRodando;
        _ultimaVarreduraLinux = DateTime.UtcNow;
        _versaoLinuxRodando = false;

        try
        {
            foreach (var pasta in Directory.EnumerateDirectories(Wine.ParaCaminhoWindows("/proc")))
            {
                var pid = Path.GetFileName(pasta);
                if (pid.Length == 0 || !pid.All(char.IsDigit)) continue;
                try
                {
                    if (File.ReadAllText(Path.Combine(pasta, "comm")).Trim() == ExecutavelLinux)
                    {
                        _versaoLinuxRodando = true;
                        break;
                    }
                }
                catch
                {
                    // processo terminou no meio da varredura
                }
            }
        }
        catch
        {
            // sem /proc visível: segue como fechado
        }

        return _versaoLinuxRodando;
    }

    /// <summary>
    /// Pede ao Valheim para fechar pela janela normal. Não usa Kill: encerrar o processo à
    /// força pode interromper a gravação do personagem ou do mundo.
    /// </summary>
    public bool RequestGameClose()
    {
        var requested = false;
        var processes = Process.GetProcessesByName("valheim");
        try
        {
            foreach (var process in processes)
            {
                if (!process.HasExited && process.CloseMainWindow()) requested = true;
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return requested;
    }

    public string ResolveValheimPath(LauncherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ValheimPath))
        {
            // No Wine, "/run/media/..." e "C:\run\media\..." querem dizer Z:\run\media\...
            var candidatos = Wine.Ativo
                ? Wine.CandidatosParaCaminhoInformado(settings.ValheimPath, Wine.HomeUnix())
                : new[] { settings.ValheimPath };
            var existente = candidatos.FirstOrDefault(Directory.Exists);
            if (existente is not null) return existente;
        }

        var detected = TryDetectFromSteamRegistry() ?? TryDetectFromLinuxSteam() ?? DefaultSteamPath;
        if (!Directory.Exists(detected))
            throw new ValheimNotFoundException(
                $"Não encontrei o Valheim em '{detected}'. Configure o caminho manualmente nas Configurações.");

        return detected;
    }

    private static string? TryDetectFromSteamRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var installPath = key?.GetValue("InstallPath") as string;
            return installPath is null ? null : ProcurarNaSteam(installPath, biblioteca => biblioteca);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Steam do Linux vista de dentro do Wine. As bibliotecas do libraryfolders.vdf
    /// vêm com caminho do Linux (/run/media/...), por isso a conversão.
    /// </summary>
    private static string? TryDetectFromLinuxSteam()
    {
        if (!Wine.Ativo) return null;
        var home = Wine.HomeUnix();
        if (home is null) return null;

        foreach (var relativo in SteamLinuxRelativos)
        {
            try
            {
                var raiz = Wine.ParaCaminhoWindows($"{home.TrimEnd('/')}/{relativo}");
                var encontrado = ProcurarNaSteam(raiz, Wine.ParaCaminhoWindows);
                if (encontrado is not null) return encontrado;
            }
            catch
            {
                // próxima instalação possível
            }
        }

        return null;
    }

    /// <summary>
    /// Procura o Valheim na pasta da Steam e nas bibliotecas extras dela (jogo
    /// instalado em outro disco).
    /// </summary>
    private static string? ProcurarNaSteam(string steamRoot, Func<string, string> converterBiblioteca)
    {
        var bibliotecas = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            bibliotecas.AddRange(LerBibliotecasDaSteam(File.ReadAllText(vdf)).Select(converterBiblioteca));

        return bibliotecas
            .Select(biblioteca => Path.Combine(biblioteca, "steamapps", "common", "Valheim"))
            .FirstOrDefault(TemExecutavel);
    }

    /// <summary>Valores "path" do libraryfolders.vdf, já sem o escape das barras.</summary>
    public static IReadOnlyList<string> LerBibliotecasDaSteam(string vdf) =>
        Regex.Matches(vdf, @"""path""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
            .ToList();

    private static bool TemExecutavel(string pasta) =>
        File.Exists(Path.Combine(pasta, ExecutavelWindows)) || File.Exists(Path.Combine(pasta, ExecutavelLinux));

    /// <summary>
    /// A pasta tem só a versão nativa de Linux (Steam sem Proton). A versão de
    /// Windows instalada pelo Proton tem o valheim.exe e segue o caminho normal.
    /// </summary>
    public static bool EhVersaoLinux(string valheimPath) =>
        !File.Exists(Path.Combine(valheimPath, ExecutavelWindows))
        && File.Exists(Path.Combine(valheimPath, ExecutavelLinux));

    /// <summary>
    /// Prepara o jogo para carregar os mods do perfil SEM instalar nada dentro
    /// dele.
    ///
    /// Antes, isto copiava os mods para BepInEx/plugins do Valheim e apagava
    /// tudo que já estivesse lá — destruindo qualquer mod que o jogador tivesse
    /// posto por conta própria. Agora a árvore BepInEx inteira (core, plugins,
    /// config) mora no perfil, e o jogo é apontado para lá na hora de iniciar.
    ///
    /// O único arquivo que precisa estar junto do valheim.exe é o winhttp.dll:
    /// é ele que injeta o carregador no processo, e não há como evitar isso sem
    /// mexer nas opções de inicialização da Steam. Junto vai um
    /// doorstop_config.ini com enabled=false, para que abrir o jogo direto pela
    /// Steam continue sendo Valheim puro — o launcher liga o carregamento por
    /// linha de comando, só na sua própria execução.
    /// </summary>
    public void PrepararJogo(string valheimPath, string profileName)
    {
        var bepinexDoPerfil = AppPaths.ProfileBepInExDir(profileName);
        if (!Directory.Exists(Path.Combine(bepinexDoPerfil, "core")))
            throw new BepInExNotFoundException(
                "O BepInEx ainda não foi baixado para este perfil. Marque o BepInEx na lista de mods e clique em Jogar de novo.");

        // No Linux nativo o Doorstop entra por LD_PRELOAD, apontado direto para o
        // perfil (ver MontarScriptLinux). Nada precisa ir para a pasta do jogo.
        if (EhVersaoLinux(valheimPath)) return;

        var origemDoPerfil = AppPaths.ProfileGameDir(profileName);

        // winhttp.dll e doorstop_libs são o mínimo que o Windows exige ao lado
        // do executável para a injeção acontecer.
        foreach (var arquivo in new[] { "winhttp.dll", ".doorstop_version" })
        {
            var origem = Path.Combine(origemDoPerfil, arquivo);
            if (File.Exists(origem)) File.Copy(origem, Path.Combine(valheimPath, arquivo), overwrite: true);
        }

        var libsOrigem = Path.Combine(origemDoPerfil, "doorstop_libs");
        if (Directory.Exists(libsOrigem))
            CopyDirectory(libsOrigem, Path.Combine(valheimPath, "doorstop_libs"));

        // Desligado por padrão: sem isso, abrir pela Steam carregaria os mods do
        // perfil sem o jogador ter pedido.
        var configDoorstop = Path.Combine(valheimPath, "doorstop_config.ini");
        if (!File.Exists(configDoorstop))
        {
            File.WriteAllText(configDoorstop,
                "[General]\r\nenabled=false\r\n" +
                "target_assembly=BepInEx\\core\\BepInEx.Preloader.dll\r\n");
        }
    }

    /// <summary>
    /// Argumentos que mandam o Doorstop carregar o BepInEx do perfil, e não o do
    /// jogo. Separado para poder ser verificado sem abrir o Valheim.
    /// </summary>
    public static string MontarArgumentosDeInicializacao(string profileName, LauncherSettings settings)
    {
        ValidarServidor(settings);

        var preloader = Path.Combine(AppPaths.ProfileBepInExDir(profileName), "core", "BepInEx.Preloader.dll");
        return "--doorstop-enabled true " +
               $"--doorstop-target-assembly \"{preloader}\" " +
               $"+connect {settings.ServerHost.Trim()}:{settings.ServerPort} " +
               $"-password \"{EscapeArgument(settings.ServerPassword!)}\"";
    }

    /// <summary>
    /// Inicia o Valheim já apontando para os mods do perfil.
    ///
    /// Executa o valheim.exe direto, e não steam://run, porque os argumentos do
    /// Doorstop precisam chegar ao processo — a URI da Steam não repassa
    /// argumentos. Com a Steam aberta, o jogo continua contando tempo e
    /// aparecendo como "jogando"; o overlay costuma funcionar normalmente.
    /// </summary>
    public void LaunchGame(string valheimPath, string profileName, LauncherSettings settings)
    {
        if (EhVersaoLinux(valheimPath))
        {
            IniciarVersaoLinux(valheimPath, profileName, settings);
            return;
        }

        var exe = Path.Combine(valheimPath, ExecutavelWindows);
        if (!File.Exists(exe))
            throw new ValheimNotFoundException($"Não encontrei o valheim.exe em '{valheimPath}'.");

        var inicio = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = MontarArgumentosDeInicializacao(profileName, settings),
            WorkingDirectory = valheimPath,
            // Environment só vale sem shell; fora do Wine fica como sempre foi.
            UseShellExecute = !Wine.Ativo
        };

        if (Wine.Ativo)
        {
            // O Wine prefere o winhttp.dll embutido dele e ignoraria o do BepInEx.
            var atuais = Environment.GetEnvironmentVariable("WINEDLLOVERRIDES");
            inicio.Environment["WINEDLLOVERRIDES"] = string.IsNullOrEmpty(atuais) ? "winhttp=n,b" : atuais + ";winhttp=n,b";
        }

        Process.Start(inicio);
    }

    /// <summary>
    /// Valheim nativo de Linux, com o launcher rodando no Wine. O BepInExPack já
    /// traz o Doorstop de Linux (doorstop_libs/libdoorstop_x64.so); o script
    /// gerado faz o mesmo que o start_game_bepinex.sh, mas apontando para o
    /// BepInEx do perfil. O Wine executa binários do Linux direto, então o
    /// /bin/sh roda fora do Wine, como um programa normal.
    /// </summary>
    private static void IniciarVersaoLinux(string valheimPath, string profileName, LauncherSettings settings)
    {
        if (!Wine.Ativo)
            throw new ValheimNotFoundException(
                $"A pasta '{valheimPath}' tem a versão de Linux do Valheim (valheim.x86_64), que não abre no Windows.");

        var raizDoPerfil = AppPaths.ProfileGameDir(profileName);
        var jogoUnix = Wine.ParaCaminhoUnix(valheimPath);
        var perfilUnix = Wine.ParaCaminhoUnix(raizDoPerfil);
        if (jogoUnix is null || perfilUnix is null)
            throw new InvalidOperationException("O Wine não converteu os caminhos para o Linux. Mande um print desta tela para a staff.");

        var script = Path.Combine(raizDoPerfil, ScriptLinux);
        File.WriteAllText(script, MontarScriptLinux(jogoUnix, perfilUnix, settings), new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = Wine.ParaCaminhoWindows("/bin/sh"),
            Arguments = $"\"{perfilUnix.TrimEnd('/')}/{ScriptLinux}\"",
            WorkingDirectory = valheimPath,
            UseShellExecute = false
        });
    }

    /// <summary>
    /// Script de shell que inicia o Valheim nativo com o BepInEx do perfil e
    /// conecta no servidor. Separado para o self-test conferir sem Linux. A
    /// saída do jogo vai para valheim_linux.log no perfil, para a staff pedir.
    /// </summary>
    public static string MontarScriptLinux(string jogoUnix, string perfilUnix, LauncherSettings settings)
    {
        ValidarServidor(settings);
        var perfil = perfilUnix.TrimEnd('/');
        var argumentos = new[]
        {
            "+connect", $"{settings.ServerHost.Trim()}:{settings.ServerPort}",
            "-password", settings.ServerPassword!
        };

        var linhas = new[]
        {
            "#!/bin/sh",
            "# Gerado pelo Deadheim Launcher a cada Jogar. Não edite: é sobrescrito.",
            $"cd {AspasShell(jogoUnix)} || exit 1",
            // Variáveis do Wine não têm o que fazer num programa do Linux.
            "unset WINEPREFIX WINELOADER WINEDLLPATH WINEDLLOVERRIDES WINESERVER WINEDEBUG",
            // Aberto fora da Steam, o jogo pede para a Steam reabri-lo sem isso, e
            // o BepInEx fica para trás.
            $"export SteamAppId={ValheimSteamAppId}",
            $"export SteamGameId={ValheimSteamAppId}",
            "export DOORSTOP_ENABLED=1",
            $"export DOORSTOP_TARGET_ASSEMBLY={AspasShell(perfil + "/BepInEx/core/BepInEx.Preloader.dll")}",
            "export DOORSTOP_IGNORE_DISABLED_ENV=0",
            $"export LD_LIBRARY_PATH={AspasShell(perfil + "/doorstop_libs")}${{LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}}",
            "export LD_PRELOAD=libdoorstop_x64.so${LD_PRELOAD:+:$LD_PRELOAD}",
            $"exec ./{ExecutavelLinux} {string.Join(" ", argumentos.Select(AspasShell))} > {AspasShell(perfil + "/valheim_linux.log")} 2>&1"
        };

        return string.Join("\n", linhas) + "\n";
    }

    private static string AspasShell(string valor) => "'" + valor.Replace("'", @"'\''") + "'";

    private static void ValidarServidor(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServerHost) || settings.ServerPort is < 1 or > 65535)
            throw new ServerConnectionNotConfiguredException("O endereço do servidor está inválido. Confira as Configurações.");
        if (string.IsNullOrWhiteSpace(settings.ServerPassword))
            throw new ServerConnectionNotConfiguredException("Informe a senha do servidor nas Configurações antes de jogar.");
    }

    private static string EscapeArgument(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destFile = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
        }
    }
}
