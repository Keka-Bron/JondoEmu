using System.Globalization;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal static class EntryInformationMessages
{
    internal static TextInformationEvent InitialWarning()
        => new(TextInformationType.TextInformationError, InfoMessages.InitialEntryWarning, []);

    internal static TextInformationEvent OnlineMutualFriends(int count)
        => new(TextInformationType.TextInformationMessage, InfoMessages.OnlineMutualFriends,
            [count.ToString(CultureInfo.InvariantCulture)]);

    internal static TextInformationEvent? PreviousConnection(DatabaseManager.LastVisit? visit)
    {
        if (visit == null) return null;

        // Keep the stored offset's calendar components; this is the previous visit,
        // not the current connection or an item-effect date on Paris's clock.
        var when = visit.When;
        string[] parameters = [when.Year.ToString("D4", CultureInfo.InvariantCulture),
            when.Month.ToString("D2", CultureInfo.InvariantCulture),
            when.Day.ToString("D2", CultureInfo.InvariantCulture),
            when.Hour.ToString("D2", CultureInfo.InvariantCulture),
            when.Minute.ToString("D2", CultureInfo.InvariantCulture)];
        return string.IsNullOrWhiteSpace(visit.Ip)
            ? new(TextInformationType.TextInformationMessage, InfoMessages.LastConnection, parameters)
            : new(TextInformationType.TextInformationMessage, InfoMessages.LastConnectionWithIp, [.. parameters, visit.Ip]);
    }
}
