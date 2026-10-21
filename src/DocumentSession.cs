using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HP3052Scanner;
internal sealed record PageRecord(string Id, string Name, string File, int Width, int Height, float DpiX, float DpiY, string Origin = "", string Mode = "Color");
internal sealed class SessionManifest
{
    public int Version { get; set; } = 1;
    public List<PageRecord> Pages { get; set; } = new();
    public int NextPage { get; set; } = 1;
    public List<string> ImportedOrigins { get; set; } = new();
    public List<string> IgnoredBatches { get; set; } = new();
    public string SavedSignature { get; set; } = "";
}

internal sealed class DocumentSession
{
    readonly string root;
    readonly Stack<SessionManifest> undo = new();
    readonly Stack<SessionManifest> redo = new();
    SessionManifest state;
    public IReadOnlyList<PageRecord> Pages => state.Pages;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public void ClearHistory() { undo.Clear(); redo.Clear(); }
    public bool Dirty => state.Pages.Count > 0 && Signature() != state.SavedSignature;
    public string Root => root;

    public DocumentSession(string directory)
    {
        root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        string manifest = Path.Combine(root, "session.json");
        state = File.Exists(manifest) ? JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifest)) ?? throw new IOException("Пустой файл проекта.") : new();
        if (state.Version != 1 || state.Pages == null || state.NextPage < 1)
            throw new IOException("Формат проекта не поддерживается.");
        state.ImportedOrigins ??= new();
        state.IgnoredBatches ??= new();
        foreach (var p in state.Pages)
        {
            if (p == null || p.Width < 1 || p.Height < 1 || !float.IsFinite(p.DpiX) || !float.IsFinite(p.DpiY) || p.DpiX < 1 || p.DpiY < 1 || p.File != Path.GetFileName(p.File) || !System.Text.RegularExpressions.Regex.IsMatch(p.File, @"^[a-f0-9]{32}\.png$"))
                throw new IOException("Повреждены сведения о странице проекта.");
            if (!File.Exists(ImagePath(p)))
                throw new IOException("Не найден файл страницы проекта: " + p.Name);
        }

        if (state.Pages.Select(p => p.Id).Distinct().Count() != state.Pages.Count)
            throw new IOException("Повторяются идентификаторы страниц проекта.");
    }

    public bool WasImported(string origin) => state.ImportedOrigins.Contains(origin) || state.Pages.Any(p => p.Origin == origin);
    public bool IsBatchIgnored(string batch) => state.IgnoredBatches.Contains(batch);
    public string NewImagePath() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".png");
    public string ImagePath(PageRecord page) => Path.Combine(root, page.File);
    string Signature() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.Pages))));
    SessionManifest Snapshot() => new()
    {
        Version = 1,
        Pages = state.Pages.ToList(),
        NextPage = state.NextPage,
        SavedSignature = state.SavedSignature,
        ImportedOrigins = state.ImportedOrigins.ToList(),
        IgnoredBatches = state.IgnoredBatches.ToList()
    };
    void Commit(Action change)
    {
        var before = Snapshot();
        try
        {
            change();
            AppFiles.AtomicJson(Path.Combine(root, "session.json"), state);
            undo.Push(before);
            redo.Clear();
        }
        catch
        {
            state = before;
            throw;
        }
    }

    public void Add(string image, int width, int height, float dx, float dy, string origin = "", string mode = "Color")
    {
        if (Path.GetDirectoryName(Path.GetFullPath(image)) != root || !File.Exists(image))
            throw new IOException("Файл страницы должен находиться в проекте.");
        if (width < 1 || height < 1 || !float.IsFinite(dx) || !float.IsFinite(dy) || dx < 1 || dy < 1)
            throw new IOException("Некорректные размеры или разрешение страницы.");
        if (origin.Length > 0 && WasImported(origin))
            return;
        var before = Snapshot();
        try
        {
            state.Pages.Add(new(Guid.NewGuid().ToString("N"), $"Страница_{state.NextPage++:000}", Path.GetFileName(image), width, height, dx, dy, origin, mode));
            if (origin.Length > 0)
                state.ImportedOrigins.Add(origin);
            AppFiles.AtomicJson(Path.Combine(root, "session.json"), state);
        }
        catch { state = before; throw; }
        // Acquired originals are not edits. Retain them when undoing earlier edits.
        var acquired = state.Pages[^1];
        foreach (var entry in undo.Concat(redo))
        {
            entry.Pages.Add(acquired);
            entry.NextPage = state.NextPage;
        }
    }

    public void Rename(int index, string name)
    {
        if (state.Pages[index].Name != name) Commit(() => state.Pages[index] = state.Pages[index] with { Name = name });
    }
    public void Remove(IEnumerable<int> indexes)
    {
        var ids = indexes.Distinct().OrderByDescending(x => x).ToArray();
        if (ids.Length == 0) return;
        Commit(() =>
        {
            foreach (int i in ids)
                state.Pages.RemoveAt(i);
        });
    }

    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= state.Pages.Count || to >= state.Pages.Count)
            return;
        Commit(() =>
        {
            var p = state.Pages[from];
            state.Pages.RemoveAt(from);
            state.Pages.Insert(to, p);
        });
    }

    public void MoveMany(IEnumerable<int> indexes, int target)
    {
        var ids = indexes.Distinct().OrderBy(x => x).ToArray();
        if (ids.Length == 0 || ids.Any(i => i < 0 || i >= state.Pages.Count) || target < 0 || target > state.Pages.Count) return;
        var moved = ids.Select(i => state.Pages[i]).ToArray();
        int insert = target - ids.Count(i => i < target);
        var rest = state.Pages.Where((_, i) => !ids.Contains(i)).ToList();
        rest.InsertRange(insert, moved);
        if (rest.SequenceEqual(state.Pages)) return;
        Commit(() => state.Pages = rest);
    }

    public void Replace(int index, string image, int width, int height, float dx, float dy, string? mode = null)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(image)) != root || !File.Exists(image))
            throw new IOException("Не найден результат редактирования.");
        Commit(() => state.Pages[index] = state.Pages[index] with { File = Path.GetFileName(image), Width = width, Height = height, DpiX = dx, DpiY = dy, Mode = mode ?? state.Pages[index].Mode });
    }

    public void Undo() => Navigate(undo, redo);
    public void Redo() => Navigate(redo, undo);
    void Navigate(Stack<SessionManifest> from, Stack<SessionManifest> to)
    {
        if (from.Count == 0)
            return;
        var previous = from.Peek();
        var current = state;
        // Export status belongs to the saved document, rather than to the history entry.
        previous.SavedSignature = current.SavedSignature;
        previous.ImportedOrigins = current.ImportedOrigins.ToList();
        try
        {
            AppFiles.AtomicJson(Path.Combine(root, "session.json"), previous);
            state = previous;
            from.Pop();
            to.Push(current);
        }
        catch
        {
            state = current;
            throw;
        }
    }

    public void MarkSaved()
    {
        string prior = state.SavedSignature;
        state.SavedSignature = Signature();
        try
        {
            AppFiles.AtomicJson(Path.Combine(root, "session.json"), state);
        }
        catch
        {
            state.SavedSignature = prior;
            throw;
        }
    }

    public void Clear()
    {
        var batches = Directory.GetDirectories(root, "batch_*");
        var empty = new SessionManifest
        {
            IgnoredBatches = state.IgnoredBatches.Concat(batches.Select(p => Path.GetFileName(p))).Distinct().ToList()
        };
        AppFiles.AtomicJson(Path.Combine(root, "session.json"), empty);
        state = empty;
        undo.Clear();
        redo.Clear();
        foreach (string f in Directory.GetFiles(root, "*.png"))
        {
            try
            {
                File.Delete(f);
            }
            catch
            {
            }
        }

        foreach (string dir in batches)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception e)
            {
                AppFiles.Log("Discarded batch cleanup: " + e.Message);
            }
        }
    }

    // Only discard revisions unreachable from the document, Undo, Redo or preview.
    public void PruneImages(string? preview = null)
    {
        var keep = state.Pages.Concat(undo.Concat(redo).SelectMany(s => s.Pages)).Select(p => p.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (preview != null) keep.Add(Path.GetFileName(preview));
        foreach (string path in Directory.GetFiles(root, "*.png"))
            if (!keep.Contains(Path.GetFileName(path)))
                try { File.Delete(path); } catch (Exception e) { AppFiles.Log("Revision cleanup: " + e.Message); }
    }
}
