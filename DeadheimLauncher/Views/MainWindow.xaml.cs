using System.Windows;
using DeadheimLauncher.ViewModels;

namespace DeadheimLauncher.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// ContextMenu normalmente só abre com o botão direito; aqui ele é o menu do
    /// botão, então precisa abrir no clique esquerdo e ancorado no próprio botão.
    /// </summary>
    private void AbrirMenuDePerfil(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button botao || botao.ContextMenu is null) return;

        botao.ContextMenu.PlacementTarget = botao;
        botao.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        botao.ContextMenu.DataContext = DataContext;
        botao.ContextMenu.IsOpen = true;
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                await vm.InitializeAsync();
        };
    }
}
