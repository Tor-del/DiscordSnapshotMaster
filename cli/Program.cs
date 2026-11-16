using System.Text;
using DiscordSnapshot;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

try { return await RunAsync(args, stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Log(Strings.Get("CliStopped"));
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(Clean(error.Message));
    return 1;
}

static async Task<int> RunAsync(string[] arguments, CancellationToken cancellation)
{
    var languageIndex = Array.IndexOf(arguments, "--language");
    if (languageIndex >= 0)
    {
        var language = arguments.ElementAtOrDefault(languageIndex + 1)?.ToLowerInvariant();
        if (language is not ("en" or "ru")) throw new ArgumentException(Strings.Get("CliInvalidLanguage"));
        Strings.SetLanguage(language);
        arguments = arguments[..languageIndex].Concat(arguments[(languageIndex + 2)..]).ToArray();
    }
    if (arguments.Contains("--help") || arguments.Contains("-h"))
    {
        Console.WriteLine(Strings.Get("CliHelp", Strings.ProductName));
        return 0;
    }
    string? resume = null, status = null, output = null;
    int apiInterval = 2000, cdnInterval = 1000;
    for (var index = 0; index < arguments.Length; index += 2)
    {
        var option = arguments[index];
        if (index + 1 == arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
            throw new ArgumentException(Strings.Get("CliMissingValue", option));
        var value = arguments[index + 1];
        switch (option)
        {
            case "--resume": resume = value; break;
            case "--status": status = value; break;
            case "--output": output = value; break;
            case "--api-delay-ms": apiInterval = Interval(value, 30, option); break;
            case "--cdn-delay-ms": cdnInterval = Interval(value, 0, option); break;
            default: throw new ArgumentException(Strings.Get("CliUnknownOption", option));
        }
    }
    if (status is not null && resume is not null) throw new ArgumentException(Strings.Get("CliExclusiveOptions"));
    if (output is not null && (resume is not null || status is not null))
        throw new ArgumentException(Strings.Get("CliOutputForNewArchive"));
    if (status is not null)
    {
        var check = await SnapshotImporter.CheckAsync(status, Log, cancellation);
        Log(check.DamagedFiles == 0 ? Strings.Get("ArchivePassed") : Strings.Get("DamagedFileCount", check.DamagedFiles));
        return check.Incomplete || check.DamagedFiles != 0 ? 2 : 0;
    }
    if (Console.IsInputRedirected) throw new InvalidOperationException(Strings.Get("CliInteractiveRequired"));
    Log(Strings.Get("CliIntervals", apiInterval, cdnInterval));
    var api = new DiscordClient(ReadToken(cancellation)) { IntervalMs = apiInterval };
    var connection = await api.ConnectAsync(cancellation);
    Log(Strings.Get("CliBotConnected", connection.Username));
    var importer = new SnapshotImporter();
    void Opened(string folder)
    {
        Log(Strings.Get("CliArchiveFolder", folder));
        Log(Strings.Get("CliResumeCommand", folder, apiInterval, cdnInterval, Strings.Language));
    }
    if (resume is not null)
    {
        var result = await importer.ResumeAsync(api, resume, cdnInterval, Log, Opened, cancellation);
        return result.Incomplete ? 2 : 0;
    }
    if (connection.Servers.Count == 0) throw new InvalidOperationException(Strings.Get("NoServers"));
    var guild = Choose(connection.Servers, server => server.Label, multiple: false, cancellation).Single();
    var channels = await api.LoadChannelsAsync(guild.Id, cancellation);
    if (channels.Count == 0) throw new InvalidOperationException(Strings.Get("CliNoChannels"));
    var selected = Choose(channels, channel => $"{channel.Title} ({channel.KindLabel})", multiple: true, cancellation);
    var snapshot = await importer.StartAsync(api, guild.Id, selected.Select(channel => channel.Id).ToArray(),
        output ?? SnapshotImporter.DefaultArchiveRoot, cdnInterval, Log, Opened, cancellation);
    return snapshot.Incomplete ? 2 : 0;
}

static T[] Choose<T>(IReadOnlyList<T> entries, Func<T, string> label, bool multiple, CancellationToken cancellation)
{
    for (var index = 0; index < entries.Count; index++) Log($"  {index + 1}. {label(entries[index])}");
    while (true)
    {
        cancellation.ThrowIfCancellationRequested();
        Console.Write(multiple ? Strings.Get("CliChooseChannels") : Strings.Get("CliChooseServer"));
        var answer = Console.ReadLine()?.Trim();
        cancellation.ThrowIfCancellationRequested();
        if (answer is null) throw new InvalidOperationException(Strings.Get("CliInputEnded"));
        if (multiple && answer.Equals("all", StringComparison.OrdinalIgnoreCase)) return entries.ToArray();
        var choices = multiple ? answer.Split(',') : [answer];
        var indices = choices.Select(value => int.TryParse(value, out var index) ? index : 0).ToArray();
        if (indices.All(index => index >= 1 && index <= entries.Count)) return indices.Distinct().Select(index => entries[index - 1]).ToArray();
        Console.WriteLine(Strings.Get("CliChooseNumber"));
    }
}

static string ReadToken(CancellationToken cancellation)
{
    Console.Write(Strings.Get("CliTokenPrompt"));
    var token = new StringBuilder();
    try
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && token.Length > 0) token.Length--;
            else if (!char.IsControl(key.KeyChar)) token.Append(key.KeyChar);
        }
        var result = token.ToString().Trim();
        return result.Length > 0 ? result : throw new InvalidOperationException(Strings.Get("CliTokenMissing"));
    }
    finally { Console.WriteLine(); }
}

static int Interval(string value, int minimum, string option) => int.TryParse(value, out var ms) && ms >= minimum
    ? ms : throw new ArgumentException(Strings.Get("CliInvalidInterval", option, minimum));

static string Clean(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
static void Log(string value) => Console.WriteLine(Clean(value));
