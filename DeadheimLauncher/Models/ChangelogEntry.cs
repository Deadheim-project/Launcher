namespace DeadheimLauncher.Models;

/// <summary>Uma entrada do changelog do manifest: uma versão e o que mudou nela.</summary>
public sealed class ChangelogEntry
{
    /// <summary>Rótulo livre: "1.0-pvp.5" para o pacote, "Launcher 1.2.6" para o launcher.</summary>
    public string Version { get; set; } = "";

    /// <summary>Data como deve aparecer na tela (ex. "28/09/2026").</summary>
    public string? Date { get; set; }

    /// <summary>Resumo opcional de uma linha.</summary>
    public string? Title { get; set; }

    public List<string> Changes { get; set; } = new();

    public bool TemTitulo => !string.IsNullOrWhiteSpace(Title);
    public bool TemData => !string.IsNullOrWhiteSpace(Date);
}
