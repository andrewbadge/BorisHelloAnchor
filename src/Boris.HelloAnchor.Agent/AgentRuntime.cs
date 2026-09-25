// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.ComponentModel;
using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Boris.HelloAnchor.Agent;

/// <summary>
/// The agent's hook thread: installs the WinEvent hook, pumps messages, runs the verify timers and
/// watches the service's shutdown event (SPEC §7.2).
/// </summary>
/// <remarks>
/// An out-of-context WinEvent hook delivers its callbacks through the message queue of the thread that
/// installed it, so the hook, the message loop and all prompt handling live on this one thread.
/// </remarks>
internal sealed class AgentRuntime
{
    /// <summary>
    /// The hook callback delegate. Held in a static field so the GC can never collect it while the native
    /// hook still points at it (SPEC §7.2).
    /// </summary>
    private static WINEVENTPROC? s_hookProc;

    /// <summary>The instance the static callback forwards to.</summary>
    private static AgentRuntime? s_instance;

    private readonly ConfigWatcher _config;
    private readonly ILogger _logger;
    private readonly WindowFilter _filter = new();
    private readonly DueQueue _queue = new();
    private readonly PromptAnchor _anchor;

    /// <summary>Creates the runtime.</summary>
    /// <param name="config">Live configuration.</param>
    /// <param name="loggerFactory">Factory for component loggers.</param>
    public AgentRuntime(ConfigWatcher config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<AgentRuntime>();
        _anchor = new PromptAnchor(
            () => _config.Current,
            new DisplayResolver(loggerFactory.CreateLogger<DisplayResolver>()),
            _queue,
            loggerFactory.CreateLogger<PromptAnchor>());
    }

    /// <summary>
    /// Runs until the shutdown event is signalled or <c>WM_QUIT</c> is received.
    /// </summary>
    /// <param name="shutdownEvent">The service's shutdown event, or <see langword="null"/> when running without the service.</param>
    /// <returns><see cref="AgentExitCodes.ShutdownRequested"/>.</returns>
    public int Run(WaitHandle? shutdownEvent)
    {
        s_instance = this;
        s_hookProc = OnWinEvent;

        // Out-of-context, all processes/threads, and skip our own windows.
        using var hook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_SHOW,
            PInvoke.EVENT_OBJECT_SHOW,
            null,
            s_hookProc,
            0,
            0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

        if (hook.IsInvalid)
        {
            // SetWinEventHook does not set a last-error code, so there is nothing more specific to report.
            throw new InvalidOperationException("SetWinEventHook failed.");
        }

        _logger.LogInformation("WinEvent hook installed; waiting for credential prompts.");

        try
        {
            return PumpMessages(shutdownEvent);
        }
        finally
        {
            // The using declaration unhooks (UnhookWinEvent) when this method returns.
            _logger.LogInformation("Message loop exited; removing hook.");
        }
    }

    /// <summary>
    /// The message loop (SPEC §7.2 step 3). Sleeps in <c>MsgWaitForMultipleObjectsEx</c> until a message
    /// arrives, the shutdown event is set, or the next verify timer is due.
    /// </summary>
    private unsafe int PumpMessages(WaitHandle? shutdownEvent)
    {
        var addedRef = false;
        var safeHandle = shutdownEvent?.SafeWaitHandle;
        try
        {
            // Pin the event handle for the lifetime of the loop.
            safeHandle?.DangerousAddRef(ref addedRef);
            ReadOnlySpan<HANDLE> handles = addedRef
                ? [(HANDLE)safeHandle!.DangerousGetHandle()]
                : [];

            while (true)
            {
                var wait = PInvoke.MsgWaitForMultipleObjectsEx(
                    handles,
                    _queue.GetWaitTimeoutMs(),
                    QUEUE_STATUS_FLAGS.QS_ALLINPUT,
                    MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);

                if (wait == WAIT_EVENT.WAIT_FAILED)
                {
                    throw new Win32Exception("MsgWaitForMultipleObjectsEx failed.");
                }

                // Index 0 is the shutdown event when present; index == handles.Length means "input available".
                if (handles.Length == 1 && wait == WAIT_EVENT.WAIT_OBJECT_0)
                {
                    _logger.LogInformation("Shutdown event signalled by the service.");
                    return AgentExitCodes.ShutdownRequested;
                }

                // Drain the queue. WinEvent callbacks are dispatched from inside PeekMessage.
                while (PInvoke.PeekMessage(out var msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
                {
                    if (msg.message == PInvoke.WM_QUIT)
                    {
                        _logger.LogInformation("WM_QUIT received.");
                        return AgentExitCodes.ShutdownRequested;
                    }

                    PInvoke.TranslateMessage(in msg);
                    PInvoke.DispatchMessage(in msg);
                }

                _queue.RunDue(ex => _logger.LogError(ex, "Verify check failed."));
            }
        }
        finally
        {
            if (addedRef)
            {
                safeHandle!.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Static WinEvent callback. Must never throw: an exception crossing the native boundary would crash
    /// the process, so everything is caught and logged.
    /// </summary>
    private static void OnWinEvent(HWINEVENTHOOK hook, uint eventType, HWND hwnd, int idObject, int idChild, uint idEventThread, uint eventTime)
    {
        var instance = s_instance;
        if (instance is null)
        {
            return;
        }

        try
        {
            var options = instance._config.Current;
            if (instance._filter.IsTarget(hwnd, idObject, idChild, options))
            {
                instance._logger.LogDebug("Credential prompt shown: {Hwnd}.", hwnd.Format());
                instance._anchor.OnPromptShown(hwnd);
            }
        }
        catch (Exception ex)
        {
            instance._logger.LogError(ex, "Error handling window-show event for {Hwnd}.", hwnd.Format());
        }
    }
}
