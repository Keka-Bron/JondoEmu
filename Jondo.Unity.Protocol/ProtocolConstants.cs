namespace Jondo.Unity.Protocol
{
    public static class ProtocolConstants
    {
        public const int DefaultTurnTimeDeciseconds = 300; // 30 seconds
        public const int DefaultPlacementTimeSeconds = 45;
        public const int DefaultSubAreaId = 450;
        public const int DefaultSpellEffectUid = 41870;

        // ActionIds used in jtx messages
        public const int ActionIdSpellCast = 300;
        public const int ActionIdDamageVariation = 102;
        public const int ActionIdMovementVariation = 129;

        // StatIds used in jvm / lgk messages
        public const int StatIdLifePoints = 0;
        public const int StatIdAP = 1;
        public const int StatIdVitality = 11;
        public const int StatIdMP = 23;
        public const int StatIdInitiative = 44;
    }
}
