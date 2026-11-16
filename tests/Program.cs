using System.Diagnostics;
using System.Net;
using System.Text;
using DiscordSnapshot;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var root = Directory.CreateTempSubdirectory("discord-snapshot-").FullName;
try
{
    using var server = new FakeDiscord();
    using var http = new HttpClient(server, disposeHandler: false);
    var client = new DiscordClient("test-token", http) { IntervalMs = 30 };
    var connection = await client.ConnectAsync(timeout.Token);
    Expect(connection.Username == "Архивариус" && connection.Servers.Single().Id == "1", "подключение бота");
    Expect(server.FirstRetryDelay.TotalMilliseconds >= 140, "Retry-After соблюдён");
    var channels = await client.LoadChannelsAsync("1", timeout.Token);
    Expect(channels.Single().Title == "Общение / русский канал", "выбор каналов учитывает права и категорию");

    var importer = new SnapshotImporter(http);
    var archiveRoot = Path.Combine(root, "Архивы Discord");
    await ExpectFailure<FileNotFoundException>(() => importer.ResumeAsync(client, root, 0, _ => { }, _ => { }, timeout.Token),
        "для продолжения нужна папка с базой снимка");
    await File.WriteAllTextAsync(Path.Combine(root, "snapshot.sqlite"), "", timeout.Token);
    await ExpectFailure<InvalidDataException>(() => SnapshotImporter.CheckAsync(root, _ => { }, timeout.Token),
        "файл с подходящим именем проверяется на версию формата");
    using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    var directory = "";
    var log = new List<string>();
    var importing = importer.StartAsync(client, "1", ["30"], archiveRoot, 0, log.Add, path => directory = path, interrupt.Token);
    var downloading = server.StartedDownloading.Task.WaitAsync(timeout.Token);
    var first = await Task.WhenAny(importing, downloading);
    await first;
    Expect(first == downloading, "загрузка дошла до вложения");
    Expect(Path.GetDirectoryName(directory) == archiveRoot && File.Exists(Path.Combine(directory, "snapshot.sqlite")),
        "архив создан в указанной папке и содержит базу снимка");
    var compose = Path.Combine(directory, "compose.yml");
    var composeContents = await File.ReadAllTextAsync(compose, timeout.Token);
    Expect(composeContents.Contains("image: docker.io/tordel/discord-snapshot-web:latest"),
        "новый архив содержит Compose для опубликованного просмотрщика");
    await ExpectFailure<IOException>(() => importer.ResumeAsync(client, directory, 0, _ => { }, _ => { }, timeout.Token), "второй импорт блокируется");
    await interrupt.CancelAsync();
    await ExpectFailure<OperationCanceledException>(async () => await importing, "остановка запроса загрузки");
    var partial = await SnapshotImporter.CheckAsync(directory, _ => { }, timeout.Token);
    Expect(partial.Incomplete && partial.DamagedFiles == 0, "незавершённое скачивание отмечено в архиве");
    var progress = SnapshotImporter.InspectProgress(directory);
    Expect(progress is { Incomplete: true, Server.Id: "1" } && progress.Channels.Single().Id == "30",
        "проект восстанавливает исходный выбор и состояние архива");

    log.Clear();
    File.Delete(compose);
    var completed = await importer.ResumeAsync(client, directory, 0, log.Add, _ => { }, timeout.Token);
    Expect(!completed.Incomplete, "архив продолжен без пропусков");
    Expect(await File.ReadAllTextAsync(compose, timeout.Token) == composeContents, "при возобновлении добавляется отсутствующий Compose");
    Expect(server.RootCursors.Count == 3 && server.RootCursors[1] == "100" && server.RootCursors[2] == "90", "курсор возобновился с сохранённой страницы");
    Expect(server.RefreshedAttachment, "получен свежий URL вложения");
    Expect(log.Any(line => line.Contains("русский канал [30] · 2 messages")), "сообщения сохранены без дубликатов");
    Expect(log.Any(line => line.Contains("Архивная ветка [31] · 0 messages")), "архивная ветка включена в снимок");
    var checkedArchive = await SnapshotImporter.CheckAsync(directory, _ => { }, timeout.Token);
    Expect(!checkedArchive.Incomplete && checkedArchive.DamagedFiles == 0, "SQLite и SHA-256 проверены");
    Expect(SnapshotImporter.InspectProgress(directory) is { Incomplete: false }, "готовность снимка определяется при повторном открытии");
    var attachment = Directory.GetFiles(Path.Combine(directory, "media"), "*.txt").Single();
    await File.WriteAllTextAsync(attachment, "fail", timeout.Token);
    var damaged = await SnapshotImporter.CheckAsync(directory, _ => { }, timeout.Token);
    Expect(damaged.DamagedFiles == 1, "повреждение того же размера найдено по SHA-256");

    var projectDirectory = Path.Combine(root, "Новый проект");
    Directory.CreateDirectory(projectDirectory);
    Expect(SnapshotImporter.InspectProgress(projectDirectory) is null, "новый проект пока не содержит снимка");
    var publishedMedia = new List<string>();
    var remoteImporter = new SnapshotImporter(http, (path, _) =>
    {
        Expect(File.Exists(Path.Combine(projectDirectory, path)), "медиа готово локально перед отправкой");
        publishedMedia.Add(path);
        return Task.CompletedTask;
    });
    var projectCopy = await remoteImporter.StartInDirectoryAsync(client, "1", ["30"], projectDirectory, 0, _ => { }, timeout.Token);
    Expect(!projectCopy.Incomplete && projectCopy.Directory == projectDirectory && File.Exists(Path.Combine(projectDirectory, "snapshot.sqlite")),
        "снимок создаётся непосредственно в папке проекта");
    Expect(publishedMedia.Any(path => path.EndsWith(".txt", StringComparison.Ordinal)), "вложение передано до завершения копирования");

    server.ExposeAdditionalChannel = true;
    var priorRequests = server.RootCursors.Count;
    var initialProgress = SnapshotImporter.InspectProgress(projectDirectory)!;
    var additionId = Guid.NewGuid().ToString("N");
    using var pauseAddition = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    await ExpectFailure<OperationCanceledException>(() =>
        importer.CopyChannelsAsync(client, projectDirectory, ["30"], additionId, 0, _ => { }, new CancellationToken(true)),
        "отмена перед постановкой обновления в очередь");
    Expect(SnapshotImporter.InspectProgress(projectDirectory)!.OperationId == initialProgress.OperationId,
        "архив сохраняет идентификатор последней запущенной операции");
    await ExpectFailure<InvalidOperationException>(() =>
        importer.CopyChannelsAsync(client, projectDirectory, ["30", "32"], additionId, 0, _ => { }, timeout.Token),
        "копирование недоступного канала отклоняется целиком");
    Expect(SnapshotImporter.InspectProgress(projectDirectory) is { Incomplete: false } unchanged && unchanged.Channels.Length == 1,
        "отклонённое добавление сохраняет состояние архива");
    await ExpectFailure<OperationCanceledException>(() => importer.CopyChannelsAsync(client, projectDirectory, ["33"], additionId, 0, line =>
    {
        if (line.Contains("Находки") && line.Contains("saved")) pauseAddition.Cancel();
    }, pauseAddition.Token), "приостановка докопирования после сохранённой страницы");
    Expect(SnapshotImporter.InspectProgress(projectDirectory) is { Incomplete: true } expanded && expanded.Channels.Length == 2 &&
        expanded.OperationId == additionId && !expanded.CopiedAt.ContainsKey("33"),
        "после паузы оба канала хранятся в той же базе, дата новой копии появится после завершения");
    log.Clear();
    var extended = await importer.ResumeAsync(client, projectDirectory, 0, log.Add, _ => { }, timeout.Token);
    Expect(!extended.Incomplete && server.RootCursors.Count == priorRequests && server.AddedCursors.Count == 2 && server.AddedCursors[1] == "101",
        "докопирование возобновляется с сохранённой страницы и пропускает готовый канал");
    Expect(log.Any(line => line.Contains("русский канал [30] · 2 messages")) &&
        log.Any(line => line.Contains("Находки [33] · 1 messages")), "сообщения обоих каналов хранятся в одном архиве");
    var addedProgress = SnapshotImporter.InspectProgress(projectDirectory)!;
    Expect(addedProgress.CopiedAt["30"] == initialProgress.CopiedAt["30"] && addedProgress.CopiedAt.ContainsKey("33"),
        "у неотмеченного канала сохраняется прежняя дата копии");

    server.NewRootMessageId = server.RootCursors[^3];
    server.ExposeThirdChannel = true;
    var mixedId = Guid.NewGuid().ToString("N");
    log.Clear();
    var mixed = await importer.CopyChannelsAsync(client, projectDirectory, ["30", "34"], mixedId, 0, log.Add, timeout.Token);
    var updatedProgress = SnapshotImporter.InspectProgress(projectDirectory)!;
    Expect(!mixed.Incomplete && updatedProgress.OperationId == mixedId && updatedProgress.Channels.Length == 3 &&
        updatedProgress.CopiedAt["30"] > addedProgress.CopiedAt["30"] && updatedProgress.CopiedAt["33"] == addedProgress.CopiedAt["33"],
        "смешанная операция обновляет дату только выбранного старого канала и добавляет новый");
    Expect(server.RootCursors.Count == priorRequests + 1 && server.AddedCursors.Count == 2 &&
        log.Any(line => line.Contains("русский канал [30] · 3 messages")) &&
        log.Any(line => line.Contains("Архивная ветка [31] · 1 messages")) &&
        log.Any(line => line.Contains("Третий [34] · 1 messages")), "обновление сохраняет новые сообщения канала и его ветки без дублей");
    var mixedCheck = await SnapshotImporter.CheckAsync(projectDirectory, _ => { }, timeout.Token);
    Expect(!mixedCheck.Incomplete && mixedCheck.DamagedFiles == 0, "архив после смешанного копирования проходит проверку");

    server.NewRootMessageId = null;
    var reviewDirectory = Path.Combine(root, "Управление копированием");
    var reviewId = Guid.NewGuid().ToString("N");
    using var pauseReview = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    await ExpectFailure<OperationCanceledException>(() => importer.StartInDirectoryAsync(client, "1", ["30", "33", "34"],
        reviewDirectory, 0, line =>
        {
            if (line.Contains("русский канал") && line.Contains("saved")) pauseReview.Cancel();
        }, pauseReview.Token, operationId: reviewId), "пауза после сохранения первой страницы");
    await ExpectFailure<InvalidOperationException>(() =>
    {
        SnapshotImporter.ResolveChannelCopies(reviewDirectory, reviewId, ["30", "32"], markComplete: false);
        return Task.CompletedTask;
    }, "неактуальный выбор отклоняется целиком");
    SnapshotImporter.ResolveChannelCopies(reviewDirectory, reviewId, ["30"], markComplete: false);
    SnapshotImporter.ResolveChannelCopies(reviewDirectory, reviewId, ["33"], markComplete: true);
    var reviewed = SnapshotImporter.InspectProgress(reviewDirectory)!;
    Expect(reviewed.Incomplete && reviewed.PendingChannelIds.SetEquals(["34"]) &&
        !reviewed.CopiedAt.ContainsKey("30") && reviewed.CopiedAt.ContainsKey("33"),
        "пропуск сохраняет прежнюю границу, ручное завершение отмечает дату, оставшийся канал ждёт копирования");
    var rootRequests = server.RootCursors.Count;
    var addedRequests = server.AddedCursors.Count;
    var threadRequests = server.ThreadRequests;
    log.Clear();
    var reviewedCopy = await importer.ResumeAsync(client, reviewDirectory, 0, log.Add, _ => { }, timeout.Token);
    Expect(!reviewedCopy.Incomplete && server.RootCursors.Count == rootRequests && server.AddedCursors.Count == addedRequests &&
        server.ThreadRequests == threadRequests &&
        log.Any(line => line.Contains("русский канал [30] · 1 messages")) &&
        log.Any(line => line.Contains("Архивная ветка [31] · 0 messages")),
        "пропуск и ручное завершение сохраняют загруженную историю и исключают каналы и ветки из продолжения");
    log.Clear();
    var retried = await importer.CopyChannelsAsync(client, reviewDirectory, ["30"], Guid.NewGuid().ToString("N"), 0, log.Add, timeout.Token);
    Expect(!retried.Incomplete && log.Any(line => line.Contains("русский канал [30] · 2 messages")),
        "пропущенный канал можно скопировать снова без потери сообщений");
    Console.WriteLine("Проверки прошли. Права, лимиты, ветки, остановка, возобновление, пропуск, ручное завершение, докопирование, обновление, вложения и SQLite.");
}
finally { Directory.Delete(root, recursive: true); }

static void Expect(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"Проверка не пройдена ({description}).");
}

static async Task ExpectFailure<T>(Func<Task> action, string description) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Ожидалась ошибка ({description}).");
}

internal sealed class FakeDiscord : HttpMessageHandler
{
    private const string Bot = """{"id":"2","bot":true,"username":"Архивариус","discriminator":"0"}""";
    private const string Author = """{"id":"10","username":"автор","discriminator":"0"}""";
    private const string Guild = """{"id":"1","roles":[{"id":"1","permissions":"66560"}]}""";
    private const string ChannelList = """
        [{"id":"40","type":4,"name":"Общение"},
         {"id":"30","type":0,"name":"русский канал","parent_id":"40"},
         {"id":"32","type":0,"name":"служебный","permission_overwrites":[{"id":"2","type":1,"deny":"65536","allow":"0"}]}]
        """;
    private const string Threads = """{"threads":[{"id":"31","type":11,"name":"Архивная ветка","parent_id":"30"}],"has_more":false}""";
    private long limitedAt;
    private int oldFileCalls;
    public TaskCompletionSource StartedDownloading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan FirstRetryDelay { get; private set; }
    public List<string> RootCursors { get; } = [];
    public List<string> AddedCursors { get; } = [];
    public int ThreadRequests { get; private set; }
    public bool ExposeAdditionalChannel { get; set; }
    public bool ExposeThirdChannel { get; set; }
    public string? NewRootMessageId { get; set; }
    public bool RefreshedAttachment { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var url = request.RequestUri!;
        if (url.Host == "cdn.discordapp.com")
        {
            if (request.Headers.Authorization is not null) throw new InvalidOperationException("Токен передан CDN.");
            if (url.Query == "?ex=old")
            {
                if (++oldFileCalls == 1)
                {
                    StartedDownloading.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data") };
        }
        if (url.Host != "discord.com" || request.Headers.Authorization?.ToString() != "Bot test-token")
            throw new InvalidOperationException("Некорректный запрос Discord.");
        var path = url.AbsolutePath["/api/v10/".Length..];
        if (path == "users/@me" && limitedAt == 0)
        {
            limitedAt = Stopwatch.GetTimestamp();
            var limited = Json("""{"retry_after":0.08}""", HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("Retry-After", "0.08");
            return limited;
        }
        if (path == "users/@me") FirstRetryDelay = Stopwatch.GetElapsedTime(limitedAt);
        if (path == "channels/30/messages")
        {
            var before = url.Query.Split('&')[0]["?before=".Length..];
            RootCursors.Add(before);
            if (NewRootMessageId is { } id)
                return Json($"[{Message(id, null)},{Message("100", "fresh")},{Message("90", null)}]");
            return Json(before switch { "100" => $"[{Message("90", null)}]", "90" => "[]", _ => $"[{Message("100", "old")}]" });
        }
        if (path == "channels/30/messages/100")
        {
            RefreshedAttachment = true;
            return Json(Message("100", "fresh"));
        }
        if (path == "channels/33/messages")
        {
            var before = url.Query.Split('&')[0]["?before=".Length..];
            AddedCursors.Add(before);
            return Json(before == "101" ? "[]" : $"[{Message("101", null)}]");
        }
        if (path == "channels/31/messages")
        {
            ThreadRequests++;
            if (NewRootMessageId is not { } rootId) return Json("[]");
            var threadId = (ulong.Parse(rootId) + 1).ToString();
            return Json(url.Query.StartsWith($"?before={threadId}&", StringComparison.Ordinal) ? "[]" : $"[{Message(threadId, null)}]");
        }
        if (path == "channels/34/messages")
            return Json(url.Query.StartsWith("?before=102&", StringComparison.Ordinal) ? "[]" : $"[{Message("102", null)}]");
        return path switch
        {
            "users/@me" => Json(Bot),
            "applications/@me" => Json("""{"flags":524288}"""),
            "users/@me/guilds" => Json("""[{"id":"1","name":"Сервер"}]"""),
            "guilds/1" => Json(Guild),
            "guilds/1/members/2" => Json($$"""{"user":{{Bot}},"roles":[]}"""),
            "guilds/1/members/10" => Json($$"""{"user":{{Author}},"nick":"Имя в сообществе","roles":[]}"""),
            "guilds/1/channels" => Json(ChannelList[..^1] +
                (ExposeAdditionalChannel ? """,{"id":"33","type":5,"name":"Находки","parent_id":"40"}""" : "") +
                (ExposeThirdChannel ? """,{"id":"34","type":2,"name":"Третий","parent_id":"40","permission_overwrites":[{"id":"2","type":1,"allow":"1048576","deny":"0"}]}""" : "") + "]"),
            "guilds/1/threads/active" or "channels/30/users/@me/threads/archived/private" or "channels/33/threads/archived/public"
                => Json("""{"threads":[],"has_more":false}"""),
            "channels/30/threads/archived/public" => Json(Threads),
            _ => throw new InvalidOperationException($"Неожиданный запрос {path}."),
        };
    }

    private static string Message(string id, string? signature)
    {
        var attachments = signature is null ? "[]" : $$"""[{"id":"300","url":"https://cdn.discordapp.com/attachments/30/300/file.txt?ex={{signature}}","size":4}]""";
        return $$"""{"id":"{{id}}","author":{{Author}},"timestamp":"2025-01-02T00:00:00Z","content":"Сообщение","attachments":{{attachments}}}""";
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
