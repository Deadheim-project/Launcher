using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>
/// Resolve a versão/zip de um pacote no Hexium (https://valheim.hexium.gg).
///
/// O Hexium é uma segunda plataforma de mods de Valheim, com a mesma forma de API
/// do Thunderstore — o mesmo `/api/experimental/package/{namespace}/{name}/`, e
/// pacotes identificados por namespace+nome. Entrou aqui porque publica antes:
/// no dia do lançamento do Valheim 1.0, AzuExtendedPlayerInventory 2.4.12 e
/// ServerCharacters 1.4.17 existiam só nele.
///
/// É fonte *adicional*, não substituta. Um mod que já funciona pelo Thunderstore
/// continua vindo de lá; só declara `source: Hexium` quem precisa.
///
/// Diferença que obriga um serviço próprio em vez de reaproveitar o do
/// Thunderstore: <b>a URL de download não é previsível</b>. O Thunderstore serve
/// em /package/download/{ns}/{name}/{versão}/, e por isso um mod pinado lá é
/// baixado sem gastar nenhuma chamada de API. O Hexium responde 404 nesse
/// caminho e entrega o zip por um id opaco de CDN
/// (https://cdn.hexium.gg/upload/11/2.4.12.zip), que só a API conhece. Então aqui
/// sempre há uma consulta — inclusive para versão fixada, que usa o endpoint da
/// versão exata para não baixar a mais recente por engano.
/// </summary>
public sealed class HexiumService
{
    public const string BaseUrl = "https://valheim.hexium.gg";

    private readonly HttpClient _http;

    public HexiumService(HttpClient http)
    {
        _http = http;
    }

    public async Task<ResolvedModVersion> GetLatestAsync(ModEntry mod, CancellationToken ct = default)
    {
        if (mod.Source != ModSource.Hexium ||
            string.IsNullOrWhiteSpace(mod.ThunderstoreNamespace) ||
            string.IsNullOrWhiteSpace(mod.ThunderstoreName))
        {
            throw new InvalidOperationException($"Mod '{mod.Id}' não é um pacote Hexium válido.");
        }

        var ns = mod.ThunderstoreNamespace;
        var name = mod.ThunderstoreName;

        // Versão fixada consulta o endpoint daquela versão; sem versão, o do pacote,
        // que traz "latest".
        var pinned = !string.IsNullOrWhiteSpace(mod.Version);
        var url = pinned
            ? $"{BaseUrl}/api/experimental/package/{ns}/{name}/{mod.Version}/"
            : $"{BaseUrl}/api/experimental/package/{ns}/{name}/";

        using var response = await HttpRetry.SendAsync(_http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DeadheimLauncher", "1.0"));
            return request;
        }, ct: ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Hexium respondeu {(int)response.StatusCode} para {ns}/{name}" +
                (pinned ? $" versão {mod.Version}." : "."));
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // O endpoint do pacote embrulha em "latest"; o da versão devolve o objeto direto.
        var payload = doc.RootElement.TryGetProperty("latest", out var latest)
            ? latest
            : doc.RootElement;

        var version = payload.TryGetProperty("version_number", out var v)
            ? v.GetString() ?? mod.Version ?? "unknown"
            : mod.Version ?? "unknown";

        if (!payload.TryGetProperty("download_url", out var d) || d.GetString() is not { Length: > 0 } downloadUrl)
            throw new InvalidOperationException($"Hexium não devolveu download_url para {ns}/{name}.");

        return new ResolvedModVersion(version, downloadUrl, $"{ns}-{name}-{version}.zip");
    }
}
