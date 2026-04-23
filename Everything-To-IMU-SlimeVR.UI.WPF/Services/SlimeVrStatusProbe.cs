using System.Net.Sockets;

namespace Everything_To_IMU_SlimeVR.UI.Services;

public static class SlimeVrStatusProbe
{
    public static async Task<bool> IsUp()
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", 21110);
            var timeout = Task.Delay(800);
            var finished = await Task.WhenAny(connect, timeout);
            return finished == connect && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }
}
