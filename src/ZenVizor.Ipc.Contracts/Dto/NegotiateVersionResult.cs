// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

public sealed record NegotiateVersionResult(
    bool Accepted,
    string ServerVersion,
    string? Reason);
