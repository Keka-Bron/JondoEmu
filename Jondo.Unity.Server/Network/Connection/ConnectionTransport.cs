using Google.Protobuf;
using Jondo.Unity.Protocol.Connection;

namespace Jondo.Unity.Server.Network.Connection;

internal static class ConnectionTransport
{
    internal static async Task RunAsync(Stream stream, byte[] firstPayload, ConnectionService service,
        IConnectionCodec codec, Func<bool> isRunning, Action<string> log)
    {
        byte[] payload = firstPayload;
        while (isRunning())
        {
            ConnectionMessage message;
            try
            {
                message = codec.Decode(payload);
            }
            catch (Exception ex) when (ex is InvalidProtocolBufferException or ConnectionConversionException)
            {
                log($"[Connection Server] Invalid connection message: {ex.Message}");
                return;
            }

            // Service/storage failures deliberately escape the decoding catch.
            switch (service.Handle(message))
            {
                case ConnectionAction.Close:
                    return;
                case ConnectionAction.Reply reply:
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, codec.Encode(reply.Response));
                    if (reply.CloseAfterReply) return;
                    break;
            }

            payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
            if (payload == null) return;
            GameServerProxy.LogTraffic("C->S", payload, payload.Length);
        }
    }
}
