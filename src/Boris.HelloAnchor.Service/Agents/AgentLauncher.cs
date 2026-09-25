// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Boris.HelloAnchor.Core;
using Boris.HelloAnchor.Core.Configuration;
using Boris.HelloAnchor.Service.Security;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Boris.HelloAnchor.Service.Agents;

/// <summary>Why an agent could not be launched.</summary>
internal enum LaunchFailure
{
    /// <summary>No agent failure; the launch succeeded.</summary>
    None,

    /// <summary><c>WTSQueryUserToken</c> failed — nobody is logged on to the session (or it is going away).</summary>
    NoUserToken,

    /// <summary>The user has no elevated token and the SYSTEM fallback is off or not trusted (SPEC §6.4).</summary>
    NotElevatable,

    /// <summary>A Win32 call failed while creating the process.</summary>
    Error,
}

/// <summary>The outcome of <see cref="AgentLauncher.TryLaunch"/>.</summary>
/// <param name="Agent">The running agent on success.</param>
/// <param name="Failure">Why the launch failed, if it did.</param>
/// <param name="Detail">Human-readable detail for logging.</param>
internal sealed record LaunchResult(AgentProcess? Agent, LaunchFailure Failure, string Detail)
{
    /// <summary>Creates a failure result.</summary>
    public static LaunchResult Fail(LaunchFailure failure, string detail) => new(null, failure, detail);
}

/// <summary>
/// Starts an elevated agent in a user session (SPEC §6.3 and §6.4).
/// </summary>
/// <remarks>
/// <para>
/// The agent path is always <see cref="AppContext.BaseDirectory"/> + agent file name — never from config or
/// <c>PATH</c> — because the process is created with elevated rights and must only ever be the binary from
/// the protected Program Files install folder.
/// </para>
/// <para>
/// Processes are created suspended, placed in the <see cref="AgentJob"/>, then resumed, so there is no
/// window in which an agent runs outside the kill-on-close job.
/// </para>
/// </remarks>
internal sealed class AgentLauncher(AgentJob job, ILogger<AgentLauncher> logger)
{
    /// <summary><c>SECURITY_MANDATORY_HIGH_RID</c>: integrity level of an elevated administrator.</summary>
    private const int HighIntegrityRid = 0x3000;

    /// <summary>Rights needed on the launch token by DuplicateTokenEx, CreateEnvironmentBlock and CreateProcessAsUser.</summary>
    private const TOKEN_ACCESS_MASK LaunchTokenAccess =
        TOKEN_ACCESS_MASK.TOKEN_QUERY |
        TOKEN_ACCESS_MASK.TOKEN_DUPLICATE |
        TOKEN_ACCESS_MASK.TOKEN_ASSIGN_PRIMARY |
        TOKEN_ACCESS_MASK.TOKEN_IMPERSONATE |
        TOKEN_ACCESS_MASK.TOKEN_ADJUST_DEFAULT |
        TOKEN_ACCESS_MASK.TOKEN_ADJUST_SESSIONID;

    /// <summary>Desktop the agent must run on so it can see the user's windows.</summary>
    private const string Desktop = @"winsta0\default";

    private static long s_generation;

    /// <summary>Full path of the agent, resolved next to the service binary.</summary>
    public static string AgentPath { get; } = Path.Combine(AppContext.BaseDirectory, HelloAnchorNames.AgentExecutable);

    /// <summary>Launches an agent into <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">Target session.</param>
    /// <param name="options">Current configuration (for the SYSTEM fallback decision).</param>
    public LaunchResult TryLaunch(uint sessionId, HelloAnchorOptions options)
    {
        if (!File.Exists(AgentPath))
        {
            return LaunchResult.Fail(LaunchFailure.Error, $"Agent binary not found at '{AgentPath}'.");
        }

        // 1. The session's user token.
        HANDLE rawUserToken = default;
        if (!PInvoke.WTSQueryUserToken(sessionId, ref rawUserToken))
        {
            return LaunchResult.Fail(LaunchFailure.NoUserToken, $"WTSQueryUserToken failed (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        using var userToken = new SafeFileHandle(rawUserToken, ownsHandle: true);

        try
        {
            // 2. Choose the token to launch with.
            var (launchToken, kind, detail) = SelectLaunchToken(userToken, sessionId, options);
            if (launchToken is null)
            {
                return LaunchResult.Fail(LaunchFailure.NotElevatable, detail);
            }

            using (launchToken)
            {
                if (kind == AgentTokenKind.SystemFallback)
                {
                    logger.LogWarning("Session {Session}: launching the agent as SYSTEM on the user's desktop (AllowSystemTokenFallback). {Detail}", sessionId, detail);
                }

                return new LaunchResult(CreateAgentProcess(launchToken, sessionId, kind), LaunchFailure.None, detail);
            }
        }
        catch (Win32Exception ex)
        {
            return LaunchResult.Fail(LaunchFailure.Error, $"{ex.Message} (Win32 error {ex.NativeErrorCode}).");
        }
    }

    /// <summary>
    /// Implements the token selection table in SPEC §6.3 step 2 and the fallback rules in §6.4.
    /// </summary>
    /// <returns>A primary token (caller disposes) or <see langword="null"/> with the reason.</returns>
    private (SafeFileHandle? Token, AgentTokenKind Kind, string Detail) SelectLaunchToken(SafeFileHandle userToken, uint sessionId, HelloAnchorOptions options)
    {
        var elevationType = (TOKEN_ELEVATION_TYPE)ReadInt32(userToken, TOKEN_INFORMATION_CLASS.TokenElevationType);
        string reason;

        switch (elevationType)
        {
            case TOKEN_ELEVATION_TYPE.TokenElevationTypeLimited:
                // Admin with UAC: the full token is linked to the filtered one.
                if (TryGetLinkedToken(userToken, out var linked, out var linkedError))
                {
                    using (linked)
                    {
                        return (DuplicatePrimary(linked), AgentTokenKind.LinkedElevated, "Using the user's elevated linked token.");
                    }
                }

                // Seen with Windows 11 Administrator Protection: treat like a standard user (SPEC §6.3 risk note).
                reason = $"The user's linked token could not be obtained (Win32 error {linkedError}); Administrator Protection may be enabled.";
                break;

            case TOKEN_ELEVATION_TYPE.TokenElevationTypeFull:
                return (DuplicatePrimary(userToken), AgentTokenKind.UserFull, "The user's token is already elevated.");

            default:
                // TokenElevationTypeDefault: either a standard user, or an admin with UAC disabled / the built-in
                // Administrator, whose token is already high integrity.
                var rid = GetIntegrityRid(userToken);
                if (rid >= HighIntegrityRid)
                {
                    return (DuplicatePrimary(userToken), AgentTokenKind.UserFull, $"The user's token is high integrity (0x{rid:X}).");
                }

                reason = $"The user has no elevated token (integrity 0x{rid:X}).";
                break;
        }

        // Standard-user path (SPEC §6.4).
        if (!options.AllowSystemTokenFallback)
        {
            return (null, default, $"{reason} AllowSystemTokenFallback is off, so no agent is launched; the prompt cannot be moved for this user.");
        }

        if (!DataFolderSecurity.IsTrusted(out var aclProblem))
        {
            return (null, default, $"{reason} AllowSystemTokenFallback is on but ignored because the configuration is not protected: {aclProblem}");
        }

        return (CreateSystemTokenForSession(sessionId), AgentTokenKind.SystemFallback, reason);
    }

    /// <summary>
    /// Creates the agent: environment block, suspended <c>CreateProcessAsUser</c>, job assignment, resume.
    /// </summary>
    private unsafe AgentProcess CreateAgentProcess(SafeFileHandle token, uint sessionId, AgentTokenKind kind)
    {
        if (!PInvoke.CreateEnvironmentBlock(out var environment, token, false))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEnvironmentBlock failed.");
        }

        try
        {
            // lpCommandLine must be writable, hence a char array rather than a pinned string.
            var commandLine = $"\"{AgentPath}\" --session {sessionId}\0".ToCharArray();
            var workingDirectory = AppContext.BaseDirectory;

            var addedRef = false;
            token.DangerousAddRef(ref addedRef);
            try
            {
                PROCESS_INFORMATION processInfo;
                fixed (char* application = AgentPath)
                fixed (char* command = commandLine)
                fixed (char* directory = workingDirectory)
                fixed (char* desktop = Desktop)
                {
                    var startup = new STARTUPINFOW
                    {
                        cb = (uint)sizeof(STARTUPINFOW),
                        lpDesktop = desktop,
                    };

                    // Suspended so it can be put in the job before it executes any code.
                    if (!PInvoke.CreateProcessAsUser(
                            (HANDLE)token.DangerousGetHandle(),
                            application,
                            command,
                            null,
                            null,
                            false,
                            PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED,
                            environment,
                            directory,
                            &startup,
                            &processInfo))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcessAsUser failed.");
                    }
                }

                var process = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
                using var thread = new SafeFileHandle(processInfo.hThread, ownsHandle: true);
                try
                {
                    job.Assign(process);
                    if (PInvoke.ResumeThread(thread) == uint.MaxValue)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "ResumeThread failed.");
                    }
                }
                catch
                {
                    // Never leave a suspended or un-jobbed agent behind.
                    PInvoke.TerminateProcess(process, 1);
                    process.Dispose();
                    throw;
                }

                return new AgentProcess(process, processInfo.dwProcessId, sessionId, Interlocked.Increment(ref s_generation), kind);
            }
            finally
            {
                if (addedRef)
                {
                    token.DangerousRelease();
                }
            }
        }
        finally
        {
            PInvoke.DestroyEnvironmentBlock(environment);
        }
    }

    /// <summary>Gets the elevated token linked to a filtered admin token.</summary>
    private static unsafe bool TryGetLinkedToken(SafeFileHandle token, out SafeFileHandle linked, out int error)
    {
        Span<byte> buffer = stackalloc byte[sizeof(TOKEN_LINKED_TOKEN)];
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenLinkedToken, buffer, out _))
        {
            error = Marshal.GetLastPInvokeError();
            linked = new SafeFileHandle();
            return false;
        }

        var handle = MemoryMarshal.Read<TOKEN_LINKED_TOKEN>(buffer).LinkedToken;
        linked = new SafeFileHandle(handle, ownsHandle: true);
        error = 0;
        return !linked.IsInvalid;
    }

    /// <summary>
    /// Duplicates a token as a primary token. With SeTcbPrivilege the linked token is already usable as a
    /// primary token, but duplicating makes the result uniform and independently closable (SPEC §6.3).
    /// </summary>
    private static SafeFileHandle DuplicatePrimary(SafeFileHandle source)
    {
        if (!PInvoke.DuplicateTokenEx(source, LaunchTokenAccess, null, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out var duplicate))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateTokenEx failed.");
        }

        return duplicate;
    }

    /// <summary>Duplicates the service's SYSTEM token and moves it into the user's session (SPEC §6.4).</summary>
    private static SafeFileHandle CreateSystemTokenForSession(uint sessionId)
    {
        using var self = PInvoke.GetCurrentProcess_SafeHandle();
        if (!PInvoke.OpenProcessToken(self, TOKEN_ACCESS_MASK.TOKEN_DUPLICATE | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var processToken))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed.");
        }

        using (processToken)
        {
            var token = DuplicatePrimary(processToken);
            Span<byte> session = stackalloc byte[sizeof(uint)];
            MemoryMarshal.Write(session, in sessionId);

            // Requires SeTcbPrivilege, which LocalSystem holds.
            if (!PInvoke.SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenSessionId, session))
            {
                var error = Marshal.GetLastPInvokeError();
                token.Dispose();
                throw new Win32Exception(error, "SetTokenInformation(TokenSessionId) failed.");
            }

            return token;
        }
    }

    /// <summary>Reads a 32-bit token information value (e.g. <c>TokenElevationType</c>).</summary>
    private static int ReadInt32(SafeFileHandle token, TOKEN_INFORMATION_CLASS infoClass)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        if (!PInvoke.GetTokenInformation(token, infoClass, buffer, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"GetTokenInformation({infoClass}) failed.");
        }

        return MemoryMarshal.Read<int>(buffer);
    }

    /// <summary>Returns the mandatory integrity RID of a token (e.g. 0x2000 medium, 0x3000 high, 0x4000 system).</summary>
    private static unsafe int GetIntegrityRid(SafeFileHandle token)
    {
        // First call reports the required size.
        PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, default, out var size);
        if (size == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetTokenInformation(TokenIntegrityLevel) size query failed.");
        }

        var buffer = new byte[size];
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetTokenInformation(TokenIntegrityLevel) failed.");
        }

        fixed (byte* p = buffer)
        {
            // The label SID is S-1-16-<rid>; its last sub-authority is the integrity level.
            var label = (TOKEN_MANDATORY_LABEL*)p;
            var sid = new SecurityIdentifier((nint)label->Label.Sid.Value);
            var value = sid.Value;
            return int.Parse(value[(value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
