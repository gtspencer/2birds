using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public sealed class GamePlayerSpawner : NetworkBehaviour
    {
        [SerializeField] private NetworkObject playerPrefab;
        [SerializeField] private Transform[] spawnPoints;
        private readonly Dictionary<int, (NetworkObject player, int slot)> players = new();
        private readonly HashSet<NetworkConnection> pending = new();
        private readonly SyncVar<int> playerCount = new();
        private uint sessionId;
        public int PlayerCount => playerCount.Value;

        public override void OnStartServer()
        {
            sessionId = SessionController.Instance.SessionId;
            ServerManager.OnRemoteConnectionState += RemoteState;
        }

        public override void OnSpawnServer(NetworkConnection connection)
        {
            if (!connection.LoadedStartScenes(true))
            {
                if (pending.Add(connection)) connection.OnLoadedStartScenes += StartScenesReady;
                return;
            }
            SpawnPlayer(connection);
        }

        private void StartScenesReady(NetworkConnection connection, bool asServer)
        {
            if (!asServer) return;
            connection.OnLoadedStartScenes -= StartScenesReady;
            pending.Remove(connection);
            SpawnPlayer(connection);
        }

        private void SpawnPlayer(NetworkConnection connection)
        {
            if (!IsServerInitialized || !connection.IsActive || !connection.IsAuthenticated ||
                gameObject.scene.name != "Game" || sessionId != SessionController.Instance.SessionId ||
                SessionController.Instance.Phase == SessionPhase.Stopping || players.ContainsKey(connection.ClientId)) return;
            for (int slot = 0; slot < spawnPoints.Length; slot++)
            {
                bool occupied = false;
                foreach (var entry in players.Values) occupied |= entry.slot == slot;
                if (occupied) continue;
                Transform marker = spawnPoints[slot];
                var player = Instantiate(playerPrefab, marker.position, marker.rotation);
                player.GetComponent<PlayerMotor>().SetSpawnPoint(marker.position);
                player.GetComponent<PlayerNetworkState>().Initialize((byte)slot);
                players.Add(connection.ClientId, (player, slot));
                ServerManager.Spawn(player, connection, gameObject.scene);
                playerCount.Value = players.Count;
                return;
            }
            connection.Disconnect(true);
        }

        private void RemoteState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;
            // FishNet destroys owned objects on disconnect; this registry only owns slot allocation.
            connection.OnLoadedStartScenes -= StartScenesReady;
            pending.Remove(connection);
            players.Remove(connection.ClientId);
            if (IsServerStarted) playerCount.Value = players.Count;
        }

        public override void OnStopServer()
        {
            ServerManager.OnRemoteConnectionState -= RemoteState;
            foreach (var connection in pending) connection.OnLoadedStartScenes -= StartScenesReady;
            pending.Clear();
            players.Clear();
        }
    }
}
