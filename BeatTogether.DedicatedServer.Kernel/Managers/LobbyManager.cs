using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatTogether.DedicatedServer.Kernel.Abstractions;
using BeatTogether.DedicatedServer.Kernel.Configuration;
using BeatTogether.DedicatedServer.Kernel.Enums;
using BeatTogether.DedicatedServer.Kernel.Managers.Abstractions;
using BeatTogether.DedicatedServer.Messaging.Enums;
using BeatTogether.DedicatedServer.Messaging.Models;
using BeatTogether.DedicatedServer.Messaging.Packets;
using BeatTogether.DedicatedServer.Messaging.Packets.MultiplayerSession.MenuRpc;
using BeatTogether.LiteNetLib.Enums;
using Serilog;

/*Lobby manager code
 * Contains the logic code for
 * - different game modes
 * - setting the beatmap
 * - setting the modifiers
 * - managing the countdown
 * - checking player entitlements
 * - when to start gameplay
 */
namespace BeatTogether.DedicatedServer.Kernel.Managers
{
    public sealed class LobbyManager : ILobbyManager, IDisposable
    {

        public bool AllPlayersReady => _playerRegistry.Players.All(p => p.IsReady || !p.WantsToPlayNextLevel); //if all players are ready OR spectating
        public bool SomePlayersReady => _playerRegistry.Players.Any(p => p.IsReady);                           //if *any* are ready
        public bool NoPlayersReady => _playerRegistry.Players.All(p => !p.IsReady || !p.WantsToPlayNextLevel); //players not ready or spectating 
        public bool AllPlayersSpectating => _playerRegistry.Players.All(p => !p.WantsToPlayNextLevel);         //if all spectating

        public BeatmapIdentifier? SelectedBeatmap { get; private set; } = null;
        public GameplayModifiers SelectedModifiers { get; private set; } = new();
        public CountdownState CountDownState { get; private set; } = CountdownState.NotCountingDown;
        public float CountdownEndTime { get; private set; } = 0;

        private BeatmapIdentifier? _lastBeatmap = null;
        private bool _lastSpectatorState;
        private bool _AllOwnMap;
        private string _lastManagerId = null!;
        private readonly CancellationTokenSource _stopCts = new();
        private const int LoopTime = 100;

        private readonly InstanceConfiguration _configuration;
        private readonly IDedicatedInstance _instance;
        private readonly IPlayerRegistry _playerRegistry;
        private readonly IPacketDispatcher _packetDispatcher;
        private readonly IGameplayManager _gameplayManager;
        private readonly ILogger _logger = Log.ForContext<LobbyManager>();

        public LobbyManager(
            InstanceConfiguration configuration,
            IDedicatedInstance instance,
            IPlayerRegistry playerRegistry,
            IPacketDispatcher packetDispatcher,
            IGameplayManager gameplayManager
            )
        {
            _configuration = configuration;
            _instance = instance;
            _playerRegistry = playerRegistry;
            _packetDispatcher = packetDispatcher;
            _gameplayManager = gameplayManager;

            _instance.StopEvent += Stop;
            Task.Run(() => UpdateLoop(_stopCts.Token));
        }

        public void Dispose()
        {
            _instance.StopEvent -= Stop;
        }

        private void Stop(IDedicatedInstance inst)
            => _stopCts.Cancel();

        private async void UpdateLoop(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(LoopTime, cancellationToken);
                Update();
                UpdateLoop(cancellationToken);
            }
            catch
            {

            }
        }

        public void Update()
        {
            if (_instance.State != MultiplayerGameState.Lobby)
                return;

            if (!_playerRegistry.TryGetPlayer(_configuration.ManagerId, out var manager) && _configuration.SongSelectionMode == SongSelectionMode.ManagerPicks)
                return;
            
            UpdateBeatmap(GetSelectedBeatmap(), GetSelectedModifiers());

            if (SelectedBeatmap != null)
            {
                bool allPlayersOwnBeatmap = _playerRegistry.Players
                    .All(p => p.GetEntitlement(SelectedBeatmap.LevelId) is EntitlementStatus.Ok or EntitlementStatus.NotDownloaded);

                // If new beatmap selected or entitlement state changed or spectator state changed or manager changed
                if (_lastBeatmap != SelectedBeatmap || _AllOwnMap != allPlayersOwnBeatmap || _lastSpectatorState != AllPlayersSpectating || _lastManagerId != _configuration.ManagerId)
                {
                    // If not all players have beatmap
                    if (!allPlayersOwnBeatmap)
                    {
                        // Set players missing entitlements
                        _packetDispatcher.SendToNearbyPlayers(new SetPlayersMissingEntitlementsToLevelPacket
                        {
                            PlayersWithoutEntitlements = _playerRegistry.Players
                                .Where(p => p.GetEntitlement(SelectedBeatmap.LevelId) is EntitlementStatus.NotOwned or EntitlementStatus.Unknown)
                                .Select(p => p.UserId).ToList()
                        }, DeliveryMethod.ReliableOrdered);

                        // Cannot start song because losers dont have your map
                        _packetDispatcher.SendToNearbyPlayers(new SetIsStartButtonEnabledPacket
                        {
                            Reason = CannotStartGameReason.DoNotOwnSong
                        }, DeliveryMethod.ReliableOrdered);

                        // get entitlement from players
                        _packetDispatcher.SendToNearbyPlayers(new GetIsEntitledToLevelPacket
                        {
                            LevelId = SelectedBeatmap.LevelId
                        }, DeliveryMethod.ReliableOrdered);
                    }
                    // If all players have beatmap
                    else
                    {
                        // Set no players missing entitlements
                        _packetDispatcher.SendToNearbyPlayers(new SetPlayersMissingEntitlementsToLevelPacket
                        {
                            PlayersWithoutEntitlements = new List<string>()
                        }, DeliveryMethod.ReliableOrdered);

                        // Allow start map if at least one player is not spectating
                        _packetDispatcher.SendToNearbyPlayers(new SetIsStartButtonEnabledPacket
                        {
                            Reason = AllPlayersSpectating ? CannotStartGameReason.AllPlayersSpectating : CannotStartGameReason.None
                        }, DeliveryMethod.ReliableOrdered);

                    }
                }
                _AllOwnMap = allPlayersOwnBeatmap;

                switch (_configuration.SongSelectionMode) //server modes
                {
                    case SongSelectionMode.ManagerPicks:
                        CountingDown(manager!.IsReady, !manager.IsReady);
                        break;
                    case SongSelectionMode.Vote:
                        CountingDown(SomePlayersReady, NoPlayersReady);
                        break;
                    case SongSelectionMode.RandomPlayerPicks:
                        CountingDown(SomePlayersReady, NoPlayersReady);
                        break;
                    case SongSelectionMode.ServerPicks:
                        CountingDown(true, false);
                        break;
                }
            }
            // If beatmap is null and it wasn't previously or manager changed
            else if (_lastBeatmap != SelectedBeatmap || _lastManagerId != _configuration.ManagerId)
            {
                // Cannot select beatmap because no beatmap is selected
                _packetDispatcher.SendToNearbyPlayers(new SetIsStartButtonEnabledPacket
                {
                    Reason = CannotStartGameReason.NoSongSelected
                }, DeliveryMethod.ReliableOrdered);
                //Send stop countdown packet if the beatmap is somehow set to null (manager may disconnect, or if tournament server setting the beatmap to null should stop the countdown)
                if(CountDownState != CountdownState.NotCountingDown)
                {
                    CancelCountdown();
                }
            }

            _lastManagerId = _configuration.ManagerId;
            _lastSpectatorState = AllPlayersSpectating;
            _lastBeatmap = SelectedBeatmap;
        }

        private void CountingDown(bool isReady, bool NotStartable)
        {
            // If not already counting down
            if (CountDownState == CountdownState.NotCountingDown)
            {
                if (CountdownEndTime != 0 && CountdownEndTime <= _instance.RunTime)
                    CancelCountdown();
                if ((AllPlayersReady && !AllPlayersSpectating && _AllOwnMap))
                    SetCountdown(CountdownState.StartBeatmapCountdown);
                else if (isReady && _AllOwnMap)
                    SetCountdown(CountdownState.CountingDown, _configuration.CountdownConfig.CountdownTimePlayersReady);
            }
            // If counting down
            if (CountDownState != CountdownState.NotCountingDown && CountdownEndTime <= _instance.RunTime)
            {
                // If countdown just finished, send map then pause lobby untill all players have map downloaded
                if (CountDownState != CountdownState.WaitingForEntitlement)
                    SetCountdown(CountdownState.WaitingForEntitlement);
                if (_playerRegistry.Players.All(p => p.GetEntitlement(SelectedBeatmap!.LevelId) is EntitlementStatus.Ok))
                {
                    // sends entitlements to players one last time
                    _packetDispatcher.SendToNearbyPlayers(new SetPlayersMissingEntitlementsToLevelPacket
                    {
                        PlayersWithoutEntitlements = _playerRegistry.Players
                            .Where(p => p.GetEntitlement(SelectedBeatmap!.LevelId) is EntitlementStatus.NotOwned)
                            .Select(p => p.UserId).ToList()
                    }, DeliveryMethod.ReliableOrdered);
                    //starts beatmap
                    _gameplayManager.SetBeatmap(SelectedBeatmap!, SelectedModifiers);
                    Task.Run(()=> _gameplayManager.StartSong(CancellationToken.None));
                    //stops countdown
                    SetCountdown(CountdownState.NotCountingDown);
                    return;
                }
                if(CountdownEndTime + _configuration.KickPlayersWithoutEntitlementTimeout <= _instance.RunTime)
                {
                    List<IPlayer> MissingEntitlement = _playerRegistry.Players.Where(p => p.GetEntitlement(SelectedBeatmap!.LevelId) is not EntitlementStatus.Ok).ToList();
                    foreach (IPlayer p in MissingEntitlement)
                    {
                        _packetDispatcher.SendToPlayer(p, new KickPlayerPacket()
                        {
                            DisconnectedReason = DisconnectedReason.ClientConnectionClosed
                        }, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
            // If manager/all players are no longer ready or not all players own beatmap
            if (NotStartable || !_AllOwnMap || AllPlayersSpectating)
                CancelCountdown();
            else if (AllPlayersReady || (CountdownEndTime - _instance.RunTime) > _configuration.CountdownConfig.BeatMapStartCountdownTime)
                SetCountdown(CountdownState.StartBeatmapCountdown);
        }

        public void UpdateBeatmap(BeatmapIdentifier? beatmap, GameplayModifiers modifiers)
        {
            if (SelectedBeatmap != beatmap)
            {
                if (beatmap != null)
                    SelectedBeatmap = beatmap;
                else
                {
                    SelectedBeatmap = null;
                }
            }
            if (SelectedModifiers != modifiers)
                SelectedModifiers = modifiers;
        }

        private List<BeatmapDifficulty> GetSelectedBeatmapDifficulties()
        {
            if (!SelectedBeatmap!.LevelId.StartsWith("custom_level_"))
            {
                return new();
            }
            foreach (var player in _playerRegistry.Players)
            {
                if(SelectedBeatmap!.LevelId == player.MapHash)
                {
                    return player.Difficulties;
                }
            }
            return new();
        }


        // Sets countdown and beatmap how the client would expect it to
        // If you want to cancel the countdown use CancelCountdown(), Not SetCountdown as CancelCountdown() ALSO informs the clients it has been canceled, whereas SetCountdown will now
        public void SetCountdown(CountdownState countdownState, float countdown = 0)
        {
            CountDownState = countdownState;
            switch (CountDownState)
            {
                case CountdownState.NotCountingDown:
                    CountdownEndTime = 0;
                    SelectedBeatmap = null;
                    SelectedModifiers = new();
                    break;
                case CountdownState.CountingDown:
                    if (countdown == 0)
                        countdown = 30f;
                    CountdownEndTime = _instance.RunTime + countdown;
                    _packetDispatcher.SendToNearbyPlayers(new SetCountdownEndTimePacket
                    {
                        CountdownTime = CountdownEndTime
                    }, DeliveryMethod.ReliableOrdered);
                    break;
                case CountdownState.StartBeatmapCountdown:
                    if (countdown == 0)
                        countdown = 5f;
                    CountdownEndTime = _instance.RunTime + countdown;
                    StartBeatmapPacket();
                    break;
                case CountdownState.WaitingForEntitlement:
                    StartBeatmapPacket();
                    break;
            }
            _instance.InstanceStateChanged(CountDownState, _gameplayManager.State);
        }

        //Checks the lobby settings and sends the player the correct beatmap
        private void StartBeatmapPacket()
        {
            switch (_configuration.AllowPerPlayerModifiers)
            {
                case false:
                    switch (_configuration.AllowPerPlayerDifficulties)
                    {
                        case false:
                            _packetDispatcher.SendToNearbyPlayers(new StartLevelPacket
                            {
                                Beatmap = SelectedBeatmap!,
                                Modifiers = SelectedModifiers,
                                StartTime = CountdownEndTime
                            }, DeliveryMethod.ReliableOrdered);
                            break;
                        case true:
                            List<BeatmapDifficulty> diff = GetSelectedBeatmapDifficulties();
                            foreach (var player in _playerRegistry.Players)
                            {
                                BeatmapIdentifier bm = SelectedBeatmap!;
                                if (player.PreferredDifficulty != null && diff.Contains((BeatmapDifficulty)player.PreferredDifficulty))
                                    bm.Difficulty = (BeatmapDifficulty)player.PreferredDifficulty!;
                                _packetDispatcher.SendToPlayer(player, new StartLevelPacket
                                {
                                    Beatmap = bm!,
                                    Modifiers = SelectedModifiers,
                                    StartTime = CountdownEndTime
                                }, DeliveryMethod.ReliableOrdered);
                            }
                            break;
                    }
                    break;
                case true:
                    switch (_configuration.AllowPerPlayerDifficulties)
                    {
                        case false:
                            foreach (var player in _playerRegistry.Players)
                            {
                                _packetDispatcher.SendToPlayer(player, new StartLevelPacket
                                {
                                    Beatmap = SelectedBeatmap!,
                                    Modifiers = player.Modifiers,
                                    StartTime = CountdownEndTime
                                }, DeliveryMethod.ReliableOrdered);
                            }
                            break;
                        case true:
                            List<BeatmapDifficulty> diff = GetSelectedBeatmapDifficulties();
                            foreach (var player in _playerRegistry.Players)
                            {
                                BeatmapIdentifier bm = SelectedBeatmap!;
                                if (player.PreferredDifficulty != null && diff.Contains((BeatmapDifficulty)player.PreferredDifficulty))
                                    bm.Difficulty = (BeatmapDifficulty)player.PreferredDifficulty!;
                                _packetDispatcher.SendToPlayer(player, new StartLevelPacket
                                {
                                    Beatmap = bm!,
                                    Modifiers = player.Modifiers,
                                    StartTime = CountdownEndTime
                                }, DeliveryMethod.ReliableOrdered);
                            }
                            break;
                    }
                    break;
            }
            _instance.BeatmapChanged(SelectedBeatmap, SelectedModifiers, false, DateTime.Now.AddSeconds(_instance.RunTime - CountdownEndTime));
        }

        public void CancelCountdown()
        {
            switch (CountDownState)
            {
                case CountdownState.CountingDown or CountdownState.NotCountingDown:
                    _packetDispatcher.SendToNearbyPlayers(new CancelCountdownPacket(), DeliveryMethod.ReliableOrdered);
                    break;
                case CountdownState.StartBeatmapCountdown or CountdownState.WaitingForEntitlement:
                    foreach (var player in _playerRegistry.Players)
                    {
                        player.IsReady = false;
                    }
                    _packetDispatcher.SendToNearbyPlayers(new CancelLevelStartPacket(), DeliveryMethod.ReliableOrdered);
                    break;
                default:
                    _logger.Warning("Canceling countdown when there is no countdown to cancel");
                    break;
            }
            SetCountdown(CountdownState.NotCountingDown);
        }

        public BeatmapIdentifier? GetSelectedBeatmap()
        {
            switch(_configuration.SongSelectionMode)
            {
                case SongSelectionMode.ManagerPicks:
                    {
                        if(_playerRegistry.TryGetPlayer(_configuration.ManagerId, out var p))
                            if(p.BeatmapIdentifier != null)
                            {
                                bool passed = ((!(p.Chroma && !_configuration.AllowChroma) || !(p.MappingExtensions && !_configuration.AllowMappingExtensions) || !(p.NoodleExtensions && !_configuration.AllowNoodleExtensions)) && p.MapHash == p.BeatmapIdentifier!.LevelId) || p.MapHash != p.BeatmapIdentifier!.LevelId;
                                if (passed)
                                    return p.BeatmapIdentifier;
                            }
                        return null;
                    }
                case SongSelectionMode.Vote:
                    Dictionary<BeatmapIdentifier, int> voteDictionary = new();
                    foreach (IPlayer player in _playerRegistry.Players.Where(p => p.BeatmapIdentifier != null && (((!(p.Chroma && !_configuration.AllowChroma) || !(p.MappingExtensions && !_configuration.AllowMappingExtensions) || !(p.NoodleExtensions && !_configuration.AllowNoodleExtensions)) && p.MapHash == p.BeatmapIdentifier!.LevelId) || p.MapHash != p.BeatmapIdentifier!.LevelId)))
                    {
                        if (voteDictionary.ContainsKey(player.BeatmapIdentifier!))
                            voteDictionary[player.BeatmapIdentifier!]++;
                        else
                            voteDictionary.Add(player.BeatmapIdentifier!, 1);
                    }
                    if (!voteDictionary.Any())
                    {
                        return null;
                    }
                    return voteDictionary.OrderByDescending(n => n.Value).First().Key;
                case SongSelectionMode.RandomPlayerPicks:
                    if (SelectedBeatmap != _lastBeatmap || SelectedBeatmap == null)
                    {
                        IPlayer p = _playerRegistry.Players[new Random().Next(_playerRegistry.Players.Count)];
                        if ((((!(p.Chroma && !_configuration.AllowChroma) || !(p.MappingExtensions && !_configuration.AllowMappingExtensions) || !(p.NoodleExtensions && !_configuration.AllowNoodleExtensions)) && p.MapHash == p.BeatmapIdentifier!.LevelId) || p.MapHash != p.BeatmapIdentifier!.LevelId))
                            return p.BeatmapIdentifier; //TODO, Fix this to work correctly at some point
                        return null;
                    }
                    return SelectedBeatmap;
                case SongSelectionMode.ServerPicks:
                    return SelectedBeatmap!;
            };
            return null;
        }

        public GameplayModifiers GetSelectedModifiers()
		{
            switch(_configuration.SongSelectionMode)
			{
                case SongSelectionMode.ManagerPicks: return _playerRegistry.GetPlayer(_configuration.ManagerId).Modifiers;
                case SongSelectionMode.Vote or SongSelectionMode.RandomPlayerPicks:
                    GameplayModifiers gameplayModifiers = new();
                    Dictionary<GameplayModifiers, int> voteDictionary = new();
                    foreach (IPlayer player in _playerRegistry.Players.Where(p => p.Modifiers != null))
                    {
                        if (voteDictionary.ContainsKey(player.Modifiers))
                            voteDictionary[player.Modifiers]++;
                        else
                            voteDictionary.Add(player.Modifiers!, 1);
                    }
                    if (voteDictionary.Any())
                        gameplayModifiers = voteDictionary.OrderByDescending(n => n.Value).First().Key;
                    gameplayModifiers.NoFailOn0Energy = true;
                    return gameplayModifiers;
                case SongSelectionMode.ServerPicks:
                    return SelectedModifiers;
            };
            return new GameplayModifiers();
		}
    }
}
