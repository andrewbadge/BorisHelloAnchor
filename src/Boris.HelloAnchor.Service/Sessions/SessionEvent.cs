// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Threading.Channels;

namespace Boris.HelloAnchor.Service.Sessions;

/// <summary>Kinds of work item processed by the <see cref="SessionManager"/> loop.</summary>
internal enum SessionEventKind
{
    /// <summary>A user logged on to the session (SCM <c>SessionLogon</c>).</summary>
    Logon,

    /// <summary>The session was connected to the physical console (SCM <c>ConsoleConnect</c>).</summary>
    ConsoleConnect,

    /// <summary>The session was connected from a remote client (SCM <c>RemoteConnect</c>).</summary>
    RemoteConnect,

    /// <summary>The user logged off (SCM <c>SessionLogoff</c>).</summary>
    Logoff,

    /// <summary>Periodic (and start-up) reconciliation against <c>WTSEnumerateSessions</c>.</summary>
    Reconcile,

    /// <summary>An agent process exited; <see cref="SessionEvent.ExitCode"/> and <see cref="SessionEvent.Generation"/> are set.</summary>
    AgentExited,

    /// <summary>A restart backoff elapsed; <see cref="SessionEvent.Generation"/> identifies the restart request.</summary>
    RestartDue,
}

/// <summary>
/// A unit of work for the session loop. Every change to agent bookkeeping goes through one of these, so
/// the bookkeeping is only ever touched by a single thread (SPEC §6.2).
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="SessionId">Session concerned; ignored for <see cref="SessionEventKind.Reconcile"/>.</param>
/// <param name="Generation">Identifies which agent launch / restart request an event belongs to, so stale events are ignored.</param>
/// <param name="ExitCode">Process exit code for <see cref="SessionEventKind.AgentExited"/>.</param>
internal sealed record SessionEvent(SessionEventKind Kind, uint SessionId = 0, long Generation = 0, uint ExitCode = 0);

/// <summary>
/// Unbounded single-reader queue that carries <see cref="SessionEvent"/>s from SCM callbacks, timers and
/// process-exit waits to the <see cref="SessionManager"/> loop.
/// </summary>
internal sealed class SessionEventQueue
{
    private readonly Channel<SessionEvent> _channel = Channel.CreateUnbounded<SessionEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>The consuming side, read only by <see cref="SessionManager"/>.</summary>
    public ChannelReader<SessionEvent> Reader => _channel.Reader;

    /// <summary>Enqueues an event. Never blocks; safe from any thread, including SCM callbacks.</summary>
    /// <param name="sessionEvent">The event to enqueue.</param>
    public void Post(SessionEvent sessionEvent) => _channel.Writer.TryWrite(sessionEvent);
}
