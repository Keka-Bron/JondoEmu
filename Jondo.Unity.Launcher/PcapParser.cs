using System;
using System.IO;
using System.Collections.Generic;
using Google.Protobuf;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher
{
    public class PcapParser
    {
        public static void Parse(string pcapPath)
        {
            if (!File.Exists(pcapPath))
            {
                Console.WriteLine($"File not found: {pcapPath}");
                return;
            }

            Console.WriteLine($"Parsing PCAPNG file: {pcapPath}");
            using var fs = File.OpenRead(pcapPath);
            using var br = new BinaryReader(fs);

            // Dictionary to store TCP streams by (SrcIP, SrcPort, DstIP, DstPort)
            var streams = new Dictionary<string, MemoryStream>();

            try
            {
                while (fs.Position < fs.Length)
                {
                    uint blockType = br.ReadUInt32();
                    uint blockLength = br.ReadUInt32();
                    long blockStart = fs.Position - 8;
                    long blockEnd = blockStart + blockLength;

                    if (blockType == 0x00000006) // Enhanced Packet Block
                    {
                        uint interfaceId = br.ReadUInt32();
                        uint tsHigh = br.ReadUInt32();
                        uint tsLow = br.ReadUInt32();
                        uint capLen = br.ReadUInt32();
                        uint origLen = br.ReadUInt32();

                        byte[] packetData = br.ReadBytes((int)capLen);

                        // Parse Ethernet Frame
                        if (packetData.Length >= 14)
                        {
                            ushort etherType = (ushort)((packetData[12] << 8) | packetData[13]);
                            if (etherType == 0x0800) // IPv4
                            {
                                int ipStart = 14;
                                if (packetData.Length >= ipStart + 20)
                                {
                                    int ipHeaderLen = (packetData[ipStart] & 0x0F) * 4;
                                    byte protocol = packetData[ipStart + 9];

                                    string srcIp = $"{packetData[ipStart + 12]}.{packetData[ipStart + 13]}.{packetData[ipStart + 14]}.{packetData[ipStart + 15]}";
                                    string dstIp = $"{packetData[ipStart + 16]}.{packetData[ipStart + 17]}.{packetData[ipStart + 18]}.{packetData[ipStart + 19]}";

                                    if (protocol == 6 && packetData.Length >= ipStart + ipHeaderLen + 20) // TCP
                                    {
                                        int tcpStart = ipStart + ipHeaderLen;
                                        ushort srcPort = (ushort)((packetData[tcpStart] << 8) | packetData[tcpStart + 1]);
                                        ushort dstPort = (ushort)((packetData[tcpStart + 2] << 8) | packetData[tcpStart + 3]);

                                        int tcpHeaderLen = ((packetData[tcpStart + 12] >> 4) & 0x0F) * 4;
                                        int payloadStart = tcpStart + tcpHeaderLen;
                                        int payloadLen = packetData.Length - payloadStart;

                                        if (payloadLen > 0 && (srcPort == 5555 || dstPort == 5555 || srcPort == 5556 || dstPort == 5556))
                                        {
                                            byte[] payload = new byte[payloadLen];
                                            Array.Copy(packetData, payloadStart, payload, 0, payloadLen);

                                            string key = $"{srcIp}:{srcPort} -> {dstIp}:{dstPort}";
                                            if (!streams.ContainsKey(key))
                                            {
                                                streams[key] = new MemoryStream();
                                            }
                                            streams[key].Write(payload, 0, payload.Length);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Seek to next block (must be aligned to 32 bits, which blockLength guarantees)
                    fs.Position = blockEnd;
                }
            }
            catch (EndOfStreamException)
            {
                // Reached end of file
            }

            Console.WriteLine($"\nFound {streams.Count} TCP streams related to ports 5555/5556:");
            foreach (var kvp in streams)
            {
                string key = kvp.Key;
                byte[] data = kvp.Value.ToArray();
                Console.WriteLine($"Stream {key}: {data.Length} bytes");

                // Parse protobuf messages in this stream
                ParseProtobufStream(data);
                Console.WriteLine(new string('-', 50));
            }
        }

        public static void ExtractWorldPackets(string pcapPath, string outputPath)
        {
            if (!File.Exists(pcapPath))
            {
                Console.WriteLine($"[-] PCAP file not found: {pcapPath}");
                return;
            }

            Console.WriteLine($"[+] Extracting world packets from: {pcapPath}");
            using var fs = File.OpenRead(pcapPath);
            using var br = new BinaryReader(fs);

            var streams = new Dictionary<string, MemoryStream>();

            try
            {
                while (fs.Position < fs.Length)
                {
                    uint blockType = br.ReadUInt32();
                    uint blockLength = br.ReadUInt32();
                    long blockStart = fs.Position - 8;
                    long blockEnd = blockStart + blockLength;

                    if (blockType == 0x00000006) // Enhanced Packet Block
                    {
                        uint interfaceId = br.ReadUInt32();
                        uint tsHigh = br.ReadUInt32();
                        uint tsLow = br.ReadUInt32();
                        uint capLen = br.ReadUInt32();
                        uint origLen = br.ReadUInt32();

                        byte[] packetData = br.ReadBytes((int)capLen);

                        if (packetData.Length >= 14)
                        {
                            ushort etherType = (ushort)((packetData[12] << 8) | packetData[13]);
                            if (etherType == 0x0800) // IPv4
                            {
                                int ipStart = 14;
                                if (packetData.Length >= ipStart + 20)
                                {
                                    int ipHeaderLen = (packetData[ipStart] & 0x0F) * 4;
                                    byte protocol = packetData[ipStart + 9];

                                    string srcIp = $"{packetData[ipStart + 12]}.{packetData[ipStart + 13]}.{packetData[ipStart + 14]}.{packetData[ipStart + 15]}";
                                    string dstIp = $"{packetData[ipStart + 16]}.{packetData[ipStart + 17]}.{packetData[ipStart + 18]}.{packetData[ipStart + 19]}";

                                    if (protocol == 6 && packetData.Length >= ipStart + ipHeaderLen + 20) // TCP
                                    {
                                        int tcpStart = ipStart + ipHeaderLen;
                                        ushort srcPort = (ushort)((packetData[tcpStart] << 8) | packetData[tcpStart + 1]);
                                        ushort dstPort = (ushort)((packetData[tcpStart + 2] << 8) | packetData[tcpStart + 3]);

                                        int tcpHeaderLen = ((packetData[tcpStart + 12] >> 4) & 0x0F) * 4;
                                        int payloadStart = tcpStart + tcpHeaderLen;
                                        int payloadLen = packetData.Length - payloadStart;

                                        if (payloadLen > 0 && (srcPort == 5555 || dstPort == 5555))
                                        {
                                            byte[] payload = new byte[payloadLen];
                                            Array.Copy(packetData, payloadStart, payload, 0, payloadLen);

                                            string key = $"{srcIp}:{srcPort} -> {dstIp}:{dstPort}";
                                            if (!streams.ContainsKey(key))
                                            {
                                                streams[key] = new MemoryStream();
                                            }
                                            streams[key].Write(payload, 0, payload.Length);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    fs.Position = blockEnd;
                }
            }
            catch (EndOfStreamException) { }

            byte[] targetData = null;
            foreach (var kvp in streams)
            {
                byte[] data = kvp.Value.ToArray();
                if (data.Length > 100)
                {
                    string ascii = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 100));
                    if (ascii.Contains(Op.Uri(Op.Kof)))
                    {
                        targetData = data;
                        Console.WriteLine($"[+] Found target Game Node stream: {kvp.Key} ({data.Length} bytes)");
                        break;
                    }
                }
            }

            if (targetData == null)
            {
                Console.WriteLine("[-] Target Game Node stream not found in PCAP!");
                return;
            }

            using var ms = new MemoryStream(targetData);
            using var outFs = File.Create(outputPath);
            int msgIndex = 1;

            while (ms.Position < ms.Length)
            {
                int nextByte = ms.ReadByte();
                if (nextByte == -1) break;
                if (nextByte == 0)
                {
                    continue;
                }
                ms.Position--;

                long startPos = ms.Position;
                int length = 0;
                int shift = 0;
                bool success = true;
                var lenBytes = new System.Collections.Generic.List<byte>();

                while (true)
                {
                    int b = ms.ReadByte();
                    if (b == -1)
                    {
                        success = false;
                        break;
                    }
                    lenBytes.Add((byte)b);
                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }

                if (!success || length <= 0 || ms.Position + length > ms.Length)
                {
                    break;
                }

                byte[] payload = new byte[length];
                ms.Read(payload, 0, length);

                if (msgIndex >= 15)
                {
                    outFs.Write(lenBytes.ToArray(), 0, lenBytes.Count);
                    outFs.Write(payload, 0, payload.Length);
                }

                msgIndex++;
            }

            Console.WriteLine($"[+] Extracted {msgIndex - 15} world entering packets successfully to: {outputPath}");
        }


        private static void ParseProtobufStream(byte[] data)
        {
            using var ms = new MemoryStream(data);
            int msgIndex = 1;

            while (ms.Position < ms.Length)
            {
                int nextByte = ms.ReadByte();
                if (nextByte == -1) break;
                if (nextByte == 0)
                {
                    continue;
                }
                ms.Position--;

                long startPos = ms.Position;
                // Read VarInt length
                int length = 0;
                int shift = 0;
                bool success = true;

                while (true)
                {
                    int b = ms.ReadByte();
                    if (b == -1)
                    {
                        success = false;
                        break;
                    }

                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }

                if (!success || length <= 0 || ms.Position + length > ms.Length)
                {
                    // If parsing failed or length is invalid, try scanning byte-by-byte or abort
                    break;
                }

                byte[] payload = new byte[length];
                ms.Read(payload, 0, length);

                Console.WriteLine($"  Msg #{msgIndex} (Offset {startPos}, Len {length} bytes):");
                Console.WriteLine($"    Hex: {BitConverter.ToString(payload)}");
                try
                {
                    var msg = Jondo.Protocol.GameMessage.Parser.ParseFrom(payload);
                    Console.WriteLine($"    Parsed successfully as GameMessage!");
                    Console.WriteLine($"    Content:\n{msg}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed to parse as GameMessage: {ex.Message}");
                }
                msgIndex++;
            }
        }
    }
}
