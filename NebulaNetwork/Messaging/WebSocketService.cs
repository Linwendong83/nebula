using System.Threading;
using NebulaAPI.Networking;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Networking.Serialization;
using NebulaModel.Utils;
using NebulaWorld;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace NebulaNetwork.Messaging;

public class WebSocketService : WebSocketBehavior
{
    public static Server Server;
    public static NebulaNetPacketProcessor PacketProcessor;
    private NebulaConnection connection;

    public WebSocketService() { }

    protected override void OnOpen()
    {
        if (Multiplayer.Session.IsGameLoaded == false && Multiplayer.Session.IsInLobby == false)
        {
            // Reject any connection that occurs while the host's game is loading.
            Context.WebSocket.Close((ushort)DisconnectionReason.HostStillLoading,
                "Host still loading, please try again later.".Translate());
            return;
        }

        Log.Info($"Client connected ID: {ID}");
        var conn = new NebulaConnection(Context.WebSocket, Context.UserEndPoint, PacketProcessor);
        Server.OnSocketConnection(conn);

        connection = conn;
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        // Find created NebulaConnection
        var conn = connection;
        if (conn != null)
        {
            PacketProcessor.EnqueuePacketForProcessing(e.RawData, conn);
        }
        else
        {
            Log.Warn($"Unregister socket {Context.UserEndPoint.GetHashCode()}");
        }
    }

    protected override void OnClose(CloseEventArgs e)
    {
        var departing = Interlocked.Exchange(ref connection, null);
        if (departing == null)
        {
            return;
        }

        // If the reason of a client disconnect is because we are still loading the game,
        // we don't need to inform the other clients since the disconnected client never
        // joined the game in the first place.
        if (e.Code == (short)DisconnectionReason.HostStillLoading)
        {
            return;
        }

        Log.Info($"Client disconnected: {ID}, reason: {e.Reason}");
        UnityDispatchQueue.RunOnMainThread(() =>
        {
            // This is to make sure that we don't try to deal with player disconnection
            // if it is because we have stopped the server and are not in a multiplayer game anymore.
            if (Multiplayer.IsActive)
            {
                Server.OnSocketDisconnection(departing);
            }
        });
    }

    protected override void OnError(ErrorEventArgs e)
    {
        var departing = Interlocked.Exchange(ref connection, null);
        if (departing == null)
        {
            return;
        }

        Log.Info($"Client disconnected because of an error: {ID}, reason: {e.Exception}");
        UnityDispatchQueue.RunOnMainThread(() =>
        {
            // This is to make sure that we don't try to deal with player disconnection
            // if it is because we have stopped the server and are not in a multiplayer game anymore.
            if (Multiplayer.IsActive)
            {
                Server.OnSocketDisconnection(departing);
            }
        });
    }
}
