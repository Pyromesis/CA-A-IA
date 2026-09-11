using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CaAIA.Presentation.Views;

public sealed partial class TasksView : UserControl
{
    public TasksViewModel ViewModel { get; }

    public TasksView()
    {
        ViewModel = App.Services.GetRequiredService<TasksViewModel>();
        InitializeComponent();
    }
}
