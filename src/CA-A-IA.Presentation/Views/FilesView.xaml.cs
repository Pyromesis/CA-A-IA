using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CaAIA.Presentation.Views;

public sealed partial class FilesView : UserControl
{
    public FilesViewModel ViewModel { get; }

    public FilesView()
    {
        ViewModel = App.Services.GetRequiredService<FilesViewModel>();
        InitializeComponent();
    }
}
