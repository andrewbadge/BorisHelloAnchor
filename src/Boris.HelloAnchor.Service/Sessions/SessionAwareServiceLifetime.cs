// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Boris.HelloAnchor.Service.Sessions;

/// <summary>
/// A <see cref="WindowsServiceLifetime"/> that also receives SCM session-change notifications and forwards
/// them to the <see cref="SessionEventQueue"/> (SPEC §6.2).
/// </summary>
/// <remarks>
/// Registered only when actually running under the SCM (see <c>Program.cs</c>); otherwise
/// <see cref="ServiceBase.Run(ServiceBase)"/> would fail when started from a console or debugger.
/// No work is done on the SCM callback thread: events are posted and handled by the session loop.
/// </remarks>
internal sealed class SessionAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly SessionEventQueue _queue;

    /// <summary>Creates the lifetime and opts in to session-change notifications.</summary>
    /// <param name="environment">Host environment.</param>
    /// <param name="applicationLifetime">Application lifetime.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="optionsAccessor">Host options.</param>
    /// <param name="windowsServiceOptionsAccessor">Windows service options (service name).</param>
    /// <param name="queue">Destination for session events.</param>
    public SessionAwareServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        SessionEventQueue queue)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _queue = queue;

        // Must be set before ServiceBase.Run registers with the SCM.
        CanHandleSessionChangeEvent = true;
    }

    /// <inheritdoc />
    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        SessionEventKind? kind = changeDescription.Reason switch
        {
            SessionChangeReason.SessionLogon => SessionEventKind.Logon,
            SessionChangeReason.ConsoleConnect => SessionEventKind.ConsoleConnect,
            SessionChangeReason.RemoteConnect => SessionEventKind.RemoteConnect,
            SessionChangeReason.SessionLogoff => SessionEventKind.Logoff,
            // Lock/unlock/disconnect need no action: agents keep running in disconnected sessions.
            _ => null,
        };

        if (kind is { } k)
        {
            _queue.Post(new SessionEvent(k, (uint)changeDescription.SessionId));
        }

        base.OnSessionChange(changeDescription);
    }
}
