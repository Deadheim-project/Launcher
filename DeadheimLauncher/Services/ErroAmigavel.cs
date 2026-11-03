using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace DeadheimLauncher.Services;

/// <summary>
/// Converte exceção em mensagem que o jogador consegue agir.
///
/// Antes, a barra de status e os diálogos mostravam ex.Message cru — coisas como
/// "Response status code does not indicate success: 404 (Not Found)" ou
/// "The process cannot access the file because it is being used by another
/// process". Isso não diz o que aconteceu nem o que fazer, e vira mensagem no
/// Discord perguntando o que significa.
/// </summary>
public static class ErroAmigavel
{
    /// <param name="contexto">
    /// O que estava sendo feito, ex. "baixar o mod RaidSystem". Entra na frase
    /// para a pessoa saber em que ponto quebrou.
    /// </param>
    public static string Descrever(Exception ex, string? contexto = null)
    {
        var detalhe = Traduzir(ex);
        return contexto is null ? detalhe : $"Falha ao {contexto}: {detalhe}";
    }

    private static string Traduzir(Exception ex) => ex switch
    {
        ValheimNotFoundException => "não encontrei a pasta do Valheim. Abra Configurações e aponte onde o jogo está instalado.",

        BepInExNotFoundException => "o BepInEx não está instalado no Valheim. Marque o BepInEx na lista de mods e clique em Jogar de novo — o launcher instala sozinho.",

        // Sem rede: o caso mais comum, e o mais mal explicado antes.
        HttpRequestException http when http.InnerException is SocketException
            => "não consegui falar com a internet. Confira sua conexão e tente de novo.",

        HttpRequestException http when http.StatusCode == HttpStatusCode.NotFound
            => "o arquivo não existe mais no servidor. A lista de mods pode estar desatualizada — clique em Atualizar lista.",

        HttpRequestException http when http.StatusCode == HttpStatusCode.TooManyRequests
            => "o servidor de mods pediu para esperar um pouco (limite de downloads). Tente de novo em alguns minutos.",

        HttpRequestException http when http.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            => "o servidor recusou o download. Se o repositório do mod for privado, ele precisa ser público.",

        HttpRequestException http when (int?)http.StatusCode >= 500
            => "o servidor de mods está fora do ar no momento. Tente de novo mais tarde.",

        HttpRequestException => "não consegui baixar o arquivo. Confira sua conexão e tente de novo.",

        TaskCanceledException or OperationCanceledException
            => "a operação demorou demais e foi cancelada. Pode ser conexão lenta — tente de novo.",

        UnauthorizedAccessException
            => "o Windows bloqueou o acesso ao arquivo. Feche o Valheim se estiver aberto e tente de novo.",

        IOException io when io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            => "um arquivo está em uso. Feche o Valheim e tente de novo.",

        IOException io when EhDiscoCheio(io)
            => "acabou o espaço em disco. Libere espaço e tente de novo.",

        IOException => "erro ao gravar os arquivos do mod. Feche o Valheim e tente de novo.",

        InvalidDataException => "o arquivo baixado veio corrompido. Clique em Jogar de novo para baixar outra vez.",

        _ => string.IsNullOrWhiteSpace(ex.Message) ? "erro inesperado." : ex.Message
    };

    private static bool EhDiscoCheio(IOException io)
    {
        // 0x70 ERROR_DISK_FULL, 0x27 ERROR_HANDLE_DISK_FULL
        var codigo = io.HResult & 0xFFFF;
        return codigo is 0x70 or 0x27;
    }
}
