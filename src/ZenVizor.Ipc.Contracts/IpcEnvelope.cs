// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts;

/// <summary>
/// Versioned wrapper for IPC payloads whose schema is expected to evolve
/// independently of the wire protocol's <see cref="ProtocolVersion"/>.
/// <para>
/// <c>SchemaVersion</c> is a per-payload-type discriminator that starts at 1.
/// Additive changes (new optional fields) keep the version. Removing or
/// renaming a field bumps the version; the server may serve multiple versions
/// during a deprecation window.
/// </para>
/// <para>
/// Existing Phase-0 methods (NegotiateVersion / Ping / GetServiceStatus) stay
/// unwrapped to preserve the version-negotiation handshake's compatibility.
/// </para>
/// </summary>
public sealed record IpcEnvelope<T>(int SchemaVersion, T Payload);
