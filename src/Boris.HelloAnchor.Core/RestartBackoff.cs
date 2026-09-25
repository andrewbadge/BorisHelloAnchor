// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

namespace Boris.HelloAnchor.Core;

/// <summary>
/// Restart-delay arithmetic for crashed agents (SPEC §4, <c>AgentRestartBackoffSeconds</c>).
/// </summary>
/// <remarks>
/// The service keeps a per-session restart counter. Each unexpected exit uses the delay at the counter's
/// index (the last value repeats), then increments the counter. An agent that stayed up for at least
/// <see cref="ResetAfter"/> is considered healthy, so its counter restarts from zero.
/// </remarks>
public static class RestartBackoff
{
    /// <summary>Uptime after which a crash is treated as the first in a new sequence.</summary>
    public static readonly TimeSpan ResetAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns the restart count to use for this crash, resetting it if the agent ran long enough.
    /// </summary>
    /// <param name="previousCount">Number of restarts already performed in the current sequence.</param>
    /// <param name="uptime">How long the agent that just exited had been running.</param>
    public static int EffectiveCount(int previousCount, TimeSpan uptime) =>
        uptime >= ResetAfter ? 0 : Math.Max(0, previousCount);

    /// <summary>
    /// Returns the delay before restart number <paramref name="restartCount"/> (zero-based).
    /// </summary>
    /// <param name="restartCount">Zero-based index of this restart within the current sequence.</param>
    /// <param name="backoffSeconds">Configured delays in seconds; must contain at least one value.</param>
    public static TimeSpan GetDelay(int restartCount, IReadOnlyList<int> backoffSeconds)
    {
        ArgumentNullException.ThrowIfNull(backoffSeconds);
        if (backoffSeconds.Count == 0)
        {
            throw new ArgumentException("At least one backoff value is required.", nameof(backoffSeconds));
        }

        // Clamp to the last entry so the final value repeats indefinitely.
        var index = Math.Clamp(restartCount, 0, backoffSeconds.Count - 1);
        return TimeSpan.FromSeconds(backoffSeconds[index]);
    }
}
