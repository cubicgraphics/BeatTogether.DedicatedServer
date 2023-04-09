﻿using BeatTogether.Core.Messaging.Abstractions;
using BeatTogether.DedicatedServer.Kernel.Handshake;

namespace BeatTogether.DedicatedServer.Kernel.Abstractions
{
    public interface IUnconnectedDispatcher
    {
        void Send(HandshakeSession session, IMessage message);
        bool Acknowledge(HandshakeSession session, uint responseId, bool handled = true);
    }
}