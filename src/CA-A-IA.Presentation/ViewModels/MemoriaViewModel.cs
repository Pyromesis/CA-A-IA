// CA-A-IA — Pestaña Memoria: la red de conocimiento (.md de la bóveda +
// lecciones globales) movible, con vista previa por clic.
//
// NOTA (MVVMTK0045): ver ViewModels.cs (campos con [ObservableProperty], sin AOT/trimming).
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using CaAIA.Application.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace CaAIA.Presentation.ViewModels;

/// <summary>Nodo arrastrable (X/Y notifican para mover también sus aristas).</summary>
public sealed partial class MemoryGraphNodeVm : ObservableObject
{
    public MemoryGraphNode Data { get; }

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    public string Title => Data.Title;
    public MemoryNodeKind Kind => Data.Kind;
    public string Meta => Data.Meta;

    public MemoryGraphNodeVm(MemoryGraphNode data)
    {
        Data = data;
        // El layout da centros; el Canvas posiciona esquinas (nodo de 40px).
        _x = data.X - 20;
        _y = data.Y - 20;
    }

    public double CenterX => X + 20;
    public double CenterY => Y + 20;

    partial void OnXChanged(double value)
    {
        OnPropertyChanged(nameof(CenterX));
    }

    partial void OnYChanged(double value)
    {
        OnPropertyChanged(nameof(CenterY));
    }
}

/// <summary>Arista centro-a-centro (se refresca al arrastrar).</summary>
public sealed partial class MemoryGraphEdgeVm : ObservableObject
{
    public string FromId { get; }
    public string ToId { get; }

    [ObservableProperty]
    private double _x1;

    [ObservableProperty]
    private double _y1;

    [ObservableProperty]
    private double _x2;

    [ObservableProperty]
    private double _y2;

    public MemoryGraphEdgeVm(string fromId, string toId)
    {
        FromId = fromId;
        ToId = toId;
    }
}

/// <summary>Red de memoria: bóveda del proyecto + lecciones globales.</summary>
public sealed partial class MemoriaViewModel : ObservableObject
{
    public ObservableCollection<MemoryGraphNodeVm> Nodes { get; } = new();
    public ObservableCollection<MemoryGraphEdgeVm> Edges { get; } = new();

    private readonly Application.Services.IUserPreferences _prefs;
    private readonly LessonStore _lessons;
    private readonly DispatcherQueue _dispatcher;

    public string WorkspacePath => _prefs.WorkspacePath;

    [ObservableProperty]
    private MemoryGraphNodeVm? _selectedNode;

    [ObservableProperty]
    private string _selectedContent = string.Empty;

    [ObservableProperty]
    private string _statusText = "Cargando red de memoria…";

    [ObservableProperty]
    private bool _isBusy;

    public MemoriaViewModel(
        Application.Services.IUserPreferences prefs,
        LessonStore lessons)
    {
        _prefs = prefs;
        _lessons = lessons;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _prefs.Changed += (_, _) =>
        {
            if (_dispatcher.HasThreadAccess)
            {
                OnPropertyChanged(nameof(WorkspacePath));
            }
            else
            {
                _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(WorkspacePath)));
            }
        };
        _ = RefreshAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var workspace = WorkspacePath;
            var graph = await Task.Run(() =>
            {
                var notes = VaultNotes.ReadAll(VaultNotes.VaultDir(workspace));
                VaultNote? global = null;
                try
                {
                    if (File.Exists(_lessons.GlobalFilePath))
                    {
                        global = new VaultNote("lecciones-globales", _lessons.GlobalFilePath,
                            File.ReadAllText(_lessons.GlobalFilePath), Array.Empty<string>());
                    }
                }
                catch (Exception)
                {
                }

                return MemoryGraphBuilder.Build(notes, global);
            }, ct).ConfigureAwait(false);

            await RunOnUiAsync(() =>
            {
                Nodes.Clear();
                Edges.Clear();
                var byId = new Dictionary<string, MemoryGraphNodeVm>(StringComparer.Ordinal);
                foreach (var node in graph.Nodes)
                {
                    var vm = new MemoryGraphNodeVm(node);
                    Nodes.Add(vm);
                    byId[node.Id] = vm;
                }

                foreach (var edge in graph.Edges)
                {
                    Edges.Add(new MemoryGraphEdgeVm(edge.FromId, edge.ToId));
                }

                foreach (var vm in Nodes)
                {
                    UpdateEdgesFor(vm);
                }

                StatusText = Nodes.Count == 0
                    ? "Sin notas todavía: la IA crea la bóveda (.ca-a-ia) al trabajar."
                    : $"{Nodes.Count} notas · {Edges.Count} enlaces. Arrastra para mover, toca para leer.";
                if (SelectedNode is null || !byId.ContainsKey(SelectedNode.Data.Id))
                {
                    var first = Nodes.FirstOrDefault();
                    if (first is not null)
                    {
                        Select(first);
                    }
                    else
                    {
                        SelectedNode = null;
                        SelectedContent = string.Empty;
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            await RunOnUiAsync(() => { StatusText = $"No se pudo leer la memoria: {message}"; })
                .ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiAsync(() => { IsBusy = false; }).ConfigureAwait(false);
        }
    }

    /// <summary>Selecciona un nodo y carga su contenido para la vista previa.</summary>
    public void Select(MemoryGraphNodeVm node)
    {
        SelectedNode = node;
        SelectedContent = "Cargando…";
        var path = node.Data.FullPath;
        Task.Run(() =>
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                text = $"No se pudo leer: {ex.Message}";
            }

            return text;
        }).ContinueWith(t =>
        {
            if (_dispatcher.HasThreadAccess)
            {
                SelectedContent = t.Result;
            }
            else
            {
                _dispatcher.TryEnqueue(() => { SelectedContent = t.Result; });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Tras arrastrar un nodo, recoloca sus aristas.</summary>
    public void UpdateEdgesFor(MemoryGraphNodeVm node)
    {
        var lookup = Nodes.ToDictionary(n => n.Data.Id, StringComparer.Ordinal);
        foreach (var edge in Edges)
        {
            if (edge.FromId != node.Data.Id && edge.ToId != node.Data.Id)
            {
                continue;
            }

            if (lookup.TryGetValue(edge.FromId, out var from)
                && lookup.TryGetValue(edge.ToId, out var to))
            {
                edge.X1 = from.CenterX;
                edge.Y1 = from.CenterY;
                edge.X2 = to.CenterX;
                edge.Y2 = to.CenterY;
            }
        }
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }))
        {
            done.SetException(new InvalidOperationException("UI dispatcher queue is shutting down."));
        }

        return done.Task;
    }
}
