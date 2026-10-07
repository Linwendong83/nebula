#region

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using NebulaAPI.Networking;
using NebulaModel.Logger;
using NebulaModel.Networking.Serialization;
using WebSocketSharp;

#endregion

namespace NebulaModel.Networking;

public class NebulaConnection : INebulaConnection
{
    private readonly NebulaNetPacketProcessor packetProcessor;

    private readonly EndPoint peerEndpoint;

    // Temporarily public until refactor finished
    public WebSocket peerSocket { get; private set; }

    private readonly Queue<byte[]> pendingPackets = new();
    private bool enable = true;
    private EConnectionStatus connectionStatus = EConnectionStatus.Undefined;
    private static int nextConnectionId;

    public bool IsAlive => peerSocket?.IsAlive ?? false;

    public int Id { get; }

    public EConnectionStatus ConnectionStatus
    {
        get => connectionStatus;
        set
        {
            if (value < connectionStatus)
                throw new InvalidOperationException("Connection Status cannot be rolled back to a lower state.");

            connectionStatus = value;
        }
    }

    public NebulaConnection(WebSocket peerSocket, EndPoint peerEndpoint, NebulaNetPacketProcessor packetProcessor)
    {
        this.peerEndpoint = peerEndpoint;
        this.peerSocket = peerSocket;
        this.packetProcessor = packetProcessor;
        // Endpoint reuse must not make a reconnect equal to a socket awaiting cleanup.
        Id = Interlocked.Increment(ref nextConnectionId);
    }


    public void SendPacket<T>(T packet) where T : class, new()
    {
        lock (pendingPackets)
        {
            lock (packetProcessor)
            {
                var rawData = packetProcessor.Write(packet); //not-threadsafe
                pendingPackets.Enqueue(rawData);
            }
            ProcessPacketQueue();
        }
    }

    public void SendRawPacket(byte[] rawData)
    {
        lock (pendingPackets)
        {
            pendingPackets.Enqueue(rawData);
            ProcessPacketQueue();
        }
    }

    public bool Equals(INebulaConnection connection)
    {
        return ReferenceEquals(this, connection);
    }

    private void ProcessPacketQueue()
    {
        if (!enable || pendingPackets.Count <= 0)
        {
            return;
        }

        var packet = pendingPackets.Dequeue();
        if (peerSocket.ReadyState == WebSocketState.Open)
        {
            peerSocket.SendAsync(packet, OnSendCompleted);
            enable = false;
        }
        else
        {
            Log.Warn($"Cannot send packet to a {peerSocket.ReadyState} connection {peerEndpoint.GetHashCode()}");
        }
    }

    private void OnSendCompleted(bool result)
    {
        lock (pendingPackets)
        {
            enable = true;
            ProcessPacketQueue();
            Monitor.PulseAll(pendingPackets);
        }
    }

    /// <summary>Waits briefly for the final personal checkpoint before a graceful close.</summary>
    public bool FlushSendQueue(int timeoutMilliseconds = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        lock (pendingPackets)
        {
            while (!enable || pendingPackets.Count > 0)
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0 || !Monitor.Wait(pendingPackets, remaining)) return false;
            }
            return true;
        }
    }

    public static bool operator ==(NebulaConnection left, NebulaConnection right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(NebulaConnection left, NebulaConnection right)
    {
        return !Equals(left, right);
    }

    public override bool Equals(object obj)
    {
        return ReferenceEquals(this, obj);
    }

    public override int GetHashCode()
    {
        return Id;
    }
}
