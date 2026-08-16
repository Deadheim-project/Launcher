namespace DeadheimLauncher.Models;

/// <summary>Raiz do manifest.json: lista completa de mods oferecidos pelo servidor Deadheim.</summary>
public sealed class ModManifest
{
    /// <summary>
    /// Versão do modpack do servidor (ex. "15.0.5"). O launcher compara com a
    /// que o perfil aplicou por último para avisar que o servidor mudou.
    /// </summary>
    public string? PackVersion { get; set; }

    public List<ModEntry> OwnMods { get; set; } = new();
    public List<ModEntry> ThunderstoreMods { get; set; } = new();

    public IEnumerable<ModEntry> AllMods => OwnMods.Concat(ThunderstoreMods);
}
