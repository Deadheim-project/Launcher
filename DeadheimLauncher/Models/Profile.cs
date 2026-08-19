namespace DeadheimLauncher.Models;

/// <summary>
/// O conjunto de mods aplicado nesta máquina: o que está habilitado, em que
/// versão, e de qual manifest veio.
/// Persistido em profiles/{Name}/profile.json.
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public List<string> EnabledModIds { get; set; } = new();

    /// <summary>ModId -> versão instalada (ex. "1.2.0"), usado pra decidir se precisa atualizar.</summary>
    public Dictionary<string, string> InstalledVersions { get; set; } = new();

    /// <summary>
    /// Impressão digital do manifest que já foi aplicado por completo.
    ///
    /// Sem isso, cada clique em Jogar reconsulta a origem de todo mod sem versão
    /// fixada — e os mods do servidor não têm. Eram 5 chamadas à API do GitHub
    /// por partida, o que estoura o limite de 60 por hora de quem não usa
    /// credencial e faz o download falhar com 403. Guardando a digital, um
    /// manifest que não mudou não gera consulta nenhuma.
    /// </summary>
    public string? ManifestAplicado { get; set; }
}
