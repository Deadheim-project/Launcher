using CommunityToolkit.Mvvm.ComponentModel;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.ViewModels;

/// <summary>Um mod na lista da UI: dados do manifest + estado de habilitado/instalação do perfil atual.</summary>
public sealed partial class ModListItemViewModel : ObservableObject
{
    public ModEntry Entry { get; }

    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public bool IsRequired => Entry.Required;

    public string SourceLabel => Entry.Source == ModSource.GitHub ? "Mod do servidor" : "Thunderstore";

    /// <summary>Versão fixada pelo pack, ou "mais recente" quando o manifest não pina.</summary>
    public string VersionLabel =>
        string.IsNullOrWhiteSpace(Entry.Version) ? "mais recente" : "v" + Entry.Version;

    /// <summary>Crédito ao autor do mod. Vazio some da tela em vez de virar "por ".</summary>
    public string AuthorLabel =>
        string.IsNullOrWhiteSpace(Entry.Author) ? "" : "por " + Entry.Author;

    public bool TemAutor => !string.IsNullOrWhiteSpace(Entry.Author);

    /// <summary>
    /// Página oficial do mod, para o crédito ser clicável.
    ///
    /// Nunca devolve null: Hyperlink.NavigateUri é do tipo Uri e o WPF avalia o
    /// binding mesmo com o elemento recolhido, então null viraria erro de
    /// binding em toda a lista. Quando não há página, o link fica invisível
    /// (ver TemLink) e este valor nunca é usado.
    /// </summary>
    public string Url => string.IsNullOrWhiteSpace(Entry.Url) ? "about:blank" : Entry.Url;

    public bool TemLink => !string.IsNullOrWhiteSpace(Entry.Url);

    /// <summary>
    /// Autor conhecido, mas sem página. Existe porque Hyperlink.NavigateUri é do
    /// tipo Uri e estoura o binding com null — o crédito precisa aparecer como
    /// texto simples nesse caso, não sumir.
    /// </summary>
    public bool TemAutorSemLink => TemAutor && !TemLink;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _statusText = "";

    public ModListItemViewModel(ModEntry entry, bool isEnabled)
    {
        Entry = entry;
        _isEnabled = isEnabled || entry.Required;
    }
}
