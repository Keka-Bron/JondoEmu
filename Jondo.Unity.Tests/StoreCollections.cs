using Xunit;

namespace Jondo.Unity.Tests;

// These collections redirect process-wide store connection strings to temporary databases.
// Other collections also read the stores while building characters and session packets, so
// serializing only the writers within each collection is insufficient. On Linux an unrelated
// reader can retain a pooled connection to a database that a writer's teardown has unlinked.
[CollectionDefinition("alliances", DisableParallelization = true)]
public sealed class AllianceStoreCollection { }

[CollectionDefinition("guild raids", DisableParallelization = true)]
public sealed class GuildStoreCollection { }

// These tests add spawns straight into the NPC tables. Any test elsewhere that calls
// Npcs.Initialize publishes fresh tables and drops them mid-test, so the spawn never shows
// and the read of its frame times out. Running alone, nothing reloads the NPCs under them;
// they also stay apart from the MapManager collection whose maps they use.
[CollectionDefinition("npc tables", DisableParallelization = true)]
public sealed class NpcTablesCollection { }
