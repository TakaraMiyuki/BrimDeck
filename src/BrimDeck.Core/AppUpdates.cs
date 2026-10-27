using System.Net;
using System.Text.Json;

namespace BrimDeck.Core;

public enum AppUpdateStage { Idle, Checking, Available, Downloading, Ready, Installing }
public sealed record AppRelease(string Version, ReleaseNotes Notes, long Size);

public interface IAppUpdateBackend
{
    string CurrentVersion { get; }
    bool CanInstall { get; }
    AppRelease? PendingRelease { get; }
    Task<AppRelease?> CheckAsync(CancellationToken cancellation);
    Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation);
    void PrepareRestart();
}

// A version check the server refused; RetryAfter carries its Retry-After header when it sent one.
public sealed class UpdateCheckException(HttpStatusCode status, TimeSpan? retryAfter)
    : HttpRequestException($"The update manifest request failed with status {(int)status}.", null, status)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// When automatic checks run. The first comes 90 seconds after startup unless the last successful check is less than
// 6 hours old; then every 6 hours. A failed check is retried after 15 minutes, 1 hour, then every 4 hours, and never
// before the server's Retry-After time.
public static class UpdateSchedule
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromDays(1);

    public static TimeSpan Backoff(int failures) => failures switch
    {
        <= 0 => TimeSpan.Zero, 1 => TimeSpan.FromMinutes(15), 2 => TimeSpan.FromHours(1), _ => TimeSpan.FromHours(4)
    };

    public static DateTimeOffset NextCheck(DateTimeOffset started, DateTimeOffset? lastSuccess, int failures, DateTimeOffset? lastFailure, DateTimeOffset? retryUntil)
    {
        var due = started + StartupDelay;
        if (lastSuccess is { } success && success + Interval > due) due = success + Interval;
        if (failures > 0 && lastFailure is { } failure && failure + Backoff(failures) > due) due = failure + Backoff(failures);
        if (retryUntil is { } retry && retry > due) due = retry;
        return due;
    }
}

// update-state.json in the data folder. It replaced update-prompt.txt, which held only the prompted version.
public sealed class AppUpdateState
{
    public DateTimeOffset? LastSuccess { get; set; }
    public string? AvailableVersion { get; set; }
    public string? PromptedVersion { get; set; }
    public string? SkippedVersion { get; set; }
    public bool AutoCheck { get; set; } = true;
}

// All commands originate on the UI thread. The backend may report progress from a worker thread.
public sealed class AppUpdates
{
    private readonly IAppUpdateBackend _backend;
    private readonly string _stateFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly AppUpdateState _state = new();
    private readonly DateTimeOffset _started;
    private int _failures;
    private DateTimeOffset? _lastFailure, _retryUntil;
    private Task? _check;
    // Background checks fail silently; a check that a person started or joined reports its error.
    private bool _quiet;
    private CancellationTokenSource? _download;
    public event Action? Changed;
    public AppUpdateStage Stage { get; private set; }
    public AppRelease? Release { get; private set; }
    public string CurrentVersion => _backend.CurrentVersion;
    public bool CanInstall => _backend.CanInstall;
    public bool HasUpdate => Release is not null;
    public bool IsBusy => Stage is AppUpdateStage.Checking or AppUpdateStage.Downloading or AppUpdateStage.Installing;
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public DateTimeOffset NextBackgroundCheck => UpdateSchedule.NextCheck(_started, LastChecked, _failures, _lastFailure, _retryUntil);

    public AppUpdates(IAppUpdateBackend backend, string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        _backend = backend; _clock = clock ?? (() => DateTimeOffset.UtcNow); _started = _clock();
        _stateFile = Path.Combine(dataDirectory, "update-state.json");
        LoadState(Path.Combine(dataDirectory, "update-prompt.txt"));
        LastChecked = _state.LastSuccess;
        Release = backend.PendingRelease;
        Stage = Release is null ? AppUpdateStage.Idle : AppUpdateStage.Ready;
    }

    public bool AutoCheck
    {
        get => _state.AutoCheck;
        set { if (_state.AutoCheck == value) return; _state.AutoCheck = value; SaveState(); Notify(); }
    }

    public bool IsSkipped(string? version) => version is not null && version == _state.SkippedVersion;

    // The version the panel marks: newer than this copy and not skipped. The last check's result is remembered across restarts.
    public string? NoticeVersion
    {
        get
        {
            var version = Release?.Version ?? _state.AvailableVersion;
            return version is not null && !IsSkipped(version) && ApplicationVersion.Compare(version, CurrentVersion) > 0 ? version : null;
        }
    }

    public void SkipVersion()
    {
        if (Release is null || IsSkipped(Release.Version)) return;
        _state.SkippedVersion = Release.Version; SaveState(); Notify();
    }

    public Task CheckAsync(bool manual, CancellationToken cancellation = default)
    {
        if (_check is { IsCompleted: false }) { if (manual) _quiet = false; return _check; }
        if (Stage is AppUpdateStage.Downloading or AppUpdateStage.Ready or AppUpdateStage.Installing) return Task.CompletedTask;
        if (!manual)
        {
            if (!AutoCheck || _retryUntil is { } retry && _clock() < retry) return Task.CompletedTask;
            // A remembered release without its details is fetched again even within the gap.
            bool remembered = Release is null && NoticeVersion is not null;
            if (!remembered && LastChecked is { } last && _clock() - last < TimeSpan.FromMinutes(30)) return Task.CompletedTask;
        }
        _quiet = false;
        return _check = CheckCoreAsync(cancellation);
    }

    // Called by the panel's minute clock, after a network change and after waking from sleep.
    // Whether a background check could start now, without asking anything slow to answer. The clock asks this first
    // because the alternative is worse: deciding to report "not for hours yet" by listing every network adapter, which
    // costs tens of milliseconds on a machine with many virtual ones — sixty times an hour, forever. Only when a check
    // really is due is the network worth querying, and CheckInBackgroundAsync applies these guards again itself.
    public bool BackgroundCheckDue => _check is not { IsCompleted: false } && AutoCheck && _clock() >= NextBackgroundCheck
        && Stage is not (AppUpdateStage.Downloading or AppUpdateStage.Ready or AppUpdateStage.Installing);

    public Task CheckInBackgroundAsync(bool networkAvailable, CancellationToken cancellation = default)
    {
        if (_check is { IsCompleted: false }) return _check;
        if (!AutoCheck || !networkAvailable || _clock() < NextBackgroundCheck) return Task.CompletedTask;
        if (Stage is AppUpdateStage.Downloading or AppUpdateStage.Ready or AppUpdateStage.Installing) return Task.CompletedTask;
        _quiet = true;
        return _check = CheckCoreAsync(cancellation);
    }

    private async Task CheckCoreAsync(CancellationToken cancellation)
    {
        Stage = AppUpdateStage.Checking; Error = null; Notify();
        try
        {
            var release = await _backend.CheckAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            Release = release; LastChecked = _clock();
            _failures = 0; _lastFailure = null; _retryUntil = null;
            _state.LastSuccess = LastChecked; _state.AvailableVersion = release?.Version; SaveState();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _failures++; _lastFailure = _clock();
            if (ex is UpdateCheckException { RetryAfter: { } wait } && wait > TimeSpan.Zero)
                _retryUntil = _lastFailure + (wait < UpdateSchedule.MaxRetryAfter ? wait : UpdateSchedule.MaxRetryAfter);
            if (!_quiet) Error = Loc.T("暂时无法检查更新，请检查网络后重试。", "Updates cannot be checked right now. Check the network and try again.");
        }
        finally { Stage = HasUpdate ? AppUpdateStage.Available : AppUpdateStage.Idle; Notify(); }
    }

    public bool ShouldPrompt(bool settingsVisible, bool settingsActive) => settingsVisible && settingsActive &&
        Stage is AppUpdateStage.Available or AppUpdateStage.Ready && Release?.Version != _state.PromptedVersion && !IsSkipped(Release?.Version);

    // Persist when a dialog is actually shown, so cancel/close and subsequent app launches do not repeat it.
    public void MarkPromptShown()
    {
        if (Release is null) return;
        _state.PromptedVersion = Release.Version; SaveState();
    }

    public async Task DownloadAsync(CancellationToken cancellation = default)
    {
        if (IsBusy || Release is null || !CanInstall || Stage == AppUpdateStage.Ready) return;
        var release = Release;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _download = download; Stage = AppUpdateStage.Downloading; Progress = 0; Error = null; Notify();
        try
        {
            await _backend.DownloadAsync(release, value =>
            {
                void Report() { if (Stage != AppUpdateStage.Downloading || _download != download) return; Progress = Math.Clamp(value, 0, 100); Notify(); }
                if (_context is null) Report(); else _context.Post(_ => Report(), null);
            }, download.Token);
            download.Token.ThrowIfCancellationRequested();
            Progress = 100; Stage = AppUpdateStage.Ready;
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested) { Stage = AppUpdateStage.Available; }
        catch (Exception) { Stage = AppUpdateStage.Available; Error = Loc.T("更新包下载或校验失败，请重试。", "The update package could not be downloaded or verified. Try again."); }
        finally { _download = null; Notify(); }
    }

    public void CancelDownload() => _download?.Cancel();

    // The app saves its settings first, then launches the updater, then exits through its normal shutdown path.
    public bool PrepareRestart(Action saveSettings)
    {
        if (Stage != AppUpdateStage.Ready || !CanInstall) return false;
        Stage = AppUpdateStage.Installing; Error = null; Notify();
        try { saveSettings(); _backend.PrepareRestart(); return true; }
        catch (Exception) { Stage = AppUpdateStage.Ready; Error = Loc.T("暂时无法安装更新，请重试。", "The update cannot be installed right now. Try again."); Notify(); return false; }
    }

    // The old update-prompt.txt is read once: its version moves into update-state.json and the file is removed.
    private void LoadState(string legacyFile)
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var saved = JsonSerializer.Deserialize<AppUpdateState>(File.ReadAllText(_stateFile));
                if (saved is not null)
                {
                    _state.LastSuccess = saved.LastSuccess is { } time && time <= _clock() ? time : null;
                    _state.AvailableVersion = saved.AvailableVersion; _state.PromptedVersion = saved.PromptedVersion;
                    _state.SkippedVersion = saved.SkippedVersion; _state.AutoCheck = saved.AutoCheck;
                }
            }
            else if (File.Exists(legacyFile))
            {
                _state.PromptedVersion = File.ReadAllText(legacyFile).Trim() is { Length: > 0 } version ? version : null;
                if (SaveState()) File.Delete(legacyFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private bool SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile + ".tmp", JsonSerializer.Serialize(_state));
            File.Move(_stateFile + ".tmp", _stateFile, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void Notify() => Changed?.Invoke();
}
