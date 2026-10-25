using System.Runtime.InteropServices;

namespace DeadheimLauncher.Services;

/// <summary>
/// Ponte com o Wine, para quem roda o launcher no Linux.
///
/// No Wine o drive C: é a pasta do prefixo (~/.wine/drive_c), e a raiz do
/// Linux aparece como Z:. Jogador de Linux copia o caminho do gerenciador de
/// arquivos (/run/media/...) e às vezes põe um C: na frente; nenhum dos dois
/// existe para o launcher. Aqui ficam a detecção do Wine e a conversão de
/// caminhos nos dois sentidos.
/// </summary>
public static class Wine
{
    private static readonly Lazy<bool> _ativo = new(Detectar);

    /// <summary>true quando o processo está rodando dentro do Wine (ou Proton).</summary>
    public static bool Ativo => _ativo.Value;

    private static bool Detectar()
    {
        try
        {
            // O ntdll do Wine exporta wine_get_version; o do Windows não.
            var ntdll = GetModuleHandleW("ntdll.dll");
            return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Caminho Windows (C:\users\..., Z:\home\...) para o caminho real no Linux.
    /// Null fora do Wine, ou se o Wine não souber converter.
    /// </summary>
    public static string? ParaCaminhoUnix(string caminhoWindows)
    {
        if (!Ativo) return null;
        try
        {
            var ptr = wine_get_unix_file_name(Path.GetFullPath(caminhoWindows));
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(ptr); }
            finally { HeapFree(GetProcessHeap(), 0, ptr); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Caminho do Linux para o caminho que o Wine enxerga. Sem o Wine para
    /// perguntar, usa o Z:, que é onde o Wine monta a raiz por padrão.
    /// </summary>
    public static string ParaCaminhoWindows(string caminhoUnix)
    {
        if (Ativo)
        {
            try
            {
                var ptr = wine_get_dos_file_name(caminhoUnix);
                if (ptr != IntPtr.Zero)
                {
                    try
                    {
                        var convertido = Marshal.PtrToStringUni(ptr);
                        if (!string.IsNullOrEmpty(convertido)) return convertido;
                    }
                    finally { HeapFree(GetProcessHeap(), 0, ptr); }
                }
            }
            catch
            {
                // cai no Z: abaixo
            }
        }

        return NoDriveZ(caminhoUnix);
    }

    public static string NoDriveZ(string caminhoUnix) => "Z:" + caminhoUnix.Replace('/', '\\');

    /// <summary>
    /// Formas que o caminho digitado pelo jogador pode ter querido dizer, na
    /// ordem de tentativa. Puro, para o self-test verificar sem Wine.
    ///
    /// "/run/media/x/Valheim"    → Z:\run\media\x\Valheim
    /// "C:\run\media\x\Valheim"  → o próprio, e depois Z:\run\media\x\Valheim
    /// "~/.steam/steam/..."      → Z:\&lt;home&gt;\.steam\steam\... (se souber o home)
    /// </summary>
    public static IReadOnlyList<string> CandidatosParaCaminhoInformado(string informado, string? homeUnix)
    {
        var caminho = informado.Trim().Trim('"', '\'').Trim();
        var candidatos = new List<string>();
        if (caminho.Length == 0) return candidatos;

        if (caminho.StartsWith("~/") && !string.IsNullOrEmpty(homeUnix))
            caminho = homeUnix.TrimEnd('/') + caminho[1..];

        if (caminho.StartsWith('/'))
        {
            candidatos.Add(NoDriveZ(caminho));
            return candidatos;
        }

        candidatos.Add(caminho);

        // Letra de drive diferente do Z: com o resto de um caminho do Linux.
        if (caminho.Length > 3 && char.IsLetter(caminho[0]) && caminho[1] == ':' && caminho[2] is '\\' or '/'
            && char.ToUpperInvariant(caminho[0]) != 'Z')
        {
            candidatos.Add("Z:" + caminho[2..].Replace('/', '\\'));
        }

        return candidatos;
    }

    /// <summary>Home do usuário do Linux, em formato Unix. Null fora do Wine ou se não achar.</summary>
    public static string? HomeUnix()
    {
        if (!Ativo) return null;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home) && home.StartsWith('/')) return home;

        // O Wine publica o home como \??\Z:\home\usuario.
        var wineHome = Environment.GetEnvironmentVariable("WINEHOMEDIR");
        if (!string.IsNullOrEmpty(wineHome))
        {
            var dos = wineHome.StartsWith(@"\??\") ? wineHome[4..] : wineHome;
            var unix = ParaCaminhoUnix(dos);
            if (!string.IsNullOrEmpty(unix)) return unix;
        }

        var usuario = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
        return string.IsNullOrEmpty(usuario) ? null : "/home/" + usuario;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll")]
    private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

    // Exportadas pelo kernel32 do Wine. Só são chamadas depois de Ativo == true.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr wine_get_unix_file_name(string dosName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr wine_get_dos_file_name([MarshalAs(UnmanagedType.LPUTF8Str)] string unixName);
}
