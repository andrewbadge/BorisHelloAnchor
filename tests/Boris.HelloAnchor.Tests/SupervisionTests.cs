// Copyright (C) 2026 Boris HelloAnchor contributors
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of Boris.HelloAnchor. It is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version. See the LICENSE file for details.

using Boris.HelloAnchor.Core;

namespace Boris.HelloAnchor.Tests;

/// <summary>Tests for the agent supervision rules: backoff and exit-code policy (SPEC §6.3, §10).</summary>
public sealed class SupervisionTests
{
    private static readonly int[] Backoff = [2, 5, 15, 60];

    /// <summary>Successive restarts walk the list, then the last value repeats.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 60)]
    [InlineData(4, 60)]
    [InlineData(100, 60)]
    public void GetDelay_WalksListThenRepeatsLast(int restartCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RestartBackoff.GetDelay(restartCount, Backoff));
    }

    /// <summary>A negative count is treated as the first restart.</summary>
    [Fact]
    public void GetDelay_NegativeCount_UsesFirst()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RestartBackoff.GetDelay(-1, Backoff));
    }

    /// <summary>An empty backoff list is a programming error.</summary>
    [Fact]
    public void GetDelay_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => RestartBackoff.GetDelay(0, []));
    }

    /// <summary>Quick crashes keep escalating the backoff.</summary>
    [Fact]
    public void EffectiveCount_ShortUptime_KeepsCount()
    {
        Assert.Equal(3, RestartBackoff.EffectiveCount(3, TimeSpan.FromSeconds(10)));
    }

    /// <summary>An agent that ran for 5 minutes or more starts a new sequence.</summary>
    [Theory]
    [InlineData(300)]
    [InlineData(3600)]
    public void EffectiveCount_LongUptime_Resets(int uptimeSeconds)
    {
        Assert.Equal(0, RestartBackoff.EffectiveCount(3, TimeSpan.FromSeconds(uptimeSeconds)));
    }

    /// <summary>Just under the threshold does not reset.</summary>
    [Fact]
    public void EffectiveCount_JustUnderThreshold_DoesNotReset()
    {
        Assert.Equal(2, RestartBackoff.EffectiveCount(2, RestartBackoff.ResetAfter - TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>Exit codes 0 and 2 are deliberate; everything else triggers a restart.</summary>
    [Theory]
    [InlineData(0u, AgentExitAction.Park)]
    [InlineData(2u, AgentExitAction.Park)]
    [InlineData(1u, AgentExitAction.Restart)]
    [InlineData(0xC0000005u, AgentExitAction.Restart)]
    [InlineData(0xE0434352u, AgentExitAction.Restart)]
    [InlineData(uint.MaxValue, AgentExitAction.Restart)]
    public void Decide_MapsExitCodes(uint exitCode, AgentExitAction expected)
    {
        Assert.Equal(expected, AgentRestartPolicy.Decide(exitCode));
    }
}
