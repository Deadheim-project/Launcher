using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>CRUD de perfis (criar/duplicar/renomear/excluir) e persistência do profile.json de cada um.</summary>
public sealed class ProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<string> ListProfiles()
    {
        AppPaths.EnsureDirs();
        return Directory.Exists(AppPaths.ProfilesDir)
            ? Directory.GetDirectories(AppPaths.ProfilesDir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).OrderBy(n => n).ToList()
            : new List<string>();
    }

    public Profile LoadOrCreate(string profileName)
    {
        AppPaths.EnsureDirs();
        Directory.CreateDirectory(AppPaths.ProfileDir(profileName));
        Directory.CreateDirectory(AppPaths.ProfilePluginsDir(profileName));

        var file = AppPaths.ProfileFile(profileName);
        if (!File.Exists(file))
        {
            var fresh = new Profile { Name = profileName };
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<Profile>(json) ?? new Profile { Name = profileName };
        }
        catch (JsonException)
        {
            return new Profile { Name = profileName };
        }
    }

    public void Save(Profile profile)
    {
        Directory.CreateDirectory(AppPaths.ProfileDir(profile.Name));
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(AppPaths.ProfileFile(profile.Name), json);
    }

    public Profile Duplicate(Profile source, string newName)
    {
        var copy = new Profile
        {
            Name = newName,
            EnabledModIds = new List<string>(source.EnabledModIds),
            InstalledVersions = new Dictionary<string, string>(source.InstalledVersions)
        };
        Save(copy);

        var sourcePlugins = AppPaths.ProfilePluginsDir(source.Name);
        var destPlugins = AppPaths.ProfilePluginsDir(newName);
        if (Directory.Exists(sourcePlugins))
        {
            CopyDirectory(sourcePlugins, destPlugins);
        }

        return copy;
    }

    public void Delete(string profileName)
    {
        var dir = AppPaths.ProfileDir(profileName);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Apaga tudo o que o launcher instalou — a árvore de jogo inteira do perfil
    /// (BepInEx, plugins e config) — e devolve quantas pastas de mod saíram.
    ///
    /// Existe porque "reinstala do zero" é a resposta certa para a maioria dos
    /// problemas que chegam: download interrompido, .dll pela metade, config
    /// editada na mão que não bate mais com a do servidor. Sem isso a única
    /// saída era mandar o jogador achar %AppData% e apagar pasta na mão, o que
    /// dá errado de formas piores que o problema original.
    ///
    /// O que NÃO sai: profile.json, que fica em profiles/&lt;perfil&gt;/ e não dentro
    /// de game/ — então a escolha de opcionais do jogador sobrevive. Só
    /// InstalledVersions é zerado, porque nada mais está no disco: se o perfil
    /// seguisse afirmando ter a versão instalada, o Jogar seguinte não baixaria
    /// nada e o jogo subiria sem mod nenhum.
    ///
    /// A instalação do Valheim não é tocada — o launcher nunca escreve lá.
    /// </summary>
    public int RemoverModsInstalados(Profile profile)
    {
        var plugins = AppPaths.ProfilePluginsDir(profile.Name);
        var quantidade = Directory.Exists(plugins) ? Directory.GetDirectories(plugins).Length : 0;

        var game = AppPaths.ProfileGameDir(profile.Name);
        if (Directory.Exists(game)) Directory.Delete(game, recursive: true);

        // Recria o esqueleto: o resto do launcher assume que a pasta existe.
        Directory.CreateDirectory(AppPaths.ProfilePluginsDir(profile.Name));

        profile.InstalledVersions.Clear();
        Save(profile);
        return quantidade;
    }

    /// <summary>
    /// Apaga do perfil os mods que não estão mais no manifest do servidor e
    /// devolve quais foram. Retirar um mod do pack é uma decisão do servidor
    /// tanto quanto adicionar: se ele continuar no disco do jogador, vai ser
    /// carregado pelo BepInEx e pode ser recusado pelo anticheat ou brigar com
    /// os que ficaram.
    ///
    /// Só mexe no que o próprio launcher instalou (pastas com id conhecido).
    /// Mod que o jogador pôs na mão em BepInEx/plugins não é problema daqui.
    /// </summary>
    public IReadOnlyList<string> RemoverModsForaDoManifest(Profile profile, IEnumerable<string> idsDoManifest)
    {
        var validos = new HashSet<string>(idsDoManifest, StringComparer.OrdinalIgnoreCase);
        var removidos = new List<string>();

        // Só a pasta de plugins: é a única onde cada subpasta corresponde a um
        // mod. A raiz de jogo do perfil guarda a árvore do BepInEx (core,
        // config...), cujos nomes não são ids de mod — varrer ali apagaria o
        // carregador inteiro por não achá-lo no manifest.
        var plugins = AppPaths.ProfilePluginsDir(profile.Name);
        if (Directory.Exists(plugins))
        {
            foreach (var pasta in Directory.GetDirectories(plugins))
            {
                var id = Path.GetFileName(pasta);
                if (validos.Contains(id)) continue;

                Directory.Delete(pasta, recursive: true);
                removidos.Add(id);
            }
        }

        // O perfil não pode seguir afirmando ter instalado o que já saiu.
        foreach (var id in profile.InstalledVersions.Keys.Where(k => !validos.Contains(k)).ToList())
        {
            profile.InstalledVersions.Remove(id);
            if (!removidos.Contains(id)) removidos.Add(id);
        }

        profile.EnabledModIds = profile.EnabledModIds.Where(validos.Contains).ToList();

        if (removidos.Count > 0) Save(profile);
        return removidos;
    }

    public void Rename(string oldName, string newName)
    {
        var oldDir = AppPaths.ProfileDir(oldName);
        var newDir = AppPaths.ProfileDir(newName);
        if (!Directory.Exists(oldDir)) return;

        Directory.Move(oldDir, newDir);
        var profile = LoadOrCreate(newName);
        profile.Name = newName;
        Save(profile);
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
