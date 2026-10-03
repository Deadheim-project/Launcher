using System.Net.Http;
using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>
/// Busca o rankings.json publicado pelo workflow rankings.yml. Sem rede, mostra
/// o último que foi baixado, com a data dele; sem nem isso, a aba fica com o
/// aviso de que o ranking não carregou. Nunca derruba a tela inicial.
/// </summary>
public sealed class RankingsService
{
    /// <summary>
    /// Onde o workflow publica. Branch próprio, e não o main, porque é
    /// reescrito a cada poucos minutos: no main seria um commit por atualização.
    /// </summary>
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/Deadheim-project/Launcher/rankings/rankings.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public RankingsService(HttpClient http)
    {
        _http = http;
    }

    public static string CacheFile => Path.Combine(AppPaths.CacheDir, "rankings.json");

    /// <summary>Ranking mais recente, ou o do cache se a rede falhar. Nulo se não há nenhum.</summary>
    public async Task<Rankings?> GetAsync(string? url, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var json = await _http.GetStringAsync(string.IsNullOrWhiteSpace(url) ? DefaultUrl : url, cts.Token);
            var rankings = Interpretar(json);
            if (rankings is not null)
            {
                AppPaths.EnsureDirs();
                File.WriteAllText(CacheFile, json);
                return rankings;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            // sem rede, branch ainda não publicado ou arquivo quebrado: cai pro cache
        }

        try
        {
            return File.Exists(CacheFile) ? Interpretar(File.ReadAllText(CacheFile)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Interpreta e numera as posições. Deixa a exceção subir (o self-test usa).</summary>
    public static Rankings? Interpretar(string json)
    {
        var rankings = JsonSerializer.Deserialize<Rankings>(json, JsonOptions);
        if (rankings is null) return null;

        rankings.Guilds = rankings.Guilds.Where(g => !string.IsNullOrWhiteSpace(g.Name)).ToList();
        rankings.Hunted = rankings.Hunted.Where(h => !string.IsNullOrWhiteSpace(h.Name)).ToList();
        rankings.Pvp = rankings.Pvp.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

        for (var i = 0; i < rankings.Guilds.Count; i++) rankings.Guilds[i].Position = i + 1;
        for (var i = 0; i < rankings.Hunted.Count; i++) rankings.Hunted[i].Position = i + 1;
        for (var i = 0; i < rankings.Pvp.Count; i++) rankings.Pvp[i].Position = i + 1;
        return rankings;
    }
}
