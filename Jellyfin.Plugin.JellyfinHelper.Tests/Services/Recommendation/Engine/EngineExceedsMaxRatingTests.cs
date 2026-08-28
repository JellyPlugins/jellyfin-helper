using System;
using System.Reflection;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the private-static ExceedsMaxRating helper on Engine. BUG SURFACE: this is a SAFETY-CRITICAL guard that decides whether a candidate item is allowed to appear in a user's recommendations based on the user's MaxParentalRating.
/// </summary>
public sealed class EngineExceedsMaxRatingTests
{
    [Fact]
    public void ExceedsMaxRating_NullMaxRating_AlwaysReturnsFalse_EvenForHighRatedItems()
    {
        // BUG GUARD: adult users have no max-rating - the helper must not accidentally treat "null max" as "0 max" (which would exclude everything).
        var movie = new Movie();
        var ratingSeeded = TrySetInheritedRating(movie, 1000);
        Assert.True(ratingSeeded,
            "Could not seed InheritedParentalRatingValue via reflection - Jellyfin BaseItem API may have changed. " +
            "Check property name and backing field. Test aborted to avoid silent false-positive.");

        Assert.False(InvokeExceedsMaxRating(movie, null));
    }

    [Fact]
    public void ExceedsMaxRating_NullMaxRating_ItemWithNoRating_ReturnsFalse()
    {
        // Both null: no guard applies. Every candidate that has no rating and no user max must still pass through, otherwise the recommendation set collapses to zero for any deployment that hasn't tagged its library.
        var movie = new Movie();
        // Do NOT set InheritedParentalRatingValue - leave it null on purpose.
        Assert.False(InvokeExceedsMaxRating(movie, null));
    }

    [Fact]
    public void ExceedsMaxRating_UserHasMax_ItemHasNoRating_ReturnsTrue()
    {
        // SECURITY-CRITICAL BUG GUARD: an untagged / unrated item MUST NOT slip past a restricted profile filter.
        var movie = new Movie();
        // Deliberately do NOT set the rating.
        Assert.True(InvokeExceedsMaxRating(movie, 10));
    }

    [Fact]
    public void ExceedsMaxRating_ItemRatingBelowMax_ReturnsFalse()
    {
        var movie = new Movie();
        Assert.True(TrySetInheritedRating(movie, 5),
            "Could not seed InheritedParentalRatingValue via reflection - Jellyfin BaseItem API may have changed. " +
            "Check property name and backing field. Test aborted to avoid silent false-positive.");
        Assert.False(InvokeExceedsMaxRating(movie, 10));
    }

    [Fact]
    public void ExceedsMaxRating_ItemRatingEqualsMax_ReturnsFalse()
    {
        // BUG GUARD: the boundary is INCLUSIVE. A "PG-13" item under a PG-13 max must be allowed.
        var movie = new Movie();
        Assert.True(TrySetInheritedRating(movie, 13),
            "Could not seed InheritedParentalRatingValue via reflection - Jellyfin BaseItem API may have changed. " +
            "Check property name and backing field. Test aborted to avoid silent false-positive.");
        Assert.False(InvokeExceedsMaxRating(movie, 13));
    }

    [Fact]
    public void ExceedsMaxRating_ItemRatingOneAboveMax_ReturnsTrue()
    {
        // Complements the equals-max test above: the boundary rejects the very next tick up.
        var movie = new Movie();
        Assert.True(TrySetInheritedRating(movie, 14),
            "Could not seed InheritedParentalRatingValue via reflection - Jellyfin BaseItem API may have changed. " +
            "Check property name and backing field. Test aborted to avoid silent false-positive.");
        Assert.True(InvokeExceedsMaxRating(movie, 13));
    }

    [Fact]
    public void ExceedsMaxRating_ItemRatingFarAboveMax_ReturnsTrue()
    {
        var movie = new Movie();
        Assert.True(TrySetInheritedRating(movie, 100),
            "Could not seed InheritedParentalRatingValue via reflection - Jellyfin BaseItem API may have changed. " +
            "Check property name and backing field. Test aborted to avoid silent false-positive.");
        Assert.True(InvokeExceedsMaxRating(movie, 7));
    }

    [Fact]
    public void ExceedsMaxRating_ZeroMax_AllowsZeroRatedItem()
    {
        // Even the strictest profile (max=0) must allow a candidate with rating=0 through, because the max is inclusive.
        var movie = new Movie();
        Assert.True(TrySetInheritedRating(movie, 0),
            "Could not seed InheritedParentalRatingValue via reflection - Jellyfin BaseItem API may have changed. " +
            "Check property name and backing field. Test aborted to avoid silent false-positive.");
        Assert.False(InvokeExceedsMaxRating(movie, 0));
    }

    [Fact]
    public void ExceedsMaxRating_ZeroMax_RejectsAnyPositiveRating()
    {
        var movie = new Movie();
        Assert.True(TrySetInheritedRating(movie, 1),
            "Could not seed InheritedParentalRatingValue via reflection - Jellyfin BaseItem API may have changed. " +
            "Check property name and backing field. Test aborted to avoid silent false-positive.");
        Assert.True(InvokeExceedsMaxRating(movie, 0));
    }

    [Fact]
    public void ExceedsMaxRating_ZeroMax_RejectsUnratedItem()
    {
        // Even under max=0 the fail-safe "unrated -> reject" rule must apply. Otherwise the strictest possible profile paradoxically becomes MORE permissive than a mid-tier profile because unrated items always pass.
        var movie = new Movie();
        // No rating set -> InheritedParentalRatingValue is null.
        Assert.True(InvokeExceedsMaxRating(movie, 0));
    }

    // Reflection helpers: ExceedsMaxRating is `private static`, and InheritedParentalRatingValue on BaseItem may or may not be an object-initializer-settable property depending on the Jellyfin version.

    private static bool InvokeExceedsMaxRating(MediaBrowser.Controller.Entities.BaseItem candidate, int? maxRating)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "ExceedsMaxRating",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(MediaBrowser.Controller.Entities.BaseItem), typeof(int?)],
                modifiers: null);
        Assert.True(method is not null,
            "Could not find Engine.ExceedsMaxRating(BaseItem, int?) via reflection. " +
            "Check if the method was renamed or its signature (int?) was changed.");
        return (bool)method!.Invoke(null, [candidate, maxRating])!;
    }

    /// <summary>
    ///     Attempts to set InheritedParentalRatingValue on a BaseItem via property, then backing field.
    /// </summary>
    private static bool TrySetInheritedRating(MediaBrowser.Controller.Entities.BaseItem item, int value)
    {
        var type = item.GetType();
        // Walk up the inheritance chain - the property lives on BaseItem, not on Movie.
        while (type is not null)
        {
            var prop = type.GetProperty(
                "InheritedParentalRatingValue",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop is { CanWrite: true })
            {
                try
                {
                    prop.SetValue(item, (int?)value);
                    return true;
                }
                catch (Exception ex) when (ex is TargetException or TargetInvocationException
                                               or MethodAccessException or ArgumentException)
                {
                    // Property has a setter but it rejects the write (e.g. computed with side-effects).
                    // Fall through to the backing-field probe below.
                }
            }

            // Backing-field fallback: property is get-only, but the field is still writable via reflection.
            var field = type.GetField(
                "<InheritedParentalRatingValue>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                try
                {
                    field.SetValue(item, (int?)value);
                    return true;
                }
                catch (Exception ex) when (ex is FieldAccessException or ArgumentException
                                               or TargetException)
                {
                    // No luck - fall through and keep walking up.
                }
            }

            type = type.BaseType;
        }

        return false;
    }
}