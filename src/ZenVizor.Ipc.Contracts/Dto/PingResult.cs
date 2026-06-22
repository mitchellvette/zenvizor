// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

public sealed record PingResult(
    string Pong,
    long ServerTimestampUnixMs);
