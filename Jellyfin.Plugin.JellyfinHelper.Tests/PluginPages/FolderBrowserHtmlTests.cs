using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Tests for FolderBrowser.js (server-side folder picker for the trash-folder setting).
/// Covers: dialog structure, quick-jump library roots, breadcrumb, keyboard support,
/// path-selection flow, error handling, and re-entrancy safety.
/// </summary>
public class FolderBrowserHtmlTests : ConfigPageTestBase
{
    // === Top-level functions ===

    [Theory]
    [InlineData("function initFolderBrowser")]
    [InlineData("function openFolderBrowserDialog")]
    [InlineData("function loadLibraryPathsForBrowser")]
    [InlineData("function browseTo")]
    public void Html_ContainsFolderBrowserFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    // === Wire-up ===

    [Fact]
    public void Html_InitFolderBrowser_HooksBrowseButton()
    {
        Assert.Contains("btnBrowseTrash", HtmlContent);
    }

    [Fact]
    public void Html_InitFolderBrowser_ReadsCurrentPathFromTrashInput()
    {
        Assert.Matches(
            new Regex(@"function\s+openFolderBrowserDialog[\s\S]*?getElementById\(\s*['""]cfgTrashPath['""]"),
            HtmlContent);
    }

    // === Dialog structure ===

    [Fact]
    public void Html_FolderBrowser_UsesUniqueOverlayId()
    {
        Assert.Contains("folderBrowserOverlay", HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_HasBreadcrumbElement()
    {
        Assert.Contains("folderBrowserBreadcrumb", HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_HasQuickJumpElement()
    {
        Assert.Contains("folderBrowserQuickJump", HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_HasListingElement()
    {
        Assert.Contains("folderBrowserListing", HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_HasNewFolderNameInput()
    {
        Assert.Contains("folderBrowserNewName", HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_HasCloseCancelSelectButtons()
    {
        Assert.Contains("folderBrowserClose", HtmlContent);
        Assert.Contains("folderBrowserCancel", HtmlContent);
        Assert.Contains("folderBrowserSelect", HtmlContent);
    }

    // === Accessibility (role/aria) ===

    [Fact]
    public void Html_FolderBrowser_DialogHasRoleAndAriaModal()
    {
        // dialog.setAttribute('role', 'dialog') and aria-modal=true
        Assert.Matches(new Regex(@"setAttribute\(\s*['""]role['""]\s*,\s*['""]dialog['""]"), HtmlContent);
        Assert.Matches(new Regex(@"setAttribute\(\s*['""]aria-modal['""]\s*,\s*['""]true['""]"), HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_TitleHasLabelledBy()
    {
        Assert.Contains("folderBrowserTitle", HtmlContent);
        Assert.Matches(new Regex(@"setAttribute\(\s*['""]aria-labelledby['""]\s*,\s*['""]folderBrowserTitle['""]"), HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_SupportsEscapeKeyToClose()
    {
        // Escape key handler is scoped to overlay
        Assert.Matches(
            new Regex(@"function\s+openFolderBrowserDialog[\s\S]*?e\.key\s*===\s*['""]Escape['""]"),
            HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_DirectoryItems_KeyboardOperable()
    {
        // Each folder-browser-item gets tabindex, role=button, Enter/Space handling
        Assert.Matches(
            new Regex(@"function\s+browseTo[\s\S]*?setAttribute\(\s*['""]tabindex['""]\s*,\s*['""]0['""]"),
            HtmlContent);
        Assert.Matches(
            new Regex(@"function\s+browseTo[\s\S]*?setAttribute\(\s*['""]role['""]\s*,\s*['""]button['""]"),
            HtmlContent);
    }

    // === Backend endpoints ===

    [Fact]
    public void Html_FolderBrowser_LoadsLibraryPathsFromServer()
    {
        Assert.Contains("JellyfinHelper/Configuration/LibraryPaths", HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_UsesBrowseFoldersEndpoint()
    {
        Assert.Contains("JellyfinHelper/Configuration/BrowseFolders", HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_EncodesPathQueryParameter()
    {
        // Path must be encodeURIComponent'd to avoid injection / broken paths
        Assert.Matches(
            new Regex(@"function\s+browseTo[\s\S]*?encodeURIComponent\(\s*path\s*\)"),
            HtmlContent);
    }

    // === Race-condition guard ===

    [Fact]
    public void Html_FolderBrowser_UsesRequestIdForRaceProtection()
    {
        // Simultaneous navigation clicks would corrupt state without this guard.
        Assert.Matches(
            new Regex(@"function\s+browseTo[\s\S]*?state\.requestId"),
            HtmlContent);
    }

    // === Path selection semantics ===

    [Fact]
    public void Html_FolderBrowser_AppendsNewFolderNameToCurrentPath()
    {
        Assert.Matches(
            new Regex(@"function\s+openFolderBrowserDialog[\s\S]*?selectedPath\s*\+=\s*newName\.trim"),
            HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_ChoosesPathSeparatorFromCurrentPath()
    {
        // Windows vs. Unix separator inferred from existing path
        Assert.Matches(new Regex(@"selectedPath\.(indexOf|includes)\(\s*['""]/['""]"), HtmlContent);
        Assert.Matches(new Regex(@"selectedPath\.(indexOf|includes)\(\s*['""]\\\\['""]"), HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_ValidatesPathBeforeSaving()
    {
        Assert.Matches(
            new Regex(@"function\s+openFolderBrowserDialog[\s\S]*?validateTrashPath\("),
            HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_ClosesDialogBeforeShowingRelocationPrompt()
    {
        // Closing first ensures any subsequent dialog appears in foreground.
        Assert.Matches(
            new Regex(@"function\s+openFolderBrowserDialog[\s\S]*?closeDialog\(\)[\s\S]*?doSaveSettings"),
            HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_DispatchesInputEventAfterSelection()
    {
        // Simulates user typing so downstream change-listeners run
        Assert.Matches(
            new Regex(@"new Event\(\s*['""]input['""]\s*,\s*\{\s*bubbles\s*:\s*true"),
            HtmlContent);
    }

    // === Absolute path detection ===

    [Fact]
    public void Html_FolderBrowser_DetectsUnixAbsolutePath()
    {
        Assert.Matches(new Regex(@"currentPath\.startsWith\(\s*['""]/['""]"), HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_DetectsUncAbsolutePath()
    {
        // Source is currentPath.startsWith('\\\\') which is 4 literal backslashes
        // between the quotes in the compiled HTML. In a C# regex literal that becomes 8 backslashes.
        Assert.Matches(new Regex(@"currentPath\.startsWith\(\s*['""]\\\\\\\\['""]"), HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_DetectsWindowsDriveAbsolutePath()
    {
        // Source regex: /^[A-Za-z]:[\\/]/
        Assert.Matches(new Regex(@"\[A-Za-z\]:\[\\\\/\]"), HtmlContent);
    }

    // === Error handling ===

    [Fact]
    public void Html_FolderBrowser_ClearsSelectionOnServerError()
    {
        // Prevents "Select This Folder" from persisting an inaccessible path.
        Assert.Matches(
            new Regex(@"function\s+browseTo[\s\S]*?state\.currentPath\s*=\s*null"),
            HtmlContent);
    }

    [Fact]
    public void Html_FolderBrowser_ShowsErrorMessageOnFailure()
    {
        Assert.Contains("trashBrowseError", HtmlContent);
    }

    // === i18n keys ===

    [Theory]
    [InlineData("trashBrowseTitle")]
    [InlineData("trashBrowseCreateNew")]
    [InlineData("trashBrowseSelect")]
    [InlineData("trashBrowseLibraryRoots")]
    [InlineData("trashBrowseLoading")]
    [InlineData("trashBrowseCurrentPath")]
    [InlineData("trashBrowseGoUp")]
    [InlineData("trashBrowseEmpty")]
    [InlineData("trashBrowseError")]
    public void Html_FolderBrowser_UsesI18nKey(string key)
    {
        Assert.Contains(key, HtmlContent);
    }

    // === Success feedback on browse button ===

    [Fact]
    public void Html_FolderBrowser_ShowsSuccessFeedbackAfterSave()
    {
        Assert.Matches(
            new Regex(@"function\s+openFolderBrowserDialog[\s\S]*?btnBrowseTrash[\s\S]*?check_circle"),
            HtmlContent);
    }
}