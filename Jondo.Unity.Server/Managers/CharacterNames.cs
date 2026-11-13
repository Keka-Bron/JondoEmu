using System.Text.RegularExpressions;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a character may be called: rule 1 of the client's NamingRules
    /// (data_assets_namingrulesdataroot), from 2 to 20 characters, a capital, lower case letters
    /// and up to two more pieces after a hyphen.
    /// </summary>
    /// <remarks>
    /// The client checks it before it sends a creation (kvz) or a rename (lvw) and greys its
    /// button otherwise; the server checks it again, because a message does not have to come from
    /// that client. <c>\z</c> and not <c>$</c>, which in .NET also lets a final line break through.
    /// </remarks>
    public static class CharacterNames
    {
        public const int MinLength = 2;
        public const int MaxLength = 20;

        private static readonly Regex Rule =
            new(@"^[A-Z][a-z]+(\-[a-zA-Z][a-z]*){0,2}\z", RegexOptions.CultureInvariant);

        public static bool IsValid(string? name)
            => name != null && name.Length >= MinLength && name.Length <= MaxLength && Rule.IsMatch(name);
    }
}
