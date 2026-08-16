using System.Windows;
using DeadheimLauncher.ViewModels;

namespace DeadheimLauncher.Views;

public partial class MainWindow : Window
{
    /// <summary>Abre a página do mod no navegador padrão.</summary>
    private void AbrirLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // Navegador ausente ou bloqueado não é motivo para derrubar o launcher.
        }
        e.Handled = true;
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
