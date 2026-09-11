// CA-A-IA — Árbol de archivos del workspace: modelo puro (sin UI) + constructor.
// Presentation lo mapea a TreeViewNode en el hilo UI; esto se testea sin dispatcher.

namespace CaAIA.Application.Services;

/// <summary>Un nodo (carpeta o archivo) con lo justo para pintarlo bonito.</summary>
public sealed record FileTreeItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes = -1,
    int DescendantFiles = 0)
{
    /// <summary>Línea secundaria: tamaño en archivos, recuento en carpetas.</summary>
    public string Meta => IsDirectory
        ? (DescendantFiles == 1 ? "1 archivo" : $"{DescendantFiles} archivos")
        : FileTreeBuilder.FormatSize(SizeBytes);
}

/// <summary>Nodo mutable solo durante la construcción (el resultado se congela).</summary>
public sealed class FileTreeNode
{
    public FileTreeItem Item { get; set; }
    public List<FileTreeNode> Children { get; } = new();

    public FileTreeNode(FileTreeItem item) => Item = item;
}

/// <summary>Construye el árbol desde rutas absolutas: anida por carpeta, ordena
/// (carpetas primero, alfabético insensible a mayúsculas) y cuenta descendientes.</summary>
public static class FileTreeBuilder
{
    public static FileTreeNode Build(string workspacePath, IEnumerable<string> files)
    {
        var rootName = Path.GetFileName(
            workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName))
        {
            rootName = workspacePath;
        }

        var root = new FileTreeNode(new FileTreeItem(rootName, workspacePath, IsDirectory: true));
        foreach (var file in files)
        {
            string relative;
            try
            {
                relative = Path.GetRelativePath(workspacePath, file);
            }
            catch (Exception)
            {
                continue;
            }

            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                continue; // fuera del workspace: no pintar
            }

            var parts = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var current = root;
            var currentPath = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                currentPath = Path.Combine(currentPath, parts[i]);
                var folder = current.Children.FirstOrDefault(c =>
                    c.Item.IsDirectory && c.Item.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (folder is null)
                {
                    folder = new FileTreeNode(new FileTreeItem(parts[i], currentPath, IsDirectory: true));
                    current.Children.Add(folder);
                }

                current = folder;
            }

            current.Children.Add(new FileTreeNode(new FileTreeItem(
                parts[^1], file, IsDirectory: false, SizeBytes: TrySize(file))));
        }

        CountAndSort(root);
        return root;
    }

    private static long TrySize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static int CountAndSort(FileTreeNode node)
    {
        var files = 0;
        foreach (var child in node.Children)
        {
            files += child.Item.IsDirectory ? CountAndSort(child) : 1;
        }

        node.Children.Sort(static (a, b) =>
        {
            var dir = b.Item.IsDirectory.CompareTo(a.Item.IsDirectory); // carpetas primero
            return dir != 0 ? dir : string.Compare(a.Item.Name, b.Item.Name, StringComparison.OrdinalIgnoreCase);
        });
        if (node.Item.IsDirectory)
        {
            node.Item = node.Item with { DescendantFiles = files };
        }

        return files;
    }

    public static string FormatSize(long bytes) =>
        bytes < 0 ? "—" :
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / (1024.0 * 1024):F1} MB";
}
