// CA-A-IA — Conversores solo-vista (sin lógica de negocio): pills de estado,
// glifos por extensión y visibilidad de estados vacíos.

using CaAIA.Domain.Enums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace CaAIA.Presentation.Views;

/// <summary>Estado de tarea/plan → pincel semántico del tema (éxito/aviso/error/info/tenue).</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value switch
        {
            AgentTaskStatus.Completed => "AppSuccessBrush",
            AgentTaskStatus.Failed => "AppDangerBrush",
            AgentTaskStatus.InProgress => "AppInfoBrush",
            AgentTaskStatus.Blocked => "AppDangerBrush",
            AgentTaskStatus.NeedsReview => "AppWarningBrush",
            AgentTaskStatus.Skipped => "AppFaintText",
            AgentTaskStatus.Pending => "AppFaintText",
            PlanStatus.Completed => "AppSuccessBrush",
            PlanStatus.Failed or PlanStatus.Cancelled => "AppDangerBrush",
            PlanStatus.Executing or PlanStatus.Auditing => "AppInfoBrush",
            PlanStatus.Approved => "AppSuccessBrush",
            PlanStatus.InReview or PlanStatus.Draft => "AppWarningBrush",
            _ => "AppFaintText",
        };
        try
        {
            if (Microsoft.UI.Xaml.Application.Current.Resources[key] is Brush b)
            {
                return b;
            }
        }
        catch (Exception)
        {
            // Clave ausente: caer al gris neutro.
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Ruta de fichero → glifo Segoe MDL2 Assets (código, documento, carpeta).</summary>
public sealed class FileGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        GlyphForPath((value as string) ?? string.Empty);

    internal static string GlyphForPath(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" or ".ps1" or ".csproj" or ".slnx" or ".sln" or ".props" or ".targets"
                or ".py" or ".js" or ".ts" or ".tsx" or ".jsx" or ".java" or ".go" or ".rs"
                or ".cpp" or ".h" or ".hpp" or ".c" or ".sql" or ".sh" or ".css" or ".scss"
                or ".html" => "\uE943",
            _ => "\uE8A5",
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Nodo del árbol → glifo (carpeta ámbar / fichero por extensión).</summary>
public sealed class FileTreeItemGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not Application.Services.FileTreeItem item)
        {
            return "\uE8A5";
        }

        return item.IsDirectory ? "\uE8B7" : FileGlyphConverter.GlyphForPath(item.FullPath);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Nodo del árbol → color (carpeta marca / fichero info).</summary>
public sealed class FileTreeItemBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Application.Services.FileTreeItem item && item.IsDirectory
            ? "AppBrandBrush" : "AppInfoBrush";
        try
        {
            var resources = Microsoft.UI.Xaml.Application.Current?.Resources;
            if (resources is not null && resources[key] is Brush b)
            {
                return b;
            }
        }
        catch (Exception)
        {
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Clase de nodo de memoria → color (lección ámbar, global azul, nota tenue).</summary>
public sealed class MemoryNodeKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Application.Services.MemoryNodeKind kind
            ? kind switch
            {
                Application.Services.MemoryNodeKind.Lesson => "AppBrandBrush",
                Application.Services.MemoryNodeKind.Global => "AppInfoBrush",
                _ => "AppMutedText",
            }
            : "AppMutedText";
        try
        {
            var resources = Microsoft.UI.Xaml.Application.Current?.Resources;
            if (resources is not null && resources[key] is Brush b)
            {
                return b;
            }
        }
        catch (Exception)
        {
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visible/Collapsed (parámetro "invert" invierte).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var show = value is true;
        if ((parameter as string) == "invert")
        {
            show = !show;
        }

        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Carpeta expandida → ⌄, plegada → ›.</summary>
public sealed class ExpandGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "\uE70D" : "\uE76C";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Recuento == 0 → Visible (paneles de estado vacío). Con parámetro "invert" invierte.</summary>
public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isZero = value switch
        {
            int n => n == 0,
            null => true,
            System.Collections.ICollection c => c.Count == 0,
            _ => false,
        };
        if ((parameter as string) == "invert")
        {
            isZero = !isZero;
        }

        return isZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Recuento &gt; 0 → Visible (contadores, insignias). Con parámetro "invert" invierte.</summary>
public sealed class NonZeroCountToVisibilityConverter : IValueConverter
{
    private static readonly ZeroCountToVisibilityConverter Inner = new();
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Inner.Convert(value, targetType, "invert", language);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
