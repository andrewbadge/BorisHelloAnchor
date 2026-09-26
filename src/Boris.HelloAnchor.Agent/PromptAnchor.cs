// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Runtime.InteropServices;
using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Boris.HelloAnchor.Agent;

/// <summary>
/// Moves a credential prompt onto the target display and keeps it there for the verify window
/// (SPEC §7.5). All members run on the hook thread.
/// </summary>
internal sealed class PromptAnchor(
    Func<HelloAnchorOptions> getOptions,
    DisplayResolver displays,
    DueQueue queue,
    ILogger logger)
{
    /// <summary>Win32 <c>ERROR_ACCESS_DENIED</c>: what UIPI returns when the agent isn't elevated.</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>Prompts currently being tracked, keyed by window handle.</summary>
    private readonly Dictionary<nint, PromptState> _active = [];

    /// <summary>Number of prompts currently being tracked (for diagnostics).</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// Handles a prompt window that was just shown or uncloaked: moves it if necessary and schedules the
    /// verify checks.
    /// </summary>
    /// <param name="hwnd">The prompt window (already filtered by <see cref="WindowFilter"/>).</param>
    public void OnPromptShown(HWND hwnd)
    {
        var options = getOptions();

        // The same prompt normally arrives twice: once when shown (still cloaked) and again when uncloaked,
        // right after the broker has re-centred it on the primary monitor. It can also be hidden and re-shown.
        // Each arrival starts a fresh tracking cycle, but keeps the move count (so the summary line stays
        // meaningful) and whether we have moved it (so a later DPI resize is still re-centred).
        var previousMoves = 0;
        var previouslyMoved = false;
        if (_active.Remove(hwnd, out var previous))
        {
            previous.Cancelled = true;
            previousMoves = previous.Moves;
            previouslyMoved = previous.AgentMoved;
        }

        var target = displays.Resolve(options);
        if (target is null)
        {
            logger.LogDebug("Prompt {Hwnd} shown but neither the target display ({Mode}) nor the internal display is available; leaving it where Windows put it.", hwnd.Format(), options.TargetDisplay);
            return;
        }

        if (!TryGetRect(hwnd, out var rect))
        {
            return;
        }

        var state = new PromptState(hwnd, target.DeviceName) { Moves = previousMoves, AgentMoved = previouslyMoved };
        _active[hwnd] = state;

        if (WindowPlacement.IsCentreInside(rect, target.WorkArea))
        {
            if (state.AgentMoved && !WindowPlacement.IsCentred(rect, target.WorkArea))
            {
                // Ours from an earlier arrival, still on target but resized (typically: sized to the real
                // dialog when uncloaked). Re-centre it at its new size.
                Move(state, rect, target);
            }
            else
            {
                // Already where it should be: either Windows put it on the right display, or we did and it
                // stayed. Leave it alone but keep watching in case the broker repositions it.
                state.Outcome = state.AgentMoved ? "centred" : "already on target";
                logger.LogDebug("Prompt {Hwnd} already on {Device}; watching.", hwnd.Format(), target.DeviceName);
            }
        }
        else
        {
            state.AgentMoved = true;
            Move(state, rect, target);
        }

        ScheduleVerifies(state, options.VerifyDelaysMs);
    }

    /// <summary>Schedules one verify check per configured delay (delays are measured from now).</summary>
    private void ScheduleVerifies(PromptState state, IReadOnlyList<int> delaysMs)
    {
        state.RemainingChecks = delaysMs.Count;
        if (delaysMs.Count == 0)
        {
            Complete(state);
            return;
        }

        foreach (var delay in delaysMs)
        {
            queue.Schedule(TimeSpan.FromMilliseconds(delay), () => Verify(state));
        }
    }

    /// <summary>One verify check (SPEC §7.5 step 4).</summary>
    private void Verify(PromptState state)
    {
        if (state.Cancelled)
        {
            return;
        }

        state.RemainingChecks--;

        // The user authenticated (or cancelled) and the window is gone.
        if (!PInvoke.IsWindow(state.Hwnd))
        {
            state.Outcome = state.AgentMoved ? "moved; closed" : "closed";
            Complete(state);
            return;
        }

        var target = displays.Resolve(getOptions());
        if (target is not null && TryGetRect(state.Hwnd, out var rect))
        {
            if (!WindowPlacement.IsCentreInside(rect, target.WorkArea))
            {
                // It snapped back (or was never moved and has now wandered off). Move it again.
                state.AgentMoved = true;
                Move(state, rect, target);
            }
            else if (state.AgentMoved && !WindowPlacement.IsCentred(rect, target.WorkArea))
            {
                // On target, but resized by WM_DPICHANGED after crossing to a monitor with different scaling.
                Move(state, rect, target);
            }
        }

        if (state.RemainingChecks <= 0)
        {
            Complete(state);
        }
    }

    /// <summary>Centres the window on the target's work area. Never activates or re-orders it.</summary>
    private void Move(PromptState state, PixelRect rect, TargetDisplay target)
    {
        var (x, y) = WindowPlacement.CentreIn(target.WorkArea, rect.Width, rect.Height);
        state.Moves++;
        state.DeviceName = target.DeviceName;

        const SET_WINDOW_POS_FLAGS flags =
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;

        if (PInvoke.SetWindowPos(state.Hwnd, default, x, y, 0, 0, flags))
        {
            state.Outcome = "centred";
            logger.LogDebug("Moved prompt {Hwnd} from {From} to ({X},{Y}) on {Device}.", state.Hwnd.Format(), rect, x, y, target.DeviceName);
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        state.Outcome = $"move failed (Win32 error {error})";
        if (error == ErrorAccessDenied)
        {
            logger.LogError(
                "SetWindowPos on prompt {Hwnd} failed with error 5 (access denied). The agent is most likely not elevated; UIPI blocks moving the prompt from a medium-integrity process.",
                state.Hwnd.Format());
        }
        else
        {
            logger.LogWarning("SetWindowPos on prompt {Hwnd} failed with Win32 error {Error}.", state.Hwnd.Format(), error);
        }
    }

    /// <summary>Stops tracking a prompt and writes the one-line summary (SPEC §7.5 step 5).</summary>
    private void Complete(PromptState state)
    {
        if (state.Completed)
        {
            return;
        }

        state.Completed = true;
        if (_active.TryGetValue(state.Hwnd, out var current) && ReferenceEquals(current, state))
        {
            _active.Remove(state.Hwnd);
        }

        logger.LogInformation(
            "Prompt {Hwnd} handled: display {Device}, moves {Moves}, outcome: {Outcome}.",
            state.Hwnd.Format(),
            state.DeviceName,
            state.Moves,
            state.Outcome);
    }

    /// <summary>Reads the window rectangle; logs at Debug and returns false if the window has gone.</summary>
    private bool TryGetRect(HWND hwnd, out PixelRect rect)
    {
        if (PInvoke.GetWindowRect(hwnd, out var native))
        {
            rect = native.ToPixelRect();
            return true;
        }

        logger.LogDebug("GetWindowRect({Hwnd}) failed: {Error}.", hwnd.Format(), Marshal.GetLastPInvokeError());
        rect = default;
        return false;
    }

    /// <summary>Mutable tracking state for one prompt.</summary>
    private sealed class PromptState(HWND hwnd, string deviceName)
    {
        /// <summary>The prompt window.</summary>
        public HWND Hwnd { get; } = hwnd;

        /// <summary>Device name of the display the prompt was (last) targeted at.</summary>
        public string DeviceName { get; set; } = deviceName;

        /// <summary>How many times <c>SetWindowPos</c> was called for this prompt.</summary>
        public int Moves { get; set; }

        /// <summary>True once the agent has moved the prompt; enables the "still centred?" check.</summary>
        public bool AgentMoved { get; set; }

        /// <summary>Verify checks not yet run.</summary>
        public int RemainingChecks { get; set; }

        /// <summary>Latest outcome text for the summary line.</summary>
        public string Outcome { get; set; } = "pending";

        /// <summary>Set when the prompt was re-shown and a newer state replaced this one.</summary>
        public bool Cancelled { get; set; }

        /// <summary>Set once the summary has been written.</summary>
        public bool Completed { get; set; }
    }
}
