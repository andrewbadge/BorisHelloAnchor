// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

namespace Boris.HelloAnchor.Agent;

/// <summary>
/// A tiny single-threaded timer queue (SPEC §7.2). The message loop asks it how long it may sleep, then
/// runs whatever has become due. Everything therefore executes on the hook thread, so no locking is needed
/// anywhere in the agent's window-handling code.
/// </summary>
/// <remarks>Not thread-safe by design: only the hook/message-loop thread may call it.</remarks>
internal sealed class DueQueue
{
    /// <summary><c>INFINITE</c> for <c>MsgWaitForMultipleObjectsEx</c>.</summary>
    public const uint Infinite = 0xFFFFFFFF;

    private readonly PriorityQueue<Action, long> _queue = new();

    /// <summary>Number of pending items.</summary>
    public int Count => _queue.Count;

    /// <summary>Schedules <paramref name="action"/> to run <paramref name="delay"/> from now.</summary>
    /// <param name="delay">Delay before the action is due.</param>
    /// <param name="action">Work to run on the loop thread.</param>
    public void Schedule(TimeSpan delay, Action action) =>
        _queue.Enqueue(action, Environment.TickCount64 + (long)Math.Max(0, delay.TotalMilliseconds));

    /// <summary>Milliseconds until the next item is due, or <see cref="Infinite"/> when the queue is empty.</summary>
    public uint GetWaitTimeoutMs()
    {
        if (!_queue.TryPeek(out _, out var due))
        {
            return Infinite;
        }

        var remaining = due - Environment.TickCount64;
        return remaining <= 0 ? 0u : (uint)Math.Min(remaining, int.MaxValue);
    }

    /// <summary>
    /// Runs every item that is due. Items scheduled while running (e.g. a verify that schedules a
    /// follow-up) are honoured on a later call if not yet due.
    /// </summary>
    /// <param name="onError">Called if an action throws, so one failure doesn't stop the others.</param>
    public void RunDue(Action<Exception> onError)
    {
        var now = Environment.TickCount64;
        while (_queue.TryPeek(out _, out var due) && due <= now)
        {
            var action = _queue.Dequeue();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }
    }
}
