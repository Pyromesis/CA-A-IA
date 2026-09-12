using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CaAIA.Presentation.Views;

/// <summary>Pestaña Memoria: red arrastrable + vista previa por clic.</summary>
public sealed partial class MemoriaView : UserControl
{
    public MemoriaViewModel ViewModel { get; }

    private MemoryGraphNodeVm? _dragged;
    private Windows.Foundation.Point _grabLocal;

    public MemoriaView()
    {
        ViewModel = App.Services.GetRequiredService<MemoriaViewModel>();
        InitializeComponent();
    }

    private void Node_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.DataContext is MemoryGraphNodeVm node)
        {
            _dragged = node;
            // Todo relativo al propio nodo: inmune a scroll/zoom/transforms.
            _grabLocal = e.GetCurrentPoint(border).Position;
            border.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void Node_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragged is null || sender is not Border border || border.DataContext != _dragged)
        {
            return;
        }

        var at = e.GetCurrentPoint(border).Position;
        _dragged.X = Math.Max(0, _dragged.X + at.X - _grabLocal.X);
        _dragged.Y = Math.Max(0, _dragged.Y + at.Y - _grabLocal.Y);
        ViewModel.UpdateEdgesFor(_dragged);
        e.Handled = true;
    }

    private void Node_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            try
            {
                border.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception)
            {
            }
        }

        _dragged = null;
    }

    private void Node_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border border && border.DataContext is MemoryGraphNodeVm node)
        {
            ViewModel.Select(node);
            e.Handled = true;
        }
    }
}
