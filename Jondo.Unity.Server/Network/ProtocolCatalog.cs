using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Versioned, data-only packet disposition catalogue. It controls telemetry classification,
    /// never protocol mutations: a packet still needs a compiled, evidence-backed handler before
    /// it can change account, character, map, quest, or fight state.
    /// </summary>
    public static class ProtocolCatalog
    {
        private static readonly object Sync = new();
        private static HashSet<string> _knownNoReply = new(StringComparer.Ordinal);
        private static HashSet<string> _legacyObservationOnly = new(StringComparer.Ordinal);
        private static bool _loaded;

        public static void Reload()
        {
            lock (Sync)
            {
                var noReply = new HashSet<string>(StringComparer.Ordinal);
                var legacy = new HashSet<string>(StringComparer.Ordinal);
                string path = Paths.ProtocolPacketPolicyJson;
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement version) ||
                        version.GetString() != Paths.ActiveClientDataVersion)
                        throw new InvalidOperationException("packet policy version does not match the active client-data snapshot.");
                    ReadOpcodes(document.RootElement, "knownNoReplyC2S", noReply);
                    ReadOpcodes(document.RootElement, "legacyObservationOnlyC2S", legacy);
                    _knownNoReply = noReply;
                    _legacyObservationOnly = legacy;
                    _loaded = true;
                    Console.WriteLine($"[ProtocolCatalog] Loaded {noReply.Count} no-reply and {legacy.Count} observation-only C2S policies from {path}.");
                }
                catch (Exception ex)
                {
                    _knownNoReply = noReply;
                    _legacyObservationOnly = legacy;
                    _loaded = true;
                    Console.WriteLine($"[ProtocolCatalog] Rejected packet policy ({ex.Message}); no packet will be silently classified.");
                }
            }
        }

        public static bool IsKnownNoReply(string payload)
        {
            if (!_loaded) Reload();
            return Contains(_knownNoReply, payload);
        }

        public static bool IsLegacyObservationOnly(string payload)
        {
            if (!_loaded) Reload();
            return Contains(_legacyObservationOnly, payload);
        }

        private static bool Contains(HashSet<string> opcodes, string payload)
        {
            foreach (string opcode in opcodes)
                if (payload.Contains("type.ankama.com/" + opcode, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void ReadOpcodes(JsonElement root, string name, HashSet<string> target)
        {
            if (!root.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"missing {name} array.");
            foreach (JsonElement value in values.EnumerateArray())
            {
                string? opcode = value.GetString();
                if (string.IsNullOrWhiteSpace(opcode) || opcode.Length > 16 || !IsAsciiIdentifier(opcode) || !target.Add(opcode))
                    throw new InvalidOperationException($"invalid or duplicate opcode in {name}.");
            }
        }

        private static bool IsAsciiIdentifier(string value)
        {
            foreach (char c in value)
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return false;
            return true;
        }
    }
}
