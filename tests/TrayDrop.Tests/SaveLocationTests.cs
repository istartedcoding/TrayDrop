using TrayDrop.Core;

namespace TrayDrop.Tests;

[TestClass]
public sealed class SaveLocationTests
{
    [TestMethod]
    public void FirstRunUsesTheProvidedDownloadsFolder()
    {
        using var directory = new TestDirectory();
        var downloads = directory.FilePath("Redirected Downloads");
        var settings = new SaveLocationStore(directory.Path, downloads);
        Assert.AreEqual(downloads, settings.CurrentDirectory);
        Assert.IsTrue(settings.IsDefault);
        Assert.IsFalse(File.Exists(settings.SettingsPath));
    }

    [TestMethod]
    public void CustomFolderPersistsAndResetFollowsWindowsDefault()
    {
        using var directory = new TestDirectory();
        var downloads = directory.FilePath("Downloads");
        var custom = directory.FilePath("Custom folder");
        var settings = new SaveLocationStore(directory.Path, downloads);
        settings.Save(custom);
        var loaded = new SaveLocationStore(directory.Path, downloads);
        Assert.AreEqual(custom, loaded.CurrentDirectory);
        Assert.IsFalse(loaded.IsDefault);
        loaded.Save(null);
        var redirectedDownloads = directory.FilePath("New Downloads");
        var reset = new SaveLocationStore(directory.Path, redirectedDownloads);
        Assert.IsTrue(reset.IsDefault);
        Assert.AreEqual(redirectedDownloads, reset.CurrentDirectory);
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "settings-*.tmp"));
    }

    [TestMethod]
    public void InvalidSettingsAreReportedInsteadOfSilentlyResetting()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("settings.json"), "{invalid");
        Assert.Throws<InvalidDataException>(() => new SaveLocationStore(directory.Path, directory.FilePath("Downloads")));
        File.WriteAllText(directory.FilePath("settings.json"), "{\"receiveDirectory\":\"relative-folder\"}");
        Assert.Throws<InvalidDataException>(() => new SaveLocationStore(directory.Path, directory.FilePath("Downloads")));
    }

    [TestMethod]
    public void FailedSavePreservesPreviousSettingsAndRemovesTemporaryFile()
    {
        using var directory = new TestDirectory();
        var downloads = directory.FilePath("Downloads");
        var settings = new SaveLocationStore(directory.Path, downloads);
        Directory.CreateDirectory(settings.SettingsPath);
        Assert.Throws<UnauthorizedAccessException>(() => settings.Save(directory.FilePath("Custom")));
        Assert.AreEqual(downloads, settings.CurrentDirectory);
        Assert.IsTrue(settings.IsDefault);
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "settings-*.tmp"));
    }
}
