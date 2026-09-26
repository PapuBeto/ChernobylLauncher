using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChernobylZLauncher.Core.Server;

public class ServerStatusResult
{
    public bool IsOnline { get; set; }
    public int PlayersOnline { get; set; }
    public int MaxPlayers { get; set; }
    public string? Motd { get; set; }
    public string? VersionName { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ServerStatusService
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;

    private const int ProtocolVersion = 763;

    public ServerStatusService(string host, int port = 25565, int timeoutMs = 5000)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public async Task<ServerStatusResult> CheckStatusAsync()
    {
        var result = new ServerStatusResult();

        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(_host, _port);
            var timeoutTask = Task.Delay(_timeoutMs);

            var completed = await Task.WhenAny(connectTask, timeoutTask);
            if (completed == timeoutTask || !client.Connected)
            {
                result.IsOnline = false;
                result.ErrorMessage = "Timeout al conectar";
                return result;
            }

            using var stream = client.GetStream();

            await SendHandshakeAsync(stream);
            await SendStatusRequestAsync(stream);

            var json = await ReadStatusResponseAsync(stream);
            ParseStatusJson(json, result);

            result.IsOnline = true;
        }
        catch (Exception ex)
        {
            result.IsOnline = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task SendHandshakeAsync(NetworkStream stream)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, 0x00);
        WriteVarInt(packet, ProtocolVersion);
        WriteString(packet, _host);
        WriteUShort(packet, (ushort)_port);
        WriteVarInt(packet, 1);

        await WritePacketAsync(stream, packet);
    }

    private async Task SendStatusRequestAsync(NetworkStream stream)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, 0x00);

        await WritePacketAsync(stream, packet);
    }

    private async Task<string> ReadStatusResponseAsync(NetworkStream stream)
    {
        await ReadVarIntAsync(stream);
        var packetId = await ReadVarIntAsync(stream);

        if (packetId != 0x00)
        {
            throw new InvalidOperationException("Paquete inesperado recibido del servidor");
        }

        var jsonLength = await ReadVarIntAsync(stream);
        var buffer = new byte[jsonLength];
        var read = 0;

        while (read < jsonLength)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(read, jsonLength - read));
            if (bytesRead == 0)
            {
                throw new InvalidOperationException("Conexion cerrada antes de terminar de leer la respuesta");
            }

            read += bytesRead;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private void ParseStatusJson(string json, ServerStatusResult result)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("players", out var players))
        {
            result.PlayersOnline = players.TryGetProperty("online", out var online) ? online.GetInt32() : 0;
            result.MaxPlayers = players.TryGetProperty("max", out var max) ? max.GetInt32() : 0;
        }

        if (root.TryGetProperty("version", out var version) && version.TryGetProperty("name", out var versionName))
        {
            result.VersionName = versionName.GetString();
        }

        if (root.TryGetProperty("description", out var description))
        {
            if (description.ValueKind == JsonValueKind.String)
            {
                result.Motd = description.GetString();
            }
            else if (description.TryGetProperty("text", out var text))
            {
                result.Motd = text.GetString();
            }
        }
    }

    private static async Task WritePacketAsync(NetworkStream stream, MemoryStream packetData)
    {
        using var final = new MemoryStream();
        WriteVarInt(final, (int)packetData.Length);
        packetData.Position = 0;
        await packetData.CopyToAsync(final);

        var bytes = final.ToArray();
        await stream.WriteAsync(bytes);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;

        while (true)
        {
            var temp = (byte)(unsigned & 0b0111_1111);
            unsigned >>= 7;

            if (unsigned != 0)
            {
                temp |= 0b1000_0000;
            }

            stream.WriteByte(temp);

            if (unsigned == 0)
            {
                break;
            }
        }
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream)
    {
        var numRead = 0;
        var result = 0;
        var singleByteBuffer = new byte[1];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(singleByteBuffer.AsMemory(0, 1));
            if (bytesRead == 0)
            {
                throw new InvalidOperationException("Conexion cerrada al leer VarInt");
            }

            var readByte = singleByteBuffer[0];
            var value = readByte & 0b0111_1111;
            result |= value << (7 * numRead);

            numRead++;
            if (numRead > 5)
            {
                throw new InvalidOperationException("VarInt demasiado largo");
            }

            if ((readByte & 0b1000_0000) == 0)
            {
                break;
            }
        }

        return result;
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUShort(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }
}