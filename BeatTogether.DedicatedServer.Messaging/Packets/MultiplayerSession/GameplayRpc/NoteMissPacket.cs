﻿using BeatTogether.DedicatedServer.Messaging.Abstractions;
using BeatTogether.DedicatedServer.Messaging.Models;
using Krypton.Buffers;

namespace BeatTogether.DedicatedServer.Messaging.Packets.MultiplayerSession.GameplayRpc
{
    public sealed class NoteMissPacket : BaseRpcWithValuesPacket
    {
        public float SongTime { get; set; }
        public NoteMissInfo Info { get; set; } = new();

        public override void ReadFrom(ref SpanBufferReader reader)
        {
            base.ReadFrom(ref reader);
            if (HasValue0)
                SongTime = reader.ReadFloat32();
            if (HasValue1)
                Info.ReadFrom(ref reader);
        }

        public override void WriteTo(ref SpanBufferWriter writer)
        {
            base.WriteTo(ref writer);
            writer.WriteFloat32(SongTime);
            Info.WriteTo(ref writer);
        }
    }
}
