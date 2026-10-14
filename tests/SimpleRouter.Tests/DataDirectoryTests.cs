using SimpleRouter.Tests.Support;

namespace SimpleRouter.Tests;

public class DataDirectoryTests
{
    [Fact]
    public async Task An_existing_world_readable_data_directory_is_made_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var dataDirectory = RouterHost.NewDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var app = RouterApp.Build(RouterHost.Args(dataDirectory));
        await app.DisposeAsync();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dataDirectory));
        Directory.Delete(dataDirectory, recursive: true);
    }
}
