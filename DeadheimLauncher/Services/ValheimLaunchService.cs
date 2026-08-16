using System.Diagnostics;
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

/// <summary>
/// Localiza a instalação do Valheim, sincroniza os plugins do perfil ativo
/// para BepInEx/plugins e inicia o jogo. Procura no caminho padrão do Steam,
/// com override em settings.json.
/// </summary>
public sealed class ValheimLaunchService
{
    private const string DefaultSteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Valheim";
    private const string ValheimSteamAppId = "892970";

    public string ResolveValheimPath(LauncherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ValheimPath) && Directory.Exists(settings.ValheimPath))
            return settings.ValheimPath;

        var detected = TryDetectFromSteamRegistry() ?? DefaultSteamPath;
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
            if (installPath is null) return null;

            var candidate = Path.Combine(installPath, "steamapps", "common", "Valheim");
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

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
    public static string MontarArgumentosDeInicializacao(string profileName)
    {
        var preloader = Path.Combine(AppPaths.ProfileBepInExDir(profileName), "core", "BepInEx.Preloader.dll");
        return "--doorstop-enabled true " +
               $"--doorstop-target-assembly \"{preloader}\"";
    }

    /// <summary>
    /// Inicia o Valheim já apontando para os mods do perfil.
    ///
    /// Executa o valheim.exe direto, e não steam://run, porque os argumentos do
    /// Doorstop precisam chegar ao processo — a URI da Steam não repassa
    /// argumentos. Com a Steam aberta, o jogo continua contando tempo e
    /// aparecendo como "jogando"; o overlay costuma funcionar normalmente.
    /// </summary>
    public void LaunchGame(string valheimPath, string profileName)
    {
        var exe = Path.Combine(valheimPath, "valheim.exe");
        if (!File.Exists(exe))
            throw new ValheimNotFoundException($"Não encontrei o valheim.exe em '{valheimPath}'.");

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = MontarArgumentosDeInicializacao(profileName),
            WorkingDirectory = valheimPath,
            UseShellExecute = true
        });
    }

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
