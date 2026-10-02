using System.Security.Cryptography;
using System.Text;

namespace DeadheimLauncher.Models;

/// <summary>Raiz do manifest.json: lista completa de mods oferecidos pelo servidor Deadheim.</summary>
public sealed class ModManifest
{
    /// <summary>
    /// Versão do modpack do servidor (ex. "15.0.5"). O launcher compara com a
    /// que o perfil aplicou por último para avisar que o servidor mudou.
    /// </summary>
    public string? PackVersion { get; set; }

    /// <summary>
    /// Servidor onde este pacote roda. Quando o manifest traz os dois, o botão
    /// Jogar conecta aqui em vez do endereço das Configurações — assim um
    /// manifest de teste (ex. servidor local) leva junto o próprio endereço, e
    /// quem aponta o ManifestUrl para ele não precisa trocar mais nada.
    /// Ausentes, vale o que está nas Configurações.
    /// </summary>
    public string? ServerHost { get; set; }
    public int? ServerPort { get; set; }

    /// <summary>
    /// Configurações com o servidor do manifest por cima, se ele declarar um.
    /// Devolve uma cópia: o settings.json do jogador não é alterado.
    /// </summary>
    public LauncherSettings AplicarServidor(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(ServerHost) || ServerPort is not (>= 1 and <= 65535))
            return settings;

        return new LauncherSettings
        {
            ValheimPath = settings.ValheimPath,
            ManifestUrl = settings.ManifestUrl,
            LastActiveProfile = settings.LastActiveProfile,
            ServerHost = ServerHost.Trim(),
            ServerPort = ServerPort.Value,
            ServerPassword = settings.ServerPassword
        };
    }

    /// <summary>
    /// Novidades mostradas na tela inicial, da mais recente para a mais antiga.
    /// Vêm no manifest para que anunciar uma mudança seja o mesmo commit que a
    /// faz, sem precisar publicar launcher novo. Vazio, a aba NOVIDADES não
    /// aparece.
    /// </summary>
    public List<ChangelogEntry> Changelog { get; set; } = new();

    /// <summary>
    /// Apresentação do servidor na tela inicial. Ausente, a tela fica só com
    /// as novidades (se houver). Launcher antigo ignora o campo.
    /// </summary>
    public ServerPresentation? Presentation { get; set; }

    public List<ModEntry> OwnMods { get; set; } = new();
    public List<ModEntry> ThunderstoreMods { get; set; } = new();

    public IEnumerable<ModEntry> AllMods => OwnMods.Concat(ThunderstoreMods);

    /// <summary>
    /// Impressão digital do que este manifest manda instalar.
    ///
    /// Só entra o que muda a instalação — id, versão e origem de cada mod. Nome,
    /// descrição e link ficam de fora de propósito: corrigir um texto não pode
    /// obrigar todo jogador a rebaixar 40 mods.
    ///
    /// É o que permite o launcher perceber que nada mudou desde a última partida
    /// e não consultar origem nenhuma — sem isso, mod sem versão fixada era
    /// reconsultado a cada clique em Jogar, estourando o limite da API do GitHub.
    /// </summary>
    public string CalcularDigital()
    {
        var partes = AllMods
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => string.Join('|',
                m.Id,
                m.Version ?? "",
                m.Source.ToString(),
                m.ThunderstoreNamespace ?? "",
                m.ThunderstoreName ?? "",
                m.GitHubOwner ?? "",
                m.GitHubRepo ?? "",
                m.AssetPattern ?? "",
                m.Target.ToString(),
                m.Required ? "1" : "0"));

        var texto = string.Join('\n', partes);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(texto));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Digital do estado realmente aplicado ao perfil. O manifesto sozinho não basta:
    /// marcar ou desmarcar um opcional/admin também muda o conjunto que deve existir no
    /// disco, mesmo quando o servidor não publicou uma nova versão do pack.
    /// </summary>
    public string CalcularDigitalDaInstalacao(IEnumerable<string> idsHabilitados)
    {
        var selecao = string.Join('|', idsHabilitados
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => id.ToLowerInvariant()));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CalcularDigital() + "\n" + selecao));
        return Convert.ToHexString(bytes)[..16];
    }
}
