using System;
using System.Collections.Generic;
using NebulaAPI.DataStructures;
using NebulaAPI.GameState;
using NebulaAPI.Networking;
using NebulaAPI.Packets;

namespace NebulaModel.Networking;

public interface IServer : INetworkProvider
{
    ushort Port { get; set; }

    public ConcurrentPlayerCollection Players { get; }

    public void Update();

    public void Start();

    public void Stop();
    void Disconnect(INebulaConnection conn, DisconnectionReason reason, string reasonMessage = "");

    public void SendToPlayers<T>(IEnumerable<KeyValuePair<INebulaConnection, INebulaPlayer>> players, T packet) where T : class, new();
}
