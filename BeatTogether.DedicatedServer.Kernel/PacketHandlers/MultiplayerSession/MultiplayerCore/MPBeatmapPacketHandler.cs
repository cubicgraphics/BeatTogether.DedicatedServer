﻿using BeatTogether.DedicatedServer.Kernel.Abstractions;
using BeatTogether.DedicatedServer.Kernel.Managers.Abstractions;
using BeatTogether.DedicatedServer.Messaging.Packets.MultiplayerSession.MpCorePackets;
using Serilog;
using System.Linq;
using System.Threading.Tasks;

namespace BeatTogether.DedicatedServer.Kernel.PacketHandlers.MultiplayerSession.MenuRpc
{
    class MpBeatmapPacketHandler : BasePacketHandler<MpBeatmapPacket>
    {
        private readonly ILobbyManager _lobbyManager;
        private readonly IPlayerRegistry _playerRegistry;
        private readonly ILogger _logger = Log.ForContext<ClearRecommendedBeatmapPacketHandler>();

        public MpBeatmapPacketHandler(
            ILobbyManager lobbyManager,
            IPlayerRegistry playerRegistry)
        {
            _lobbyManager = lobbyManager;
            _playerRegistry = playerRegistry;
        }

        public override Task Handle(IPlayer sender, MpBeatmapPacket packet)
        {
            _logger.Debug(
                $"Handling packet of type '{nameof(MpBeatmapPacket)}' " +
                $"(SenderId={sender.ConnectionId})."
            );
            lock (sender.BeatmapLock)
            {
                if(sender.BeatmapIdentifier != null && sender.BeatmapIdentifier.LevelId == packet.levelHash)
                {
                    sender.BeatmapIdentifier.Chroma = packet.requirements[packet.difficulty].Contains("Chroma");
                    sender.BeatmapIdentifier.NoodleExtensions = packet.requirements[packet.difficulty].Contains("Noodle Extensions");
                    sender.BeatmapIdentifier.MappingExtensions = packet.requirements[packet.difficulty].Contains("Mapping Extensions");
                }
            }
            return Task.CompletedTask;
        }
    }
}
