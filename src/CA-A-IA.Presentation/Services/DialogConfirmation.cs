// CA-A-IA — Confirmación humana vía ContentDialog (puerta del §15 en la UI real).

using CaAIA.Domain.Interaction;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace CaAIA.Presentation.Services;

/// <summary>
/// Pregunta al usuario en el UI thread. Cancelar el token equivale a denegar (y cierra el diálogo).
/// </summary>
public sealed class DialogConfirmation : IUserConfirmation
{
    private readonly Func<MainWindow> _window;

    public DialogConfirmation(Func<MainWindow> window)
    {
        _window = window;
    }

    public Task<bool> RequestAsync(string title, string details, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow window;
        try
        {
            window = _window();
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }

        var queue = window.DispatcherQueue;
        if (!queue.TryEnqueue(() => Show(window, title, details, tcs, cancellationToken)))
        {
            return Task.FromResult(false);
        }

        if (cancellationToken.CanBeCanceled)
        {
            // El registro se libera al completarse la petición (antes fugaba uno por diálogo).
            var registration = cancellationToken.Register(() => tcs.TrySetResult(false));
            _ = tcs.Task.ContinueWith(
                static (_, state) => ((IDisposable)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return tcs.Task;
    }

    private void Show(
        MainWindow window, string title, string details, TaskCompletionSource<bool> tcs, CancellationToken ct)
    {
        ContentDialog dialog;
        try
        {
            var xamlRoot = window.Content?.XamlRoot;
            if (xamlRoot is null)
            {
                // Ventana aún sin contenido (pre-Activate): denegar en vez de NullReference
                // en el UI thread (que dejaría el TCS colgado).
                tcs.TrySetResult(false);
                return;
            }

            dialog = new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer
                {
                    MaxHeight = 400,
                    Content = new TextBlock { Text = details, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                },
                PrimaryButtonText = "Permitir",
                CloseButtonText = "Denegar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };
        }
        catch (Exception)
        {
            tcs.TrySetResult(false);
            return;
        }

        // El registro debe vivir hasta que el diálogo cierre: antes se disponía al
        // salir de Show (inmediato, fire-and-forget) y cancelar ya no cerraba nada.
        var registration = ct.Register(() =>
            window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    dialog.Hide();
                }
                catch (Exception)
                {
                }
            }));
        _ = ShowAsync(dialog, tcs, registration);
    }

    private static async Task ShowAsync(ContentDialog dialog, TaskCompletionSource<bool> tcs, IDisposable registration)
    {
        using (registration)
        {
            try
            {
                var result = await dialog.ShowAsync();
                tcs.TrySetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception)
            {
                tcs.TrySetResult(false);
            }
        }
    }
}
