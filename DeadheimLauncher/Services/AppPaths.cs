namespace DeadheimLauncher.Services;

/// <summary>Caminhos fixos usados pelo launcher em %AppData%\DeadheimLauncher.</summary>
public static class AppPaths
{
    private static readonly string DefaultRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeadheimLauncher");

    private static string? _rootOverride;

    public static string Root => _rootOverride ?? DefaultRoot;

    /// <summary>
    /// Redireciona toda a persistência para outra pasta. Existe para o self-test
    /// rodar numa pasta descartável em vez de mexer nos perfis reais do jogador.
    /// </summary>
    public static void UseRoot(string root) => _rootOverride = root;

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ProfilesDir => Path.Combine(Root, "profiles");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string ManifestCacheFile => Path.Combine(CacheDir, "manifest.json");

    public static string ProfileDir(string profileName) => Path.Combine(ProfilesDir, profileName);
    public static string ProfileFile(string profileName) => Path.Combine(ProfileDir(profileName), "profile.json");

    /// <summary>
    /// Raiz "de jogo" do perfil: uma árvore BepInEx completa que fica fora da
    /// pasta do Valheim. O jogo é iniciado apontando o Doorstop para cá, então
    /// os mods do servidor nunca entram na instalação do jogador — e nada que
    /// ele tenha posto lá na mão é tocado.
    /// </summary>
    public static string ProfileGameDir(string profileName) => Path.Combine(ProfileDir(profileName), "game");

    public static string ProfileBepInExDir(string profileName) => Path.Combine(ProfileGameDir(profileName), "BepInEx");

    public static string ProfilePluginsDir(string profileName) => Path.Combine(ProfileBepInExDir(profileName), "plugins");

    /// <summary>
    /// Pacotes que se instalam na raiz do Valheim em vez de BepInEx/plugins —
    /// hoje só o próprio BepInEx. Ver InstallTarget.GameRoot.
    /// </summary>
    /// <summary>
    /// Pacotes que se instalam na raiz do jogo — hoje só o próprio BepInEx.
    /// Vai para a raiz de jogo do perfil, não para a pasta do Valheim.
    /// </summary>
    public static string ProfileGameRootDir(string profileName) => ProfileGameDir(profileName);

    /// <summary>
    /// Arquivos .cfg que vão para BepInEx/config, e não para dentro da pasta do
    /// mod. Um pacote que traz config/ está entregando a configuração do
    /// servidor; se ela cair em plugins/&lt;mod&gt;/config/ o BepInEx não lê, o mod
    /// gera um .cfg padrão e o jogador roda com valores diferentes dos do
    /// servidor — sem erro nenhum aparecendo.
    /// </summary>
    public static string ProfileConfigDir(string profileName) => Path.Combine(ProfileBepInExDir(profileName), "config");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(CacheDir);
    }
}
