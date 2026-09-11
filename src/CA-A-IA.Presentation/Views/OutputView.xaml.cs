using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CaAIA.Presentation.Views;

public sealed partial class OutputView : UserControl
{
    public OutputViewModel ViewModel { get; }

    public OutputView()
    {
        ViewModel = App.Services.GetRequiredService<OutputViewModel>();
        InitializeComponent();
    }
}
