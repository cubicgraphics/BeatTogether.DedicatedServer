﻿using BeatTogether.DedicatedServer.Kernel.Abstractions;
using BeatTogether.DedicatedServer.Kernel.Managers;
using BeatTogether.DedicatedServer.Kernel.Managers.Abstractions;
using BeatTogether.DedicatedServer.Messaging.Packets.MultiplayerSession.MenuRpc;
using BeatTogether.LiteNetLib.Enums;
using Serilog;
using System.Threading.Tasks;

namespace BeatTogether.DedicatedServer.Kernel.PacketHandlers.MultiplayerSession.MenuRpc
{
    public sealed class GetSelectedGameplayModifiersHandler : BasePacketHandler<GetSelectedGameplayModifiers>
    {
        private readonly IPacketDispatcher _packetDispatcher;
        private readonly ILobbyManager _lobbyManager;
        private readonly IGameplayManager _gameplayManager;
        private readonly IDedicatedInstance _instance;
        private readonly ILogger _logger = Log.ForContext<GetSelectedGameplayModifiersHandler>();

        public GetSelectedGameplayModifiersHandler(
            IPacketDispatcher packetDispatcher,
            ILobbyManager lobbyManager,
            IGameplayManager gameplayManager,
            IDedicatedInstance dedicatedInstance)
        {
            _packetDispatcher = packetDispatcher;
            _lobbyManager = lobbyManager;
            _gameplayManager = gameplayManager;
            _instance = dedicatedInstance;
        }

        public override Task Handle(IPlayer sender, GetSelectedGameplayModifiers packet)
        {
            _logger.Debug(
                $"Handling packet of type '{nameof(GetSelectedGameplayModifiers)}' " +
                $"(SenderId={sender.ConnectionId})."
            );
            if(_instance.State == Messaging.Enums.MultiplayerGameState.Lobby && _lobbyManager.SelectedModifiers != _lobbyManager.EmptyModifiers)
            {
                _packetDispatcher.SendToPlayer(sender, new SetSelectedGameplayModifiers
                {
                    Modifiers = _lobbyManager.SelectedModifiers
                }, DeliveryMethod.ReliableOrdered);
                return Task.CompletedTask;
            }
            if (_instance.State == Messaging.Enums.MultiplayerGameState.Game && _gameplayManager.State != Enums.GameplayManagerState.Results)
            {
                _packetDispatcher.SendToPlayer(sender, new SetSelectedGameplayModifiers
                {
                    Modifiers = _gameplayManager.CurrentModifiers
                }, DeliveryMethod.ReliableOrdered);
                return Task.CompletedTask;
            }
            _packetDispatcher.SendToPlayer(sender, new ClearSelectedGameplayModifiers(), DeliveryMethod.ReliableOrdered);
            return Task.CompletedTask;
        }
    }
}
