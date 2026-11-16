using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DiscordSnapshot.Gui;

internal sealed class ViewerSession(Process process, string address) : IDisposable
{
    public string Address { get; } = address;
    public bool IsRunning => !process.HasExited;

    public static async Task<ViewerSession> StartAsync(string directory, CancellationToken cancellation)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "viewer", "server.py");
        Uri? callback = null;
        if (ArchiveAuthorization.IsEnabled(directory))
        {
            var redirect = ArchiveAuthorization.Load(directory).RedirectUri;
            if (!ArchiveAuthorization.IsValidRedirect(redirect))
                throw new InvalidOperationException(Strings.Get("InvalidOAuthSettings"));
            callback = new Uri(redirect);
            if (callback.Scheme != "http" || !ArchiveAuthorization.IsLoopback(callback))
                throw new InvalidOperationException(Strings.Get("DesktopOAuthRedirect"));
        }
        var start = new ProcessStartInfo(File.Exists(bundled) ? bundled : "python.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.Environment["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "false";
        foreach (var argument in new[] { "-I", "-B", "-u", script, "--archive", directory, "--port", (callback?.Port ?? 0).ToString(), "--desktop" })
            start.ArgumentList.Add(argument);

        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException(Strings.Get("ViewerLaunchFailed")); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            throw new InvalidOperationException(Strings.Get("ViewerPythonMissing"), error);
        }

        try
        {
            process.BeginErrorReadLine();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            var ready = await process.StandardOutput.ReadLineAsync(startup.Token);
            if (!int.TryParse(ready, out var port) || port is <= 0 or > 65535)
                throw new InvalidOperationException(Strings.Get(File.Exists(bundled) ? "ViewerLaunchFailed" : "ViewerPythonMissing"));
            return new ViewerSession(process, callback is null ? $"http://127.0.0.1:{port}/" : callback.GetLeftPart(UriPartial.Authority) + "/");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Stop(process);
            throw new InvalidOperationException(Strings.Get("ViewerLaunchFailed"));
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    public void Dispose() => Stop(process);

    private static void Stop(Process process)
    {
        try
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            if (!process.WaitForExit(350))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.WaitForExit(350);
            }
        }
        finally
        {
            process.StandardOutput.Dispose();
            process.Dispose();
        }
    }
}
