using System.IO;
using System.Windows;

namespace DeadheimLauncher.Views;

public partial class InputDialog : Window
{
    private readonly Func<string, string?>? _validar;

    public string? ResponseText { get; private set; }

    /// <param name="validar">
    /// Devolve a mensagem de erro, ou null se o valor serve. Roda antes de
    /// fechar a janela: o usuário corrige ali mesmo, em vez de a janela fechar
    /// e a falha aparecer depois na barra de status.
    /// </param>
    public InputDialog(string message, string defaultValue, string? hint = null, Func<string, string?>? validar = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        ResponseBox.Text = defaultValue;
        _validar = validar;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        // Erro some assim que a pessoa começa a corrigir.
        ResponseBox.TextChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            ResponseBox.SelectAll();
            ResponseBox.Focus();
        };
    }

    /// <summary>
    /// Nome de perfil vira nome de pasta. Sem isso, um "/" no nome estoura uma
    /// exceção de caminho inválido lá no ProfileService, longe de onde o usuário
    /// digitou.
    /// </summary>
    public static string? ValidarNomeDePerfil(string nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
            return "Dê um nome ao perfil.";

        var invalidos = Path.GetInvalidFileNameChars();
        if (nome.Any(invalidos.Contains))
            return "O nome não pode conter " + string.Join(" ", @"\ / : * ? "" < > |".Split(' ')) + ".";

        if (nome.Length > 60)
            return "Use um nome mais curto (até 60 caracteres).";

        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var valor = ResponseBox.Text.Trim();

        var erro = _validar?.Invoke(valor);
        if (erro is not null)
        {
            ErrorText.Text = erro;
            ErrorText.Visibility = Visibility.Visible;
            ResponseBox.Focus();
            return;
        }

        ResponseText = valor;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
