﻿using BeatTogether.DedicatedServer.Kernel.Abstractions;
using BeatTogether.DedicatedServer.Kernel.Configuration;
using BeatTogether.DedicatedServer.Messaging.Models;
using BeatTogether.DedicatedServer.Messaging.Packets.MultiplayerSession.MenuRpc;
using BeatTogether.DedicatedServer.Messaging.Packets.MultiplayerSession.MpCorePackets;
using BeatTogether.LiteNetLib.Enums;
using Serilog;
using System.Linq;
using System.Threading.Tasks;

namespace BeatTogether.DedicatedServer.Kernel.PacketHandlers.MultiplayerSession.MenuRpc
{
    class MCSetNewManagerPacketHandler : BasePacketHandler<MCSetNewManagerPacket>
    {
        public InstanceConfiguration _configuration;
        public readonly IPacketDispatcher _packetDispatcher;
        public readonly IPlayerRegistry _playerRegistry;
        private readonly ILogger _logger = Log.ForContext<ClearRecommendedBeatmapPacketHandler>();

        public MCSetNewManagerPacketHandler(
            IPlayerRegistry playerRegistry,
            IPacketDispatcher packetDispatcher,
            InstanceConfiguration configuration)
        {
            _playerRegistry = playerRegistry;
            _packetDispatcher = packetDispatcher;
            _configuration = configuration;
        }

        object handleLock = new();
        public override Task Handle(IPlayer sender, MCSetNewManagerPacket packet)
        {
            _logger.Debug(
                $"Handling packet of type '{nameof(MpBeatmapPacket)}' " +
                $"(SenderId={sender.ConnectionId})."
            );
            lock (handleLock)
            {
                if (sender.IsManager && _configuration.GameplayServerMode == Enums.GameplayServerMode.Managed)
                {
                    _configuration.ManagerId = packet.NewManagerID;

                    _packetDispatcher.SendToNearbyPlayers(new SetPlayersPermissionConfigurationPacket
                    {
                        PermissionConfiguration = new PlayersPermissionConfiguration
                        {
                            PlayersPermission = _playerRegistry.Players.Select(x => new PlayerPermissionConfiguration
                            {
                                UserId = x.UserId,
                                IsServerOwner = x.IsManager,
                                HasRecommendBeatmapsPermission = x.CanRecommendBeatmaps,
                                HasRecommendGameplayModifiersPermission = x.CanRecommendModifiers,
                                HasKickVotePermission = x.CanKickVote,
                                HasInvitePermission = x.CanInvite
                            }).ToList()
                        }
                    }, DeliveryMethod.ReliableOrdered);
                }
            }
            return Task.CompletedTask;
        }
    }
}