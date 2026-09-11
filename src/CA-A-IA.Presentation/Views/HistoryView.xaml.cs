using CaAIA.Application.Services;
using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CaAIA.Presentation.Views;

/// <summary>Historial de conversaciones: abrir una la carga en el Chat.</summary>
public sealed partial class HistoryView : UserControl
{
    public HistoryViewModel ViewModel { get; }

    public HistoryView()
    {
        ViewModel = App.Services.GetRequiredService<HistoryViewModel>();
        InitializeComponent();
        // La vista es transient: cada navegación crea una instancia, así que
        // recargar aquí garantiza ver lo conversado hasta ahora (no solo lo del arranque).
        Loaded += (_, _) =>
        {
            if (ViewModel.RefreshCommand.CanExecute(null))
            {
                ViewModel.RefreshCommand.Execute(null);
            }
        };
    }

    private async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list && list.SelectedItem is ConversationSegment segment)
        {
            list.SelectedItem = null;
            list.IsEnabled = false;
            try
            {
                // Esperar a que el Chat cargue la conversación ANTES de navegar:
                // si no, se ve el contenido anterior y parece que "no abre".
                await ViewModel.OpenCommand.ExecuteAsync(segment);
                App.Services.GetRequiredService<MainWindow>().Navigate("chat");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Open conversation failed: {ex.GetType().Name}: {ex.Message}");
                ViewModel.StatusText = $"No se pudo abrir la conversación: {ex.Message}";
            }
            finally
            {
                list.IsEnabled = true;
            }
        }
    }
}
