using CDBurner10.Core.Burning;

namespace CDBurner10.Cli;

internal enum CliCommand
{
    Eject,
    Load,
    ListDrives,
    Erase,
    BurnData,
    BurnAudio,
    BurnVideo,
    BurnIso,
    WaitForDisc,
    DvdToMp4
}

internal readonly record struct FileFolderArgument(string SourcePath, string TargetPath)
{
    public bool IsFile
    {
        get
        {
            try { return File.Exists(SourcePath); }
            catch { return false; }
        }
    }

    public bool IsDir
    {
        get
        {
            try { return Directory.Exists(SourcePath); }
            catch { return false; }
        }
    }
}

internal sealed class CommandLineOptions
{
    public CliCommand Command { get; } = CliCommand.Eject;
    public short DeviceIndex { get; private set; }
    public string? RawDeviceArg { get; }
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FileFolderArgument> FilesAndFolders { get; } = [];
    public Dictionary<string, string> Arguments { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CommandLineOptions(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentNullException(nameof(args));

        string cmdName = args[0].Replace("-", "").ToLowerInvariant();
        foreach (CliCommand val in Enum.GetValues<CliCommand>())
        {
            if (val.ToString().Equals(cmdName, StringComparison.OrdinalIgnoreCase))
            {
                Command = val;
                break;
            }
        }

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            int colon = arg.IndexOf(':');
            if (colon < 0)
            {
                Flags.Add(arg);
                continue;
            }

            string keyFull = arg[..colon];
            int bracket = keyFull.IndexOf('[');
            string keyBase = bracket >= 0 ? keyFull[..bracket] : keyFull;
            string value = arg[(colon + 1)..];

            switch (keyBase.ToLowerInvariant())
            {
                case "-device":
                    RawDeviceArg = value;
                    break;
                case "-file":
                case "-folder":
                {
                    string target = string.Empty;
                    if (bracket >= 0)
                    {
                        target = keyFull[(bracket + 1)..].TrimEnd(']');
                    }
                    FilesAndFolders.Add(new FileFolderArgument(value.Trim(), target.Trim()));
                    break;
                }
                default:
                    Arguments[keyFull] = value;
                    break;
            }
        }
    }

    public void ResolveDeviceIndex()
    {
        if (string.IsNullOrWhiteSpace(RawDeviceArg))
        {
            DeviceIndex = 0;
            return;
        }

        var drive = BurningEngine.GetDriveByLetter(RawDeviceArg);
        if (drive is not null)
        {
            DeviceIndex = (short)drive.Index;
            return;
        }

        if (short.TryParse(RawDeviceArg, out short idx))
        {
            DeviceIndex = idx;
            return;
        }

        throw new ArgumentException($"Invalid device argument '{RawDeviceArg}'.", "device");
    }

    public bool HasAtLeastOneFile() => FilesAndFolders.Any(f => f.IsFile);
    public FileFolderArgument GetFirstFile() => FilesAndFolders.FirstOrDefault(f => f.IsFile);
}
