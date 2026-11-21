using System.Runtime.InteropServices;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Settings;

namespace CDBurner10.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        SettingsService.Default.Load();

        CommandLineOptions options;
        try
        {
            options = new CommandLineOptions(args);
        }
        catch (ArgumentNullException)
        {
            Console.WriteLine("No command line arguments given.");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine("The following command line argument is invalid: " + ex.ParamName);
            return 0;
        }

        if (options.Command == CliCommand.DvdToMp4)
        {
            return Commands.DvdToMp4(options);
        }

        if (options.Flags.Contains("-sptd"))
        {
            if (!BurningEngine.CheckSptdDriver())
            {
                Console.WriteLine("SPTD driver is not installed.");
                return -1;
            }
            BurningEngine.SetTransport(useSptd: true);
        }

        try
        {
            BurningEngine.Initialize();
            options.ResolveDeviceIndex();

            switch (options.Command)
            {
                case CliCommand.ListDrives:
                    Commands.ListDrives(options.Flags);
                    break;
                case CliCommand.Erase:
                    Commands.Erase(options.DeviceIndex);
                    break;
                case CliCommand.Load:
                    Commands.Load(options.DeviceIndex);
                    break;
                case CliCommand.BurnData:
                    Commands.BurnData(options.DeviceIndex, options);
                    break;
                case CliCommand.BurnAudio:
                    Commands.BurnAudio(options.DeviceIndex, options);
                    break;
                case CliCommand.BurnVideo:
                    Commands.BurnVideo(options.DeviceIndex, options);
                    break;
                case CliCommand.BurnIso:
                    Commands.BurnIso(options.DeviceIndex, options);
                    break;
                case CliCommand.WaitForDisc:
                    Commands.WaitForDisc(options.DeviceIndex);
                    break;
                default:
                    Commands.Eject(options.DeviceIndex);
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine("The following command line argument is invalid: " + ex.ParamName);
            return 0;
        }
        catch (COMException ex)
        {
            Console.WriteLine("An error (" + ex.ErrorCode.ToString("X") + ") occured while executing the command: " + ex.Message);
            return ex.ErrorCode;
        }
        finally
        {
            BassAudioEngine.Shutdown();
            BurningEngine.Uninitialize();
        }

        return 0;
    }
}
