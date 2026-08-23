using System.Net.Http;
using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>
/// Busca o manifest.json remoto (lista de mods do servidor Deadheim). Se a
/// requisição falhar (sem internet, URL não configurada ainda, etc.), cai
/// pro cache local mais recente e, na ausência dele, pro manifest.sample.json
/// embutido no launcher — assim o app nunca fica sem lista de mods pra mostrar.
/// </summary>
public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public ManifestService(HttpClient http)
    {
        _http = http;
    }

    public async Task<ModManifest> GetManifestAsync(string manifestUrl, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(manifestUrl, ct);
            var manifest = Interpretar(json);
            if (manifest is not null)
            {
                AppPaths.EnsureDirs();
                File.WriteAllText(AppPaths.ManifestCacheFile, json);
                return manifest;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // sem rede, URL ainda não configurada, ou manifest que esta versão do
            // launcher não entende: cai pro cache/sample abaixo
        }

        return LerDoDisco(AppPaths.ManifestCacheFile)
            ?? LerDoDisco(Path.Combine(AppContext.BaseDirectory, "manifest.sample.json"))
            ?? new ModManifest();
    }

    /// <summary>
    /// Interpreta um manifest com as mesmas regras usadas em produção, e deixa a
    /// exceção subir. É o que a suíte de testes chama para conferir o arquivo
    /// publicado: GetManifestAsync engole JsonException de propósito, então
    /// testar por ela esconderia justamente um manifest ilegível.
    /// </summary>
    public static ModManifest? Interpretar(string json) =>
        JsonSerializer.Deserialize<ModManifest>(json, JsonOptions);

    /// <summary>
    /// Lê um manifest de disco sem nunca estourar: arquivo ilegível ou que este
    /// launcher não sabe interpretar vale como ausente, e a busca segue para a
    /// próxima alternativa.
    ///
    /// Antes o cache era desserializado solto. O caso que quebrava: o servidor
    /// publica um manifest com um valor novo (um InstallTarget que só existe numa
    /// versão mais nova do launcher), uma versão nova grava esse texto no cache, e
    /// aí um launcher antigo lê o cache — a exceção escapava do método inteiro,
    /// pulava o manifest.sample.json embutido e ia parar crua na barra de status
    /// como "The JSON value could not be converted to ...". A promessa do
    /// comentário da classe, de nunca ficar sem lista de mods, não valia.
    /// </summary>
    private static ModManifest? LerDoDisco(string caminho)
    {
        try
        {
            return File.Exists(caminho)
                ? Interpretar(File.ReadAllText(caminho))
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
