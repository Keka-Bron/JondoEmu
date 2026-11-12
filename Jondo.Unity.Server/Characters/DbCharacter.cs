namespace Jondo.Unity.Server;

public class DbCharacter
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public int ServerId { get; set; } = DatabaseManager.DefaultServerId;
    public string Name { get; set; }
    public int Breed { get; set; }
    public int Sex { get; set; }
    public int Level { get; set; }
    public CharacterColors Colors { get; set; }
    /// <summary>ISO date of the last connection, empty if the character never logged in.</summary>
    public string LastConnection { get; set; } = "";
    /// <summary>Head chosen at creation. Its skin is part of the look.</summary>
    public int HeadId { get; set; }
}
