using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DeadheimLauncher.Services;

public sealed record AtualizacaoDisponivel(string Versao, string UrlDoInstalador, string Notas);

/// <summary>
/// Verifica se saiu versão nova do launcher e aplica sozinho.
///
/// Sem isso, corrigir um defeito significa pedir para cada jogador baixar o
/// instalador de novo — e quem não fizer fica com a versão velha, o que é
/// exatamente o público que este launcher existe para atender.
/// </summary>
public sealed class AutoAtualizacaoService
{
    private const string Repositorio = "Deadheim-project/Launcher";
    private const string NomeDoAsset = "DeadheimLauncherSetup.exe";

    private readonly HttpClient _http;

    public AutoAtualizacaoService(HttpClient http) => _http = http;

    public static string VersaoAtual =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Devolve a atualização se houver uma mais nova; null se já está em dia.</summary>
    public async Task<AtualizacaoDisponivel?> ProcurarAsync(CancellationToken ct = default)
    {
        using var resposta = await HttpRetry.SendAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repositorio}/releases/latest");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("DeadheimLauncher", "1.0"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return req;
        }, ct: ct);

        if (!resposta.IsSuccessStatusCode) return null;

        using var stream = await resposta.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var raiz = doc.RootElement;

        var tag = raiz.GetProperty("tag_name").GetString() ?? "";
        if (!EhMaisNova(tag, VersaoAtual)) return null;

        foreach (var asset in raiz.GetProperty("assets").EnumerateArray())
        {
            var nome = asset.GetProperty("name").GetString() ?? "";
            if (!string.Equals(nome, NomeDoAsset, StringComparison.OrdinalIgnoreCase)) continue;

            return new AtualizacaoDisponivel(
                tag.TrimStart('v', 'V'),
                asset.GetProperty("browser_download_url").GetString()!,
                raiz.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
        }

        // Release nova sem instalador anexado não serve para atualizar sozinho.
        return null;
    }

    /// <summary>
    /// Compara versões numericamente. Texto puro não serve: "1.0.10" é menor que
    /// "1.0.9" em ordem alfabética, o que travaria a atualização justo depois de
    /// dez lançamentos.
    /// </summary>
    public static bool EhMaisNova(string? tagRemota, string? versaoLocal)
    {
        var remota = Analisar(tagRemota);
        var local = Analisar(versaoLocal);
        if (remota is null || local is null) return false;
        return remota > local;
    }

    private static Version? Analisar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var limpo = texto.Trim().TrimStart('v', 'V');

        // Descarta sufixos tipo "-beta", que Version não aceita.
        var corte = limpo.IndexOfAny(new[] { '-', '+', ' ' });
        if (corte > 0) limpo = limpo[..corte];

        return Version.TryParse(limpo, out var v) ? v : null;
    }

    /// <summary>
    /// Baixa o instalador e o executa. O instalador fecha o launcher, substitui
    /// os arquivos e o abre de novo — por isso o processo atual sai logo em
    /// seguida, sem esperar.
    /// </summary>
    public async Task<string> BaixarInstaladorAsync(
        AtualizacaoDisponivel atualizacao,
        IProgress<double>? progresso = null,
        CancellationToken ct = default)
    {
        var destino = Path.Combine(Path.GetTempPath(), $"DeadheimLauncherSetup-{atualizacao.Versao}.exe");

        using var resposta = await HttpRetry.SendAsync(_http,
            () => new HttpRequestMessage(HttpMethod.Get, atualizacao.UrlDoInstalador), ct: ct);

        if (!resposta.IsSuccessStatusCode)
            throw new InvalidOperationException($"Download do instalador falhou: HTTP {(int)resposta.StatusCode}.");

        var total = resposta.Content.Headers.ContentLength ?? 0;
        await using (var arquivo = File.Create(destino))
        await using (var origem = await resposta.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long lidos = 0;
            int n;
            while ((n = await origem.ReadAsync(buffer, ct)) > 0)
            {
                await arquivo.WriteAsync(buffer.AsMemory(0, n), ct);
                lidos += n;
                if (total > 0) progresso?.Report(lidos * 100.0 / total);
            }
        }

        MarkOfTheWeb.Unblock(destino);
        return destino;
    }

    public static void ExecutarInstalador(string caminho)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = caminho,
            // /SILENT mostra a barra do instalador sem fazer perguntas, e
            // FORCECLOSEAPPLICATIONS deixa ele substituir o exe em uso.
            Arguments = "/SILENT /NORESTART /FORCECLOSEAPPLICATIONS",
            UseShellExecute = true
        });
    }
}
