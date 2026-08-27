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
    public string ServerHost { get; set; } = "loboda.dathost.net";
    public int ServerPort { get; set; } = 20486;
    /// <summary>
    /// Senha do servidor oficial, distribuída no launcher para que todos os
    /// jogadores entrem diretamente sem depender do FastLink ou digitá-la.
    ///
    /// É a mesma que o Azumatt.FastLink_servers.yml publica e a mesma do
    /// AutoJoinPassword: senha de jogo, feita para ser distribuída.
    ///
    /// Aqui entra SÓ a senha do jogo. Este repositório precisa ser público — é
    /// de raw.githubusercontent.com que o launcher busca o manifest.json — então
    /// tudo neste arquivo é publicado junto. O valor anterior era, byte a byte,
    /// a senha de FTP da DatHost: além de dar escrita em plugins, na whitelist
    /// do anticheat e nos mundos salvos, ela nem servia para entrar, e todo
    /// jogador com instalação nova era recusado no -password.
    /// </summary>
    public string? ServerPassword { get; set; } = "secret";
}
