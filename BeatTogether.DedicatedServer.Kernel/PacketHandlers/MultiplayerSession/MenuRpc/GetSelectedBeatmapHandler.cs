﻿using BeatTogether.DedicatedServer.Kernel.Abstractions;
using BeatTogether.DedicatedServer.Kernel.Managers.Abstractions;
using BeatTogether.DedicatedServer.Messaging.Packets.MultiplayerSession.MenuRpc;
using BeatTogether.LiteNetLib.Enums;
using Serilog;
using System.Threading.Tasks;

namespace BeatTogether.DedicatedServer.Kernel.PacketHandlers.MultiplayerSession.MenuRpc
{
    public sealed class GetSelectedBeatmapHandler : BasePacketHandler<GetSelectedBeatmap>
    {
        private readonly IPacketDispatcher _packetDispatcher;
        private readonly ILobbyManager _lobbyManager;
        private readonly IGameplayManager _gameplayManager;
        private readonly IDedicatedInstance _instance;
        private readonly ILogger _logger = Log.ForContext<GetSelectedBeatmapHandler>();

        public GetSelectedBeatmapHandler(
            IPacketDispatcher packetDispatcher,
            ILobbyManager lobbyManager,
            IGameplayManager gameplayManager,
            IDedicatedInstance instance)
        {
            _packetDispatcher = packetDispatcher;
            _lobbyManager = lobbyManager;
            _gameplayManager = gameplayManager;
            _instance = instance;
        }

        public override Task Handle(IPlayer sender, GetSelectedBeatmap packet)
        {
            _logger.Debug(
                $"Handling packet of type '{nameof(GetSelectedBeatmap)}' " +
                $"(SenderId={sender.ConnectionId})."
            );
            if(_instance.State == Messaging.Enums.MultiplayerGameState.Lobby && _lobbyManager.SelectedBeatmap != null)
            {
                _packetDispatcher.SendToPlayer(sender, new SetSelectedBeatmap
                {
                    Beatmap = _lobbyManager.SelectedBeatmap
                }, DeliveryMethod.ReliableOrdered);
                return Task.CompletedTask;
            }
            if (_instance.State == Messaging.Enums.MultiplayerGameState.Game && _gameplayManager.State != Enums.GameplayManagerState.Results && _gameplayManager.CurrentBeatmap != null)
            {
                _packetDispatcher.SendToPlayer(sender, new SetSelectedBeatmap
                {
                    Beatmap = _gameplayManager.CurrentBeatmap
                }, DeliveryMethod.ReliableOrdered);
                return Task.CompletedTask;
            }
            _packetDispatcher.SendToPlayer(sender, new ClearSelectedBeatmap(), DeliveryMethod.ReliableOrdered);
            return Task.CompletedTask;
        }
    }
}
