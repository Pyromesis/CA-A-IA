using CaAIA.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CaAIA.Presentation.Views;

/// <summary>Elige la burbuja del chat según el rol (usuario a la derecha en acento,
/// agente a la izquierda en tarjeta, sistema centrado sutil).</summary>
public sealed class ChatBubbleSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AgentTemplate { get; set; }
    public DataTemplate? SystemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        ChatMessage { Role: ChatRole.User } => UserTemplate ?? base.SelectTemplateCore(item),
        ChatMessage { Role: ChatRole.Agent } => AgentTemplate ?? base.SelectTemplateCore(item),
        ChatMessage => SystemTemplate ?? base.SelectTemplateCore(item),
        _ => base.SelectTemplateCore(item),
    };
}

/// <summary>Chat literal: burbujas, selector de carpeta, selector de modelo, Enter para enviar.</summary>
public sealed partial class ChatView : UserControl
{
    public ChatViewModel ViewModel { get; }

    public ChatView()
    {
        ViewModel = App.Services.GetRequiredService<ChatViewModel>();
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
        Loaded += (_, _) => ScrollToBottom();
        // Las vistas son transient y el VM singleton: sin desuscribir, el VM retiene
        // cada vista muerta (fuga + N ScrollIntoView por mensaje).
        Unloaded += (_, _) => ViewModel.Messages.CollectionChanged -= OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Solo autoscroll ante mensajes nuevos al final (no robar el scroll si el
        // usuario subió a leer, ni en recargas masivas del historial).
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

    private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");

            // Las pickers WinRT exigen HWND en apps desempaquetadas.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
                App.Services.GetRequiredService<MainWindow>());
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.WorkspacePath = folder.Path;
            }
        }
        catch (Exception ex)
        {
            // El picker puede fallar (HWND, permisos, cancelación del sistema):
            // no tumbar la UI; el workspace actual se conserva.
            System.Diagnostics.Debug.WriteLine($"PickFolder failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
