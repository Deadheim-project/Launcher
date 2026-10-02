namespace DeadheimLauncher.Models;

/// <summary>
/// Apresentação do servidor na tela inicial: o que é o Deadheim, para quem
/// abre o launcher pela primeira vez. Vem no manifest, como o changelog, para
/// que mudar uma regra do servidor e o texto que a descreve seja o mesmo
/// commit, sem publicar launcher novo.
/// </summary>
public sealed class ServerPresentation
{
    /// <summary>Chamada em uma linha (ex. "Temporada PvP no Valheim 1.0").</summary>
    public string? Title { get; set; }

    /// <summary>Parágrafo curto sobre o servidor, logo abaixo da chamada.</summary>
    public string? About { get; set; }

    /// <summary>Cartões com os sistemas do servidor, dois por linha na tela.</summary>
    public List<PresentationHighlight> Highlights { get; set; } = new();

    public bool TemTitulo => !string.IsNullOrWhiteSpace(Title);
    public bool TemSobre => !string.IsNullOrWhiteSpace(About);
}

/// <summary>Um cartão da apresentação: nome do sistema e uma ou duas frases sobre ele.</summary>
public sealed class PresentationHighlight
{
    public string Title { get; set; } = "";
    public string? Text { get; set; }
}
