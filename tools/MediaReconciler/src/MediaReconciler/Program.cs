using MediaReconciler;
using MediaReconciler.Config;
using MediaReconciler.Dropbox;
using MediaReconciler.Hashing;
using MediaReconciler.Reporting;

const string Usage = """
    MediaReconciler — checks that every photo and video is backed up, across any number of
    iCloud mirrors, Dropbox accounts, Google Takeout exports, iPhones and folders.
    Read-only: it never deletes or changes anything.

    Usage:
      MediaReconciler init [reconcile.json]          Write a starter config to edit
      MediaReconciler run [reconcile.json] [--no-cache]
                                                     Scan everything and write the report
      MediaReconciler dropbox-auth --app-key <key>   Get a Dropbox refresh token for a config
      MediaReconciler devices                        List connected phones (Windows build)
    """;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var command = args.FirstOrDefault()?.ToLowerInvariant();
    var rest = args.Skip(1).ToArray();
    return command switch
    {
        "init" => Init(rest),
        "run" or "reconcile" => await RunAsync(rest, cts.Token),
        "dropbox-auth" => await DropboxAuthAsync(rest, cts.Token),
        "devices" => Devices(),
        null or "help" or "-h" or "--help" => Help(),
        _ => Help(exitCode: 2),
    };
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled. Hashes read so far are cached for next time.");
    return 130;
}
// An HttpClient timeout is also an OperationCanceledException; it is an error, not a Ctrl+C.
catch (Exception e) when (e is ConfigException or DropboxException or IOException or InvalidDataException or HttpRequestException
                              or InvalidOperationException or System.Text.Json.JsonException or FormatException
                              or UnauthorizedAccessException or OperationCanceledException)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 1;
}

static int Help(int exitCode = 0)
{
    Console.WriteLine(Usage);
    return exitCode;
}

static int Init(string[] args)
{
    var path = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "reconcile.json";
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"{path} already exists; not overwriting it.");
        return 1;
    }

    File.WriteAllText(path, SampleConfig.Json);
    Console.WriteLine($"Wrote {Path.GetFullPath(path)}. Edit the sources, then run: MediaReconciler run {path}");
    return 0;
}

static async Task<int> RunAsync(string[] args, CancellationToken ct)
{
    var path = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "reconcile.json";
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No config at {Path.GetFullPath(path)}. Create one with: MediaReconciler init");
        return 1;
    }

    var config = ReconcileConfig.Load(path);
    var cache = args.Contains("--no-cache") ? ScanCache.InMemory() : ScanCache.Load(config.ResolveCachePath());
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

    var reconciler = new Reconciler(config, cache, http, message => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {message}"));
    var analysis = await reconciler.RunAsync(ct);
    var summary = ReportWriter.Write(analysis, config.OutputDirectory);

    Console.WriteLine();
    Console.WriteLine(summary);
    Console.WriteLine($"Full report and CSV lists: {config.OutputDirectory}");
    return analysis.ByBackupCopies[0].Count > 0 ? 3 : 0;
}

static async Task<int> DropboxAuthAsync(string[] args, CancellationToken ct)
{
    var keyIndex = Array.IndexOf(args, "--app-key");
    if (keyIndex < 0 || keyIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("""
            Usage: MediaReconciler dropbox-auth --app-key <key>

            One-time setup: at https://www.dropbox.com/developers/apps create an app with
            "Scoped access" and "Full Dropbox", tick the files.metadata.read permission,
            and copy its App key. Run this once per Dropbox account you want to check.
            """);
        return 2;
    }

    var appKey = args[keyIndex + 1];
    var (url, verifier) = DropboxClient.BuildAuthorizeUrl(appKey);
    Console.WriteLine("1. Open this link, sign in to the Dropbox account to check, and click Allow:");
    Console.WriteLine();
    Console.WriteLine("   " + url);
    Console.WriteLine();
    Console.Write("2. Paste the code Dropbox shows you: ");
    var code = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(code)) return 1;

    using var http = new HttpClient();
    var (refreshToken, accountId) = await new DropboxClient(http).ExchangeCodeAsync(appKey, code, verifier, ct);
    Console.WriteLine();
    Console.WriteLine($"Connected (account {accountId}). Add to this account's source in reconcile.json:");
    Console.WriteLine();
    Console.WriteLine($"   \"appKey\": \"{appKey}\",");
    Console.WriteLine($"   \"refreshToken\": \"{refreshToken}\"");
    Console.WriteLine();
    Console.WriteLine("Or keep the token out of the file: set an environment variable (e.g. setx DROPBOX_JOHN <token>)");
    Console.WriteLine("and write \"refreshToken\": \"env:DROPBOX_JOHN\".");
    return 0;
}

static int Devices()
{
#if WINDOWS
    var devices = MediaReconciler.Sources.IPhoneSource.ListDevices();
    if (devices.Count == 0) Console.WriteLine("No portable devices found. Plug in the phone, unlock it and tap Trust.");
    foreach (var device in devices) Console.WriteLine(device);
    return 0;
#else
    Console.Error.WriteLine("Listing phones needs the Windows build (net10.0-windows).");
    return 1;
#endif
}
