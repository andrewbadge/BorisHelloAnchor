// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.Security.AccessControl;
using System.Security.Principal;
using Boris.HelloAnchor.Core;

namespace Boris.HelloAnchor.Service;

/// <summary>
/// Owns the <c>Global\Boris.HelloAnchor.Shutdown</c> event that tells agents to exit (SPEC §6.5).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Manual-reset, so every agent sees it once set.</item>
/// <item>ACL: SYSTEM and Administrators full control; Authenticated Users <c>SYNCHRONIZE</c> only, so standard
/// processes can wait on it but cannot set it and make every agent quit.</item>
/// <item>Reset right after creation: if an old handle kept a signalled instance alive, the "create" actually
/// opened it, and without the reset every new agent would exit at once.</item>
/// </list>
/// </remarks>
internal sealed class ShutdownSignal : IDisposable
{
    private readonly EventWaitHandle? _event;

    /// <summary>Creates (or opens and resets) the event.</summary>
    /// <param name="logger">Receives warnings.</param>
    public ShutdownSignal(ILogger<ShutdownSignal> logger)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), EventWaitHandleRights.Synchronize, AccessControlType.Allow));

        try
        {
            _event = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, HelloAnchorNames.ShutdownEvent, out var createdNew, security);
            if (!createdNew)
            {
                logger.LogWarning("Shutdown event already existed (a previous instance's handle is still open); resetting it.");
            }

            _event.Reset();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Creating Global\ objects needs SeCreateGlobalPrivilege; a developer running the service from a
            // non-elevated console won't have it. Agents are still terminated on stop, just not gracefully.
            logger.LogWarning(ex, "Could not create shutdown event '{Name}'; agents will be terminated instead of asked to exit.", HelloAnchorNames.ShutdownEvent);
        }
    }

    /// <summary>True if the event exists and agents can be asked to exit gracefully.</summary>
    public bool IsAvailable => _event is not null;

    /// <summary>Signals every agent to exit.</summary>
    public void Signal() => _event?.Set();

    /// <inheritdoc />
    public void Dispose() => _event?.Dispose();
}
