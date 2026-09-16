using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace TwoBirds
{
    public sealed class SteamLobby : MonoBehaviour
    {
        public readonly List<(ulong Id, string Name)> Friends = new();
        public bool HasLobby => lobby.IsValid();
        internal ulong CurrentLobby => lobby.m_SteamID;
        public bool Available => steam && steam.Ready;
        public string FriendsStatus { get; private set; } = "Refresh to find friends playing Two Birds.";
        public event Action Changed;
        private readonly List<IDisposable> pending = new();
        private readonly Dictionary<ulong, int> joining = new();
        private readonly Dictionary<ulong, string> candidates = new();
        private SteamLifetime steam;
        private SessionController session;
        private CSteamID lobby;
        private ulong originalHost;
        private Callback<GameLobbyJoinRequested_t> invite;
        private Callback<LobbyChatUpdate_t> membership;
        private Callback<LobbyDataUpdate_t> data;

        private void Start()
        {
            steam = GetComponent<SteamLifetime>();
            session = GetComponent<SessionController>();
            if (!Available) return;
            invite = Callback<GameLobbyJoinRequested_t>.Create(message => session.JoinSteamLobby(message.m_steamIDLobby.m_SteamID));
            membership = Callback<LobbyChatUpdate_t>.Create(MembershipChanged);
            data = Callback<LobbyDataUpdate_t>.Create(DataChanged);
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id)) session.JoinSteamLobby(id);
        }

        internal void Create(int attempt)
        {
            CallResult<LobbyCreated_t> call = null;
            call = CallResult<LobbyCreated_t>.Create((result, failed) =>
            {
                pending.Remove(call);
                call.Dispose();
                var created = new CSteamID(result.m_ulSteamIDLobby);
                if (!session.IsAttempt(attempt))
                {
                    if (!failed && result.m_eResult == EResult.k_EResultOK && created != lobby && !joining.ContainsKey(created.m_SteamID))
                        SteamMatchmaking.LeaveLobby(created);
                    return;
                }
                if (failed || result.m_eResult != EResult.k_EResultOK)
                { session.Leave($"Steam lobby creation failed: {(failed ? "Steam connection error" : result.m_eResult.ToString())}."); return; }
                lobby = created;
                originalHost = steam.UserId;
                SteamMatchmaking.SetLobbyData(lobby, "game", SessionController.GameId);
                SteamMatchmaking.SetLobbyData(lobby, "protocol", SessionController.Protocol);
                SteamMatchmaking.SetLobbyData(lobby, "host", originalHost.ToString());
                session.LobbyCreated(attempt);
                session.UpdateLobby();
                Changed?.Invoke();
            });
            pending.Add(call);
            var handle = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, SessionController.MultiplayerCapacity);
            if (handle == SteamAPICall_t.Invalid)
            {
                pending.Remove(call);
                call.Dispose();
                session.Leave("Steam could not submit the lobby creation request.");
                return;
            }
            call.Set(handle);
        }

        internal void Join(ulong id, int attempt)
        {
            bool alreadyPending = joining.ContainsKey(id);
            joining[id] = attempt;
            if (alreadyPending) return;
            CallResult<LobbyEnter_t> call = null;
            call = CallResult<LobbyEnter_t>.Create((result, failed) =>
            {
                pending.Remove(call);
                call.Dispose();
                int currentAttempt = joining[id];
                joining.Remove(id);
                var entered = new CSteamID(result.m_ulSteamIDLobby);
                bool success = !failed && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
                if (!session.IsAttempt(currentAttempt))
                {
                    if (success && entered != lobby) SteamMatchmaking.LeaveLobby(entered);
                    return;
                }
                if (!success)
                {
                    string reason = failed ? "Steam connection error" : (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse switch
                    {
                        EChatRoomEnterResponse.k_EChatRoomEnterResponseFull => "lobby is full",
                        EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist => "lobby no longer exists",
                        EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed => "lobby is not accepting this join",
                        _ => ((EChatRoomEnterResponse)result.m_EChatRoomEnterResponse).ToString()
                    };
                    session.Leave($"Steam lobby join failed: {reason}.");
                    return;
                }
                lobby = entered;
                string error = ConnectionError(lobby, out originalHost);
                if (error != null) { session.Leave(error); return; }
                session.SteamJoined(currentAttempt, originalHost);
                Changed?.Invoke();
            });
            pending.Add(call);
            var handle = SteamMatchmaking.JoinLobby(new CSteamID(id));
            if (handle == SteamAPICall_t.Invalid)
            {
                joining.Remove(id);
                pending.Remove(call);
                call.Dispose();
                session.Leave("Steam could not submit the lobby join request.");
                return;
            }
            call.Set(handle);
        }

        private static string ConnectionError(CSteamID target, out ulong host)
        {
            host = 0;
            if (SteamMatchmaking.GetLobbyData(target, "game") != SessionController.GameId ||
                SteamMatchmaking.GetLobbyData(target, "protocol") != SessionController.Protocol)
                return "This lobby is for another game or an incompatible version of Two Birds.";
            string phase = SteamMatchmaking.GetLobbyData(target, "phase");
            if (phase != "lobby" && phase != "loading" && phase != "playing") return "The Steam lobby is not accepting connections.";
            if (!ulong.TryParse(SteamMatchmaking.GetLobbyData(target, "host"), out host) || host == 0 ||
                SteamMatchmaking.GetLobbyOwner(target).m_SteamID != host) return "The original host has left the session.";
            return null;
        }

        internal void Publish(string phase, bool spaceAvailable)
        {
            if (!Available || !HasLobby || originalHost != steam.UserId) return;
            SteamMatchmaking.SetLobbyData(lobby, "phase", phase);
            SteamMatchmaking.SetLobbyJoinable(lobby, phase != "closing" && spaceAvailable);
        }

        public void InviteFriends()
        {
            if (Available && HasLobby) SteamFriends.ActivateGameOverlayInviteDialog(lobby);
        }

        public void RefreshFriends()
        {
            Friends.Clear();
            candidates.Clear();
            if (!Available)
            {
                FriendsStatus = steam ? steam.Failure : "Steam is unavailable.";
                Changed?.Invoke();
                return;
            }
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(friend, out var game) || !game.m_steamIDLobby.IsValid() ||
                    game.m_gameID.AppID() != SteamUtils.GetAppID()) continue;
                candidates[game.m_steamIDLobby.m_SteamID] = SteamFriends.GetFriendPersonaName(friend);
                SteamMatchmaking.RequestLobbyData(game.m_steamIDLobby);
            }
            FriendsStatus = "No compatible friends' lobbies found.";
            Changed?.Invoke();
        }

        private void DataChanged(LobbyDataUpdate_t message)
        {
            if (message.m_bSuccess == 0) return;
            var target = new CSteamID(message.m_ulSteamIDLobby);
            if (target == lobby && HasLobby && originalHost != steam.UserId)
            {
                string error = ConnectionError(target, out ulong host);
                if (error != null || host != originalHost) { session.Leave(error ?? "The original host has left the session."); return; }
            }
            if (!candidates.TryGetValue(message.m_ulSteamIDLobby, out string name)) return;
            Friends.RemoveAll(entry => entry.Id == message.m_ulSteamIDLobby);
            if (ConnectionError(target, out _) == null)
                Friends.Add((message.m_ulSteamIDLobby, $"{name} · {SteamMatchmaking.GetNumLobbyMembers(target)} / {SessionController.MultiplayerCapacity}"));
            FriendsStatus = Friends.Count == 0 ? "No compatible friends' lobbies found." : "Choose a lobby to join.";
            Changed?.Invoke();
        }

        private void MembershipChanged(LobbyChatUpdate_t message)
        {
            if (!HasLobby || message.m_ulSteamIDLobby != lobby.m_SteamID) return;
            var change = (EChatMemberStateChange)message.m_rgfChatMemberStateChange;
            if (message.m_ulSteamIDUserChanged == originalHost && change != EChatMemberStateChange.k_EChatMemberStateChangeEntered ||
                SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID != originalHost)
            { session.Leave("The host left the Steam lobby. The session has ended."); return; }
            if (message.m_ulSteamIDUserChanged == steam.UserId && change != EChatMemberStateChange.k_EChatMemberStateChangeEntered)
            { session.Leave("You left the Steam lobby. The session has ended."); return; }
            session.UpdateLobby();
        }

        internal void Leave()
        {
            if (Available && HasLobby)
            {
                if (originalHost == steam.UserId)
                {
                    SteamMatchmaking.SetLobbyData(lobby, "phase", "closing");
                    SteamMatchmaking.SetLobbyJoinable(lobby, false);
                }
                SteamMatchmaking.LeaveLobby(lobby);
            }
            lobby = default;
            originalHost = 0;
            Changed?.Invoke();
        }

        internal void Shutdown()
        {
            invite?.Dispose();
            membership?.Dispose();
            data?.Dispose();
            foreach (var call in pending) call.Dispose();
            pending.Clear();
            joining.Clear();
        }

        private void OnDestroy() => Shutdown();
    }
}
