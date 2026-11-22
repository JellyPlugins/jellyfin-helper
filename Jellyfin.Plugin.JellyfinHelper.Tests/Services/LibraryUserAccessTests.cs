using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

public sealed class LibraryUserAccessTests
{
    [Fact]
    public void BoundedIdsPreserveRealHostTagPolicyAndThePreviouslyConfiguredLibraryScope()
    {
        var user = new User("viewer", "test", "test") { MaxParentalRatingScore = 12 };
        user.Preferences.Add(new Preference(PreferenceKind.BlockedTags, "blocked"));
        var visibleRoot = Guid.NewGuid();
        var hiddenRoot = Guid.NewGuid();
        var visibleCopy = Guid.NewGuid();
        var hiddenCopy = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var rows = new[] { (Id: visibleCopy, Root: visibleRoot, Tag: ""), (Id: hiddenCopy, Root: hiddenRoot, Tag: ""), (Id: blocked, Root: visibleRoot, Tag: "blocked") };
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(manager => manager.ConfigureUserAccess(It.IsAny<InternalItemsQuery>(), user)).Callback<InternalItemsQuery, User>((query, _) =>
        {
            // This is the 12.4 host's narrowing boundary. Its actual query constructor
            // supplies tags and parental restrictions; IDs must be attached afterward.
            Assert.Empty(query.ItemIds);
            Assert.Empty(query.AncestorIds);
            Assert.Contains("blocked", query.ExcludeInheritedTags);
            Assert.NotNull(query.MaxParentalRating);
            query.TopParentIds = [visibleRoot];
        });
        library.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => rows
            .Where(row => query.ItemIds.Contains(row.Id) && query.TopParentIds.Contains(row.Root) && !query.ExcludeInheritedTags.Contains(row.Tag)).Select(row => row.Id).ToArray());
        Assert.Equal(visibleCopy, Assert.Single(LibraryUserAccess.GetAllowedIds(library.Object, user, [hiddenCopy, visibleCopy, blocked])));
        library.VerifyAll();
    }

    [Fact]
    public void EmptyBoundedInputDoesNotEnumerateTheWholeLibrary()
    {
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        Assert.Empty(LibraryUserAccess.GetAllowedIds(library.Object, new User("viewer", "test", "test"), []));
        library.VerifyNoOtherCalls();
    }
}
