using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CaAIA.Presentation.Views;

public sealed partial class PlanView : UserControl
{
    public PlanViewModel ViewModel { get; }

    public PlanView()
    {
        ViewModel = App.Services.GetRequiredService<PlanViewModel>();
        InitializeComponent();
    }
}
