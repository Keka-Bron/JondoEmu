namespace Jondo.Unity.Protocol
{
    /// <summary>
    /// Centralized registry for network opcode URIs in Dofus 3.6.10.10.
    /// Decouples 3-letter opcode strings from server logic to allow seamless version migration.
    /// </summary>
    public static class OpcodeRegistry
    {
        public const string UriPrefix = "type.ankama.com/";

        // Authentication & Server Selection (3.6.10.10)
        public const string ServerSelectionRequest = "kqz";
        public const string ServerSelectionToken = "krt";
        public const string ServerSelectionResponse = "kra";
        
        // Game Server Node Handshake & Character Selection (3.6.10.10)
        public const string GameServerHello = "hoy";
        public const string CharacterListRequest = "kqu";
        public const string CharactersListMessage = "kvi";
        public const string SelectCharacterRequest = "kvw";
        public const string SelectCharacterSuccess = "kva";

        // Map and Navigation (3.6.10.10)
        public const string MapComplementaryInformation = "jru";
        public const string MapChangeRequest = "lqu";
        public const string MapChangeFinish = "lva";
        public const string MapInformationsRequest = "iom";
        public const string MapMovementRequest = "joi";
        public const string MapMovementConfirm = "joo";

        // Combat Engine (3.6.10.10)
        public const string GameFightStarting = "kml";
        public const string GameFightJoin = "kmp";
        public const string GameFightPlacementPossiblePositions = "kub";
        public const string GameFightShowFighter = "kae";
        public const string GameFightTurnStart = "jwo";
        public const string GameFightTurnStartPlaying = "jox";
        public const string GameFightTurnReady = "jwe";
        public const string GameFightTurnEnd = "jxw";
        public const string GameFightTurnList = "jxe";
        public const string GameFightMovement = "joo";
        public const string GameFightSpellCast = "jtx";
        public const string GameFightSequenceStart = "jud";
        public const string GameFightSequenceEnd = "juc";
        public const string GameFightPointsVariation = "jvm";
        public const string GameFightEnd = "kme";

        /// <summary>
        /// Returns the full URI string for a 3-letter opcode.
        /// </summary>
        public static string GetUri(string opcode)
        {
            return UriPrefix + opcode;
        }
    }
}
