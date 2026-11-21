using System.Diagnostics;
using CDBurner10.Cli;

namespace CDBurner10.Tests;

public sealed class CliTests
{
    [Fact]
    public void CommandLineOptions_ParsesBurnDataFlagsFilesAndArguments()
    {
        var opts = new CommandLineOptions(
        [
            "--burn-data",
            "-device:E",
            "-file[\\Docs]:C:\\temp\\readme.txt",
            "-folder[\\Backup]:C:\\temp\\data",
            "-name:ReleaseDisc",
            "-verify",
            "-eject"
        ]);

        Assert.Equal(CliCommand.BurnData, opts.Command);
        Assert.Equal("E", opts.RawDeviceArg);
        Assert.Contains("-verify", opts.Flags);
        Assert.Contains("-eject", opts.Flags);
        Assert.Equal("ReleaseDisc", opts.Arguments["-name"]);

        Assert.Equal(2, opts.FilesAndFolders.Count);
        Assert.Equal(@"C:\temp\readme.txt", opts.FilesAndFolders[0].SourcePath);
        Assert.Equal(@"\Docs", opts.FilesAndFolders[0].TargetPath);
        Assert.Equal(@"C:\temp\data", opts.FilesAndFolders[1].SourcePath);
        Assert.Equal(@"\Backup", opts.FilesAndFolders[1].TargetPath);
    }

    [Theory]
    [InlineData("--list-drives", "ListDrives")]
    [InlineData("--burn-audio", "BurnAudio")]
    [InlineData("--burn-iso", "BurnIso")]
    [InlineData("--burn-video", "BurnVideo")]
    [InlineData("--erase", "Erase")]
    [InlineData("--eject", "Eject")]
    [InlineData("--load", "Load")]
    [InlineData("--wait-for-disc", "WaitForDisc")]
    public void CommandLineOptions_ParsesAllCliCommands(string flag, string expectedName)
    {
        var opts = new CommandLineOptions([flag]);
        Assert.Equal(expectedName, opts.Command.ToString());
    }

    [Fact]
    public async Task CdbxpcmdExe_ListDrives_ExitsZeroAndDetectsDrive()
    {
        // Locate cdbxpcmd.exe next to test output or in CDBurner10.Cli build output
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cdbxpcmd.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\CDBurner10.Cli\bin\Release\net10.0-windows10.0.19041.0\win-x86\cdbxpcmd.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\CDBurner10.Cli\bin\Debug\net10.0-windows10.0.19041.0\win-x86\cdbxpcmd.exe")),
            @"d:\CDBurner10\src\CDBurner10.Cli\bin\Release\net10.0-windows10.0.19041.0\win-x86\cdbxpcmd.exe",
            @"d:\CDBurner10\src\CDBurner10.Cli\bin\Debug\net10.0-windows10.0.19041.0\win-x86\cdbxpcmd.exe"
        };

        var exePath = candidates.FirstOrDefault(File.Exists);
        Assert.NotNull(exePath);

        var psi = new ProcessStartInfo
        {
            FileName = exePath!,
            Arguments = "--list-drives",
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.True(proc.ExitCode == 0, $"cdbxpcmd.exe --list-drives exited with {proc.ExitCode}. Stdout: {stdout}\nStderr: {stderr}");
        Assert.False(string.IsNullOrWhiteSpace(stdout), "Expected drive listing output on stdout.");
        Assert.Contains("0:", stdout);
    }
}
