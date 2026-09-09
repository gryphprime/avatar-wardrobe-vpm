using System;
using System.Collections.Generic;
using System.Linq;
namespace OutfitToggleGenerator
{
    internal static class WardrobeEditPolicy
    {
        internal static bool ContextMatches(string session, string expectedSession, int avatar, int expectedAvatar) =>
            !string.IsNullOrEmpty(expectedSession) && session == expectedSession && avatar != 0 && avatar == expectedAvatar;
        internal static T ExactInstance<T>(IEnumerable<T> candidates, string expected, Func<T, int> identity) where T : class =>
            string.IsNullOrEmpty(expected) ? null : candidates.SingleOrDefault(item => identity(item).ToString() == expected);
        internal static bool FitMatches(string savedAsset, string asset, string savedAvatar, string avatar, string savedProfile, string profile) =>
            !string.IsNullOrEmpty(asset) && !string.IsNullOrEmpty(avatar) && !string.IsNullOrEmpty(profile) &&
            savedAsset == asset && savedAvatar == avatar && savedProfile == profile;
    }
}
