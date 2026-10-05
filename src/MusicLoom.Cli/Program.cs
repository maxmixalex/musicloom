using System.Buffers.Binary;
using MusicLoom.AudioFingerprint;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Metadata;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "fingerprint-pcm" => FingerprintPcm(args),
                "config-check" => await CheckConfigAsync(args),
                "state-info" => await ShowStateAsync(args),
                "scan" => await ScanAsync(args),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int FingerprintPcm(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException("usage: musicloom fingerprint-pcm <3s-8khz-mono-f32le.pcm>");
        }

        byte[] bytes = File.ReadAllBytes(args[1]);
        if (bytes.Length != NeteaseFingerprint.RequiredSampleCount * sizeof(float))
        {
            throw new InvalidDataException(
                $"Expected {NeteaseFingerprint.RequiredSampleCount * sizeof(float)} bytes, got {bytes.Length}.");
        }

        float[] samples = new float[NeteaseFingerprint.RequiredSampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
            samples[i] = BitConverter.Int32BitsToSingle(bits);
        }

        FingerprintResult result = NeteaseFingerprint.Generate(samples);
        Console.WriteLine(result.Encoded);
        Console.Error.WriteLine($"peaks: {result.Peaks.Count}");
        return 0;
    }

    private static async Task<int> CheckConfigAsync(string[] args)
    {
        string path = args.Length >= 2 ? args[1] : "config.json";
        MusicLoomSettings settings = await SettingsLoader.LoadAsync(path);
        Console.WriteLine($"source:      {settings.Path.SourceDirectory}");
        Console.WriteLine($"destination: {settings.Path.DestinationDirectory}");
        Console.WriteLine($"lyrics:      {(settings.Organizer.Lyrics.Enabled ? "enabled" : "disabled")}");
        return 0;
    }

    private static async Task<int> ShowStateAsync(string[] args)
    {
        string path = args.Length >= 2 ? args[1] : Path.Combine(".musicloom", "state.json");
        StateDocument state = await new StateStore(path).LoadAsync();
        Console.WriteLine($"schema: {state.SchemaVersion}");
        Console.WriteLine($"recipe: {state.RecipeVersion}");
        Console.WriteLine($"tracks: {state.Tracks.Count}");
        return 0;
    }

    private static async Task<int> ScanAsync(string[] args)
    {
        string configPath = args.Length >= 2 ? args[1] : "config.json";
        string statePath = args.Length >= 3 ? args[2] : Path.Combine(".musicloom", "state.json");

        MusicLoomSettings settings = await SettingsLoader.LoadAsync(configPath);
        StateDocument state = await new StateStore(statePath).LoadAsync();
        LibraryScanner scanner = new(new AtlMetadataReader());
        LibraryPlan plan = await scanner.BuildPlanAsync(settings, state);

        Console.WriteLine(
            $"tracks={plan.TotalCount} unchanged={plan.UnchangedCount} new={plan.NewCount} " +
            $"modified={plan.ModifiedCount} errors={plan.ErrorCount}");

        foreach (PlanEntry entry in plan.Entries.Where(static entry => entry.Status != PlanEntryStatus.Unchanged))
        {
            string destination = entry.DestinationPath ?? "-";
            Console.WriteLine($"{entry.Status,-8} {entry.SourcePath} -> {destination}");
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                Console.WriteLine($"         {entry.Detail}");
            }
        }

        return plan.ErrorCount == 0 ? 0 : 1;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MusicLoom development CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  fingerprint-pcm <file>      Generate native Netease AFP from 3 s / 8 kHz / mono f32le PCM");
        Console.WriteLine("  config-check [file]         Validate and display a MusicLoom JSON configuration");
        Console.WriteLine("  state-info [file]           Inspect the incremental state store");
        Console.WriteLine("  scan [config] [state]       Dry-run the library and print pending changes");
    }
}
