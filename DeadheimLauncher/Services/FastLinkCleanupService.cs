namespace DeadheimLauncher.Services;

/// <summary>
/// Remove os restos de uma instalação antiga do FastLink. Nenhuma informação
/// dele é importada: senha, personagem e configuração são descartados.
/// </summary>
public static class FastLinkCleanupService
{
    public static bool RemoveLegacyFiles(string profileName)
    {
        var configDir = AppPaths.ProfileConfigDir(profileName);
        var yaml = Path.Combine(configDir, "Azumatt.FastLink_servers.yml");
        var cfg = Path.Combine(configDir, "Azumatt.FastLink.cfg");
        var obsoleteMarker = Path.Combine(configDir, "Detalhes.Deadheim.directjoin.migrate");
        var plugin = Path.Combine(AppPaths.ProfilePluginsDir(profileName), "fastlink");
        var existed = File.Exists(yaml) || File.Exists(cfg) || File.Exists(obsoleteMarker) || Directory.Exists(plugin);

        if (File.Exists(yaml)) File.Delete(yaml);
        if (File.Exists(cfg)) File.Delete(cfg);
        if (File.Exists(obsoleteMarker)) File.Delete(obsoleteMarker);
        if (Directory.Exists(plugin)) Directory.Delete(plugin, recursive: true);
        return existed;
    }
}
