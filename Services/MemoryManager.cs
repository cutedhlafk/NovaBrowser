using System.Runtime.InteropServices;
using System.Windows.Threading;
using NovaBrowser.Models;
using WpfApplication = System.Windows.Application;

namespace NovaBrowser.Services;

/// <summary>
/// Desired memory target for an initialized WebView. BrowserTabViewModel translates
/// this into WebView2's CoreWebView2.MemoryUsageTargetLevel property.
/// </summary>
public enum MemoryTargetLevel
{
    Normal,
    Low
}

/// <summary>
/// Minimal tab contract used by <see cref="MemoryManager"/>. It deliberately has
/// no dependency on a view or WebView2, which keeps lifetime and lazy-loading
/// decisions in the tab implementation.
/// </summary>
public interface IMemoryManagedTab
{
    bool IsActive { get; }

    DateTimeOffset InactiveSinceUtc { get; }

    void SetMemoryTarget(MemoryTargetLevel level);
}

/// <summary>
/// Applies a non-destructive WebView memory policy. It never suspends a WebView,
/// reloads a page, or clears page state; it only asks inactive views to use a lower
/// memory target after the configured idle period.
/// </summary>
public sealed class MemoryManager : IDisposable
{
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BalancedInactivityThreshold = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ConservativeInactivityThreshold = TimeSpan.FromMinutes(1);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<IMemoryManagedTab> _tabs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IMemoryManagedTab, MemoryTargetLevel> _appliedTargets = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public MemoryManager(Dispatcher? dispatcher = null, TimeProvider? timeProvider = null)
    {
        _dispatcher = dispatcher ?? WpfApplication.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = EvaluationInterval
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    /// <summary>
    /// Balanced is the default: active tabs stay normal and inactive tabs switch to
    /// Low only after three minutes. Performance keeps every registered tab normal;
    /// Conservative uses a one-minute idle period.
    /// </summary>
    public MemorySavingMode Mode { get; private set; } = MemorySavingMode.Balanced;

    public int RegisteredTabCount
    {
        get
        {
            VerifyUsable();
            return _tabs.Count;
        }
    }

    /// <summary>
    /// Adds a tab to the policy and evaluates its current state immediately.
    /// </summary>
    public void Register(IMemoryManagedTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        VerifyUsable();
        RegisterCore(tab);
        EvaluateTab(tab, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Removes a closed tab from the policy. The method does not dispose the tab or
    /// alter its WebView lifetime.
    /// </summary>
    public void Unregister(IMemoryManagedTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        VerifyUsable();
        _tabs.Remove(tab);
        _appliedTargets.Remove(tab);
    }

    /// <summary>
    /// Updates the registered set to match the supplied tabs, then evaluates it.
    /// This is convenient when the tab collection is restored or replaced as a unit.
    /// </summary>
    public void SynchronizeTabs(IEnumerable<IMemoryManagedTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        VerifyUsable();

        var desiredTabs = new HashSet<IMemoryManagedTab>(tabs, ReferenceEqualityComparer.Instance);
        foreach (var existingTab in _tabs.ToArray())
        {
            if (!desiredTabs.Contains(existingTab))
            {
                _tabs.Remove(existingTab);
                _appliedTargets.Remove(existingTab);
            }
        }

        foreach (var tab in desiredTabs)
        {
            RegisterCore(tab);
        }

        EvaluateCore();
    }

    /// <summary>
    /// Must be called when a tab becomes selected. It restores Normal immediately,
    /// rather than waiting for the next 30-second timer tick.
    /// </summary>
    public void NotifyTabActivated(IMemoryManagedTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        VerifyUsable();
        RegisterCore(tab);
        ApplyTarget(tab, MemoryTargetLevel.Normal);
    }

    /// <summary>
    /// Optionally informs the manager that a tab just became inactive. The tab must
    /// already expose its updated InactiveSinceUtc value when calling this method.
    /// </summary>
    public void NotifyTabDeactivated(IMemoryManagedTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        VerifyUsable();
        RegisterCore(tab);
        EvaluateTab(tab, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Changes the policy. Performance restores Normal to all known tabs at once;
    /// other modes are evaluated immediately against each tab's idle timestamp.
    /// </summary>
    public void SetMode(MemorySavingMode mode)
    {
        VerifyUsable();
        ValidateMode(mode);
        Mode = mode;
        EvaluateCore();
    }

    /// <summary>
    /// Runs the policy now. Primarily useful after a tab-state update and in tests;
    /// regular operation is also evaluated every 30 seconds.
    /// </summary>
    public void EvaluateNow()
    {
        VerifyUsable();
        EvaluateCore();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.VerifyAccess();
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _appliedTargets.Clear();
        _tabs.Clear();
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!_disposed)
        {
            EvaluateCore();
        }
    }

    private void RegisterCore(IMemoryManagedTab tab)
    {
        _tabs.Add(tab);
    }

    private void EvaluateCore()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var tab in _tabs.ToArray())
        {
            EvaluateTab(tab, now);
        }
    }

    private void EvaluateTab(IMemoryManagedTab tab, DateTimeOffset now)
    {
        if (tab.IsActive)
        {
            ApplyTarget(tab, MemoryTargetLevel.Normal);
            return;
        }

        var threshold = GetInactivityThreshold(Mode);
        if (threshold is null || tab.InactiveSinceUtc == default || tab.InactiveSinceUtc > now)
        {
            ApplyTarget(tab, MemoryTargetLevel.Normal);
            return;
        }

        var target = now - tab.InactiveSinceUtc >= threshold.Value
            ? MemoryTargetLevel.Low
            : MemoryTargetLevel.Normal;
        ApplyTarget(tab, target);
    }

    private void ApplyTarget(IMemoryManagedTab tab, MemoryTargetLevel target)
    {
        if (_appliedTargets.TryGetValue(tab, out var appliedTarget) && appliedTarget == target)
        {
            return;
        }

        try
        {
            tab.SetMemoryTarget(target);
            _appliedTargets[tab] = target;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or
                                         InvalidOperationException or
                                         NotSupportedException or
                                         NotImplementedException or
                                         COMException)
        {
            // A closing or not-yet-initialized WebView can reject a target update.
            // Leave it untracked so the next evaluation can safely retry.
            _appliedTargets.Remove(tab);
        }
    }

    private void VerifyUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
    }

    private static TimeSpan? GetInactivityThreshold(MemorySavingMode mode) => mode switch
    {
        MemorySavingMode.Balanced => BalancedInactivityThreshold,
        MemorySavingMode.Conservative => ConservativeInactivityThreshold,
        MemorySavingMode.Performance => null,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Nieznany tryb oszczędzania pamięci.")
    };

    private static void ValidateMode(MemorySavingMode mode)
    {
        _ = GetInactivityThreshold(mode);
    }
}
