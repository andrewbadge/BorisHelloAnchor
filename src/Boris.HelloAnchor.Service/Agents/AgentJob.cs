// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;

namespace Boris.HelloAnchor.Service.Agents;

/// <summary>
/// A Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> that owns every agent (SPEC §6.3 step 5).
/// </summary>
/// <remarks>
/// When the service process ends for any reason — clean stop, crash or <c>taskkill /f</c> — the kernel closes
/// the job handle and terminates every agent in it. A restarted service therefore never finds orphaned
/// agents holding the per-session mutex.
/// </remarks>
internal sealed class AgentJob : IDisposable
{
    private readonly SafeFileHandle _job;

    /// <summary>Creates the job and applies the kill-on-close limit.</summary>
    /// <exception cref="Win32Exception">The job could not be created or configured.</exception>
    public unsafe AgentJob()
    {
        _job = PInvoke.CreateJobObject((SECURITY_ATTRIBUTES?)null, (string?)null);
        if (_job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var bytes = new ReadOnlySpan<byte>(&info, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        if (!PInvoke.SetInformationJobObject(_job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, bytes))
        {
            var error = Marshal.GetLastPInvokeError();
            _job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
    }

    /// <summary>Adds a (suspended) process to the job.</summary>
    /// <param name="process">Process handle with <c>PROCESS_SET_QUOTA | PROCESS_TERMINATE</c> access.</param>
    /// <exception cref="Win32Exception">Assignment failed.</exception>
    public void Assign(SafeHandle process)
    {
        if (!PInvoke.AssignProcessToJobObject(_job, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed.");
        }
    }

    /// <summary>Closes the job handle, which terminates any agent still running.</summary>
    public void Dispose() => _job.Dispose();
}
