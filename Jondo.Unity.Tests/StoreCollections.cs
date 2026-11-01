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
