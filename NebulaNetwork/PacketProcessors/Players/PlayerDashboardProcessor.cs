#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Players;

#endregion

namespace NebulaNetwork.PacketProcessors.Players;

[RegisterPacketProcessor]
public class PlayerDashboardProcessor : PacketProcessor<PlayerDashboardPacket>
{
    protected override void ProcessPacket(PlayerDashboardPacket packet, NebulaConnection conn)
    {
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player != null)
            {
                player.Data.DashboardData = packet.DashboardData;
            }
        }
        else
        {
            if (packet.DashboardData != null && packet.DashboardData.Length > 0 && GameMain.data?.statistics?.charts != null)
            {
                using var reader = new BinaryUtils.Reader(packet.DashboardData);
                GameMain.data.statistics.charts.Import(reader.BinaryReader);
                var dashboard = UIRoot.instance?.uiGame?.dashboard;
                if (dashboard != null)
                {
                    dashboard.DetermineCharts();
                    dashboard.UpdateCharts();
                }
            }
        }
    }
}
