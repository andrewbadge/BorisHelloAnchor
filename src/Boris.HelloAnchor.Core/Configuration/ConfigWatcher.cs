// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Microsoft.Extensions.Logging;

namespace Boris.HelloAnchor.Core.Configuration;

/// <summary>
/// Holds the current <see cref="HelloAnchorOptions"/> and reloads them when <c>config.json</c> changes
/// (SPEC §4).
/// </summary>
/// <remarks>
/// <para>
/// File-system notifications arrive on thread-pool threads. Each reload builds a new immutable options
/// instance and publishes it with a volatile write, so readers on any thread (including the agent's hook
/// thread) just read <see cref="Current"/> and always get a complete snapshot.
/// </para>
/// <para>
/// Editors often save with several writes, or by writing a temp file and renaming it, so events are
/// debounced: a reload happens once no further event has arrived for the debounce interval.
/// </para>
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    /// <summary>Default quiet period before a burst of change events triggers a reload.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly Timer _debounceTimer;
    private readonly FileSystemWatcher? _watcher;
    private volatile HelloAnchorOptions _current;
    private int _disposed;

    /// <summary>
    /// Loads the configuration once and starts watching for changes.
    /// </summary>
    /// <param name="path">Full path of <c>config.json</c>.</param>
    /// <param name="logger">Receives load warnings and reload notices.</param>
    /// <param name="debounce">Quiet period before reloading; <see langword="null"/> for <see cref="DefaultDebounce"/>.</param>
    public ConfigWatcher(string path, ILogger logger, TimeSpan? debounce = null)
    {
        _path = path;
        _logger = logger;
        _debounce = debounce ?? DefaultDebounce;
        _current = ConfigLoader.Load(path, logger);
        _debounceTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);

        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
        {
            // Typical for a developer running the agent before anything is installed.
            _logger.LogInformation("Configuration folder '{Directory}' does not exist; changes will not be watched.", directory);
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not watch '{Path}' for changes; edits will need a restart.", path);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Raised on a thread-pool thread after a reload has published new options.</summary>
    public event EventHandler<HelloAnchorOptions>? Changed;

    /// <summary>The most recently loaded options. Safe to read from any thread.</summary>
    public HelloAnchorOptions Current => _current;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher?.Dispose();
        _debounceTimer.Dispose();
    }

    /// <summary>Restarts the debounce timer for every file-system event.</summary>
    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The watcher's internal buffer overflowed or the folder became unavailable. Reload anyway (in case a
    /// change was missed) and try to re-arm the watcher.
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "Configuration watcher error; reloading and re-arming.");
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Could not re-arm the configuration watcher.");
        }

        OnFileEvent(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(_path) ?? string.Empty, Path.GetFileName(_path)));
    }

    /// <summary>Loads the file and publishes the result. Runs on a timer (thread-pool) thread.</summary>
    private void Reload()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var options = ConfigLoader.Load(_path, _logger);
            _current = options;
            _logger.LogInformation("Configuration reloaded: {Options}", options.Describe());
            Changed?.Invoke(this, options);
        }
        catch (Exception ex)
        {
            // ConfigLoader never throws for bad input, but a subscriber might. Never let that kill the process.
            _logger.LogError(ex, "Error while applying reloaded configuration.");
        }
    }
}

/// <summary>Formatting helpers for logging options.</summary>
public static class HelloAnchorOptionsExtensions
{
    /// <summary>Returns a single-line, human-readable summary of every setting.</summary>
    /// <param name="options">The options to describe.</param>
    public static string Describe(this HelloAnchorOptions options) =>
        $"TargetDisplay={options.TargetDisplay}" +
        (options.TargetDisplay == TargetDisplayMode.DeviceName ? $" ({options.TargetDeviceName})" : string.Empty) +
        (options.TargetDisplay == TargetDisplayMode.Monitor ? $" ({options.TargetMonitorId})" : string.Empty) +
        $", Processes=[{string.Join(", ", options.TargetProcessNames)}]" +
        $", Classes=[{string.Join(", ", options.TargetWindowClasses)}]" +
        $", VerifyDelaysMs=[{string.Join(", ", options.VerifyDelaysMs)}]" +
        $", SkipRemoteSessions={options.SkipRemoteSessions}" +
        $", AllowSystemTokenFallback={options.AllowSystemTokenFallback}" +
        $", BackoffSeconds=[{string.Join(", ", options.AgentRestartBackoffSeconds)}]" +
        $", LogLevel={options.LogLevel}";
}
