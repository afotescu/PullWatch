namespace PullWatch;

public static class WowEvents
{
    // Mythic+
    public const string ChallengeModeStart = "CHALLENGE_MODE_START";
    public const string ChallengeModeEnd = "CHALLENGE_MODE_END";

    // Raid / Boss encounters
    public const string EncounterStart = "ENCOUNTER_START";
    public const string EncounterEnd = "ENCOUNTER_END";

    // World / Location
    public const string ZoneChange = "ZONE_CHANGE";
    public const string MapChange = "MAP_CHANGE";

    // Metadata
    public const string CombatLogVersion = "COMBAT_LOG_VERSION";
    public const string CombatantInfo = "COMBATANT_INFO";
}
