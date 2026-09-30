namespace DeadheimLauncher.Models;

/// <summary>Configurações persistentes do launcher (settings.json em %AppData%\DeadheimLauncher).</summary>
public sealed class LauncherSettings
{
    public string? ValheimPath { get; set; }
    /// <summary>
    /// Onde o launcher busca a lista de mods do servidor. Aponta para o
    /// manifest.json na raiz do repositório do launcher, então atualizar a lista
    /// de mods é um commit — ninguém precisa reinstalar nada.
    /// O repositório precisa ser público para essa URL responder.
    /// </summary>
    public string ManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/Deadheim-project/Launcher/main/manifest.json";
    public string LastActiveProfile { get; set; } = "Default";

    // Temporada nova em desenvolvimento: o servidor e o local de teste do dono
    // (D:\dh-local, 127.0.0.1:2456). Trocar pelo endereco real quando o servidor
    // entrar no ar -- ate la nenhum jogador conecta.
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 2456;
    /// <summary>
    /// Senha do servidor, distribuída no launcher para que o jogador entre
    /// direto, sem digitá-la. Hoje é a do servidor local de teste.
    ///
    /// Aqui entra SÓ a senha do jogo. Este repositório precisa ser público — é
    /// de raw.githubusercontent.com que o launcher busca o manifest.json — então
    /// tudo neste arquivo é publicado junto. Um valor antigo daqui era, byte a
    /// byte, a senha de FTP da DatHost: além de dar escrita em plugins, na
    /// whitelist do anticheat e nos mundos salvos, ela nem servia para entrar.
    /// </summary>
    public string? ServerPassword { get; set; } = "dhlocal1";
}
