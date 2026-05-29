namespace SharpLensMcp;

// Filesystem watcher that automatically syncs documents when files change on disk.
// Enabled via SHARPLENS_WATCH_MODE=true. Eliminates the need for agents to
// manually call sync_documents after external file edits.
public partial class RoslynService
{
    private FileSystemWatcher? _fileWatcher;
    private System.Timers.Timer? _debounceTimer;
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watchLock = new();

    private readonly bool _watchEnabled = IsWatchEnabled();
    private readonly int _watchDebounceMs = GetWatchDebounceMs();
    private readonly string _watchExtensions = GetWatchExtensions();

    private static bool IsWatchEnabled() =>
        Environment.GetEnvironmentVariable("SHARPLENS_WATCH_MODE")?.ToLower() == "true";

    private static int GetWatchDebounceMs() =>
        int.TryParse(Environment.GetEnvironmentVariable("SHARPLENS_WATCH_DEBOUNCE_MS"), out var ms) ? ms : 300;

    private static string GetWatchExtensions() =>
        Environment.GetEnvironmentVariable("SHARPLENS_WATCH_EXTENSIONS") ?? ".cs,.razor,.cshtml";

    internal void StartFileWatcher()
    {
        if (!_watchEnabled || _solution?.FilePath == null)
            return;

        var solutionDir = Path.GetDirectoryName(_solution.FilePath);
        if (solutionDir == null) return;

        StopFileWatcher();

        _fileWatcher = new FileSystemWatcher(solutionDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.FileName
                         | NotifyFilters.CreationTime,
        };

        foreach (var ext in _watchExtensions.Split(',').Select(e => e.Trim()))
            _fileWatcher.Filters.Add($"*{ext}");

        _debounceTimer = new System.Timers.Timer(_watchDebounceMs) { AutoReset = false };
        _debounceTimer.Elapsed += OnWatcherDebounce;

        _fileWatcher.Changed += OnFileChanged;
        _fileWatcher.Created += OnFileChanged;
        _fileWatcher.Deleted += OnFileChanged;
        _fileWatcher.Renamed += OnFileRenamed;
        _fileWatcher.Error += OnWatcherError;
        _fileWatcher.EnableRaisingEvents = true;

        Console.Error.WriteLine(
            $"[Watcher] Monitoring {solutionDir} ({_watchExtensions}) — debounce {_watchDebounceMs}ms");
    }

    internal void StopFileWatcher()
    {
        _fileWatcher?.Dispose();
        _debounceTimer?.Dispose();
        _fileWatcher = null;
        _debounceTimer = null;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsWatchedExtension(e.FullPath)) return;
        lock (_watchLock)
        {
            _pendingPaths.Add(e.FullPath);
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        lock (_watchLock)
        {
            if (IsWatchedExtension(e.OldFullPath)) _pendingPaths.Add(e.OldFullPath);
            if (IsWatchedExtension(e.FullPath)) _pendingPaths.Add(e.FullPath);
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        Console.Error.WriteLine($"[Watcher] Error: {ex.Message}");

        if (ex is InternalBufferOverflowException)
        {
            Console.Error.WriteLine("[Watcher] Buffer overflow — restarting watcher");
            try { StartFileWatcher(); } catch { }
        }
    }

    private async void OnWatcherDebounce(object? sender, System.Timers.ElapsedEventArgs e)
    {
        List<string> paths;
        lock (_watchLock)
        {
            paths = _pendingPaths.ToList();
            _pendingPaths.Clear();
        }

        if (paths.Count == 0) return;

        try
        {
            await SyncDocumentsAsync(paths);
            Console.Error.WriteLine($"[Watcher] Synced {paths.Count} file(s)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Watcher] Sync failed: {ex.Message}");
        }
    }

    private static bool IsWatchedExtension(string? path)
    {
        if (path == null) return false;
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }
}
