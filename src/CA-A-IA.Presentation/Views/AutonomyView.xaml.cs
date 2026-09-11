using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CaAIA.Presentation.Views;

/// <summary>Pestaña Autonomía: conversación + composer con Enter para enviar.</summary>
public sealed partial class AutonomyView : UserControl
{
    public AutonomyViewModel ViewModel { get; }

    public AutonomyView()
    {
        ViewModel = App.Services.GetRequiredService<AutonomyViewModel>();
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
        Loaded += (_, _) => ScrollToBottom();
        // El VM es singleton y la vista transient: desuscribir al salir.
        Unloaded += (_, _) => ViewModel.Messages.CollectionChanged -= OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom()
    {
        if (MessagesList.Items.Count > 0)
        {
            MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
        }
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
