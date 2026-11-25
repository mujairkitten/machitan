using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Core;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace Machitan.Shell;

/// <summary>Where the update stands.</summary>
internal enum UpdateState
{
    NotChecked,
    Checking,
    UpToDate,
    CheckFailed,

    /// <summary>A newer release is there (installable, or only its page when it has no installer for this PC or no SHA-256).</summary>
    Available,
    Downloading,
    DownloadFailed,

    /// <summary>Its Setup was started; Setup closes Machitan before it installs.</summary>
    SetupStarted,
    SetupFailed,
}

/// <summary>Why something did not happen, said in Settings > About.</summary>
internal enum UpdateProblem
{
    None,
    NoConnection,
    Refused,
    NoRelease,

    /// <summary>GitHub has only pre-releases, and they are not offered (the switch is off).</summary>
    NoFinalRelease,
    BadAnswer,
    NoInstaller,
    NoDigest,
    Broken,
    Cancelled,
    CannotSave,
    SetupDeclined,
    SetupDidNotStart,
    Other,
}

/// <summary>
/// In-app update, from Machitan's releases on GitHub (github.com/mujairkitten/machitan).
/// - The check reads GitHub's latest releases and takes the highest version, compared with this
///   Machitan's own (the csproj's Version; numbers first, then a label such as "-beta.1", which
///   comes before the same numbers without one). Drafts are never offered. Pre-releases (every
///   version before 1.0, as Sam has it, one with a label, and one GitHub marks as pre-release)
///   only while Settings > About > "Also offer pre-releases" is on (on until Machitan's 1.0, see
///   ShellSettings.PreReleasesByDefault). It runs
///   30 seconds after start and then once a day, while Settings > About > "Check for updates at
///   start and once a day" is on (on by default), and whenever About's button is used. A check
///   that could not run says so: never "newest" without GitHub's answer.
/// - A newer release: its installer for this PC (x64 or ARM64; Full or Trimmed, as this copy is)
///   is offered, never fetched on its own. "Download and install" fetches it into
///   %TEMP%\Machitan\Update, checks it against the SHA-256 GitHub computes for every release file
///   (no SHA-256 from GitHub, or a different one: not installed), checks it again just before,
///   and starts it with its own pages (no silent install). Setup asks Windows for administrator
///   rights, closes Machitan cleanly first (as "Machitan.exe --exit") and can start it again at
///   the end.
/// Failures go to diagnostics.log and Settings > About; never fatal. UI thread.
/// </summary>
internal sealed class UpdateService
{
    internal const string ReleasesPage = "https://github.com/mujairkitten/machitan/releases";
    /// <summary>The latest releases of either kind, newest first (drafts are not listed to others).</summary>
    private const string ReleaseList = "https://api.github.com/repos/mujairkitten/machitan/releases?per_page=20";
    private const string DownloadPrefix = "https://github.com/mujairkitten/machitan/releases/download/";

    /// <summary>Setup's own entry in Windows' installed apps (installer\Machitan.iss AppId).</summary>
    private const string SetupKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{19095618-65AC-4465-A960-1A10F039367D}_is1";

    private const int ErrorCancelled = 1223;
    private const int BufferSize = 81920;
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    /// <summary>A download that receives nothing for this long has stopped.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    private static readonly ReleaseVersion Installed = InstalledVersion();
    private static readonly HttpClient Http = MakeClient();

    private DispatcherQueueTimer? _timer;
    private Func<bool>? _wanted;
    private Func<bool>? _preReleases;
    private CancellationTokenSource? _download;
    private string? _assetUrl;
    private string? _assetDigest;
    private string? _setupFile;

    internal UpdateService()
    {
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        bool full = File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));
        // The x64 installers say "-x64" from 0.9.3 on (Sam, 2026-11-25); before, they said nothing.
        string edition = full ? "Full" : "Trimmed";
        EditionPattern = arm ? "-ARM64-" + edition : "(?:-x64)?-" + edition;
        ThisPc = (arm ? "ARM64" : "x64") + ", " + (full ? "Full" : "Trimmed");
        SetupFolder = ReadSetupFolder();
    }

    /// <summary>Raised when anything below changed (on the UI thread).</summary>
    internal event Action? Changed;

    internal UpdateState State { get; private set; } = UpdateState.NotChecked;

    /// <summary>Why the last step did not happen (or why an available release cannot be installed from here).</summary>
    internal UpdateProblem Problem { get; private set; }

    /// <summary>This Machitan's version as releases name it ("0.9.2", or "0.9.3-beta.1" with a label).</summary>
    internal string InstalledText { get; } = Installed.Text;

    /// <summary>The newest release's version, once a check found a newer one.</summary>
    internal string LatestText { get; private set; } = string.Empty;

    /// <summary>The newer one found is a pre-release (before 1.0, with a label, or marked so on GitHub).</summary>
    internal bool LatestIsPreRelease { get; private set; }

    /// <summary>The newest release's page (its notes), or the releases page.</summary>
    internal string ReleaseUrl { get; private set; } = ReleasesPage;

    /// <summary>The installer for this PC in the newest release, when there is one.</summary>
    internal string AssetName { get; private set; } = string.Empty;

    internal long AssetSize { get; private set; }

    /// <summary>This copy's kind, as the installers are made: "x64, Full", "ARM64, Trimmed".</summary>
    internal string ThisPc { get; }

    /// <summary>Where Setup installed Machitan (Windows' installed apps), or null when it never did.</summary>
    internal string? SetupFolder { get; }

    /// <summary>True when this copy is the one Setup installed (else an update goes to Setup's folder, not here).</summary>
    internal bool IsSetupCopy => SetupFolder is not null && string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(SetupFolder)),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>When the last check ended (local time), or null.</summary>
    internal DateTime? CheckedAt { get; private set; }

    /// <summary>The download's progress, 0 to 1.</summary>
    internal double Progress { get; private set; }

    /// <summary>The installer can be fetched and checked: there is one for this PC, and GitHub gave its SHA-256.</summary>
    internal bool CanInstall => _assetUrl is not null && _assetDigest is not null;

    /// <summary>A newer Machitan is known (and not installed yet as far as Machitan can tell).</summary>
    internal bool NewerKnown => State is UpdateState.Available or UpdateState.Downloading
        or UpdateState.DownloadFailed or UpdateState.SetupStarted or UpdateState.SetupFailed;

    /// <summary>
    /// The end of this PC's installer's name, before ".exe", as a regular expression:
    /// "-ARM64-Full", or "-x64-Trimmed" with the "-x64" optional (the names before 0.9.3).
    /// </summary>
    private string EditionPattern { get; }

    private static string UpdateFolder => Path.Combine(Path.GetTempPath(), "Machitan", "Update");

    /// <summary>
    /// Starts the checks: 30 seconds from now, then once a day, each only while
    /// <paramref name="wanted"/> says the setting is on; <paramref name="preReleases"/> says
    /// whether pre-releases are offered (read at every check). Installers left from an earlier
    /// update go.
    /// </summary>
    internal void Start(DispatcherQueue queue, Func<bool> wanted, Func<bool> preReleases)
    {
        _wanted = wanted;
        _preReleases = preReleases;
        _ = Task.Run(() => ClearFolder(UpdateFolder));
        DispatcherQueueTimer timer = queue.CreateTimer();
        _timer = timer;
        timer.Interval = FirstCheckAfter;
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (timer.Interval != CheckEvery)
            {
                timer.Stop();
                timer.Interval = CheckEvery;
                timer.Start();
            }

            if (_wanted?.Invoke() == true && State is not (UpdateState.Downloading or UpdateState.SetupStarted))
            {
                _ = CheckAsync();
            }
        };
        timer.Start();
    }

    internal void Stop()
    {
        _timer?.Stop();
        _download?.Cancel();
    }

    // ---------------------------------------------------------------- the check

    /// <summary>Asks GitHub for the newest release. Ignored while a check or a download runs.</summary>
    internal async Task CheckAsync()
    {
        if (State is UpdateState.Checking or UpdateState.Downloading)
        {
            return;
        }

        Problem = UpdateProblem.None;
        Set(UpdateState.Checking);
        UpdateProblem problem;
        bool newer = false;
        bool preReleases = _preReleases?.Invoke() == true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseList);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                problem = UpdateProblem.NoRelease;
            }
            else if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                problem = UpdateProblem.Refused;
            }
            else if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Update: GitHub answered the check with " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                problem = UpdateProblem.BadAnswer;
            }
            else
            {
                string text = await response.Content.ReadAsStringAsync();
                problem = ReadList(text, preReleases, out newer);
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("Update: GitHub could not be reached.", ex);
            problem = UpdateProblem.NoConnection;
        }
        catch (TaskCanceledException ex)
        {
            Log.Warn("Update: GitHub did not answer in time.", ex);
            problem = UpdateProblem.NoConnection;
        }
        catch (Exception ex)
        {
            Log.Error("Update: the check failed.", ex);
            problem = UpdateProblem.Other;
        }

        CheckedAt = DateTime.Now;
        if (problem is not (UpdateProblem.None or UpdateProblem.NoInstaller or UpdateProblem.NoDigest))
        {
            Problem = problem;
            Log.Info(problem == UpdateProblem.NoFinalRelease
                ? "Update: GitHub has only pre-releases, and they are not offered."
                : "Update: the check failed (" + problem + "); nothing is known about newer versions.");
            Set(UpdateState.CheckFailed);
            return;
        }

        Problem = problem;
        if (newer)
        {
            Log.Info("Update: Machitan " + LatestText + (LatestIsPreRelease ? " (pre-release)" : string.Empty)
                + " is available (this is " + InstalledText + ", " + ThisPc + "); "
                + (CanInstall ? "installer " + AssetName + "." : "it cannot be installed from here (" + problem + ")."));
            Set(UpdateState.Available);
        }
        else
        {
            Log.Info("Update: Machitan " + InstalledText + " is the newest release" + (preReleases ? " (pre-releases included)." : "."));
            Set(UpdateState.UpToDate);
        }
    }

    /// <summary>
    /// GitHub's latest releases: the one with the highest version, drafts skipped, and
    /// pre-releases too unless <paramref name="preReleases"/>. Only pre-releases, and those not
    /// offered: <see cref="UpdateProblem.NoFinalRelease"/>.
    /// </summary>
    private UpdateProblem ReadList(string text, bool preReleases, out bool newer)
    {
        newer = false;
        try
        {
            if (JsonNode.Parse(text) is not JsonArray releases)
            {
                Log.Warn("Update: GitHub's list of releases could not be read.");
                return UpdateProblem.BadAnswer;
            }

            JsonObject? best = null;
            ReleaseVersion bestVersion = default;
            int leftOut = 0;
            foreach (JsonNode? node in releases)
            {
                if (node is not JsonObject release
                    || (release["draft"] is JsonValue draftValue && draftValue.TryGetValue(out bool draft) && draft)
                    || TagVersion(release) is not ReleaseVersion version)
                {
                    continue;
                }

                if (!preReleases && IsPreRelease(release, version))
                {
                    leftOut++;
                    continue;
                }

                if (best is null || version.CompareTo(bestVersion) > 0)
                {
                    best = release;
                    bestVersion = version;
                }
            }

            if (best is null)
            {
                return leftOut > 0 ? UpdateProblem.NoFinalRelease : UpdateProblem.NoRelease;
            }

            return Use(best, bestVersion, out newer);
        }
        catch (JsonException ex)
        {
            Log.Warn("Update: GitHub's list of releases could not be read.", ex);
            return UpdateProblem.BadAnswer;
        }
    }

    /// <summary>
    /// A pre-release: every version before 1.0 (Sam, 2026-11-25: "anything below 1.0 are
    /// naturally pre-release"), one with a label ("1.0.0-rc.1"), and one GitHub marks as such.
    /// </summary>
    private static bool IsPreRelease(JsonObject release, ReleaseVersion version)
    {
        bool marked = release["prerelease"] is JsonValue value && value.TryGetValue(out bool pre) && pre;
        return marked || version.Numbers.Major == 0 || version.Label.Length > 0;
    }

    /// <summary>A release's version, from its tag (the first "1.2.3" in it, with a "-label" after it if any).</summary>
    private static ReleaseVersion? TagVersion(JsonObject release)
    {
        return release["tag_name"] is JsonValue tagValue && tagValue.TryGetValue(out string? tag)
            ? ReleaseVersion.Parse(tag)
            : null;
    }

    /// <summary>The release to compare with this Machitan: its version, page and installer for this PC.</summary>
    private UpdateProblem Use(JsonObject release, ReleaseVersion latest, out bool newer)
    {
        newer = latest.CompareTo(Installed) > 0;
        _assetUrl = null;
        _assetDigest = null;
        AssetName = string.Empty;
        AssetSize = 0;
        if (!newer)
        {
            return UpdateProblem.None;
        }

        LatestText = latest.Text;
        LatestIsPreRelease = IsPreRelease(release, latest);
        ReleaseUrl = release["html_url"] is JsonValue pageValue && pageValue.TryGetValue(out string? page)
            && page.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? page
            : ReleasesPage;

        // This PC's installer: "Machitan-Setup-<version>" and this edition's end, nothing else.
        var name = new Regex("^Machitan-Setup-[0-9.]+" + EditionPattern + @"\.exe$", RegexOptions.IgnoreCase);
        if (release["assets"] is JsonArray assets)
        {
            foreach (JsonNode? asset in assets)
            {
                if (asset is not JsonObject item
                    || item["name"] is not JsonValue nameValue
                    || !nameValue.TryGetValue(out string? assetName)
                    || !name.IsMatch(assetName)
                    || item["browser_download_url"] is not JsonValue urlValue
                    || !urlValue.TryGetValue(out string? url)
                    || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AssetName = assetName;
                AssetSize = item["size"] is JsonValue sizeValue && sizeValue.TryGetValue(out long size) ? size : 0;
                _assetUrl = url;
                _assetDigest = item["digest"] is JsonValue digestValue && digestValue.TryGetValue(out string? digest)
                    ? ReadDigest(digest)
                    : null;
                break;
            }
        }

        if (_assetUrl is null)
        {
            return UpdateProblem.NoInstaller;
        }

        return _assetDigest is null ? UpdateProblem.NoDigest : UpdateProblem.None;
    }

    /// <summary>GitHub's "sha256:&lt;64 hex digits&gt;", as lowercase hex; null when it is anything else.</summary>
    private static string? ReadDigest(string digest)
    {
        const string Prefix = "sha256:";
        if (!digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string hex = digest.Substring(Prefix.Length).Trim().ToLowerInvariant();
        return Regex.IsMatch(hex, "^[0-9a-f]{64}$") ? hex : null;
    }

    // ---------------------------------------------------------------- download and Setup

    /// <summary>
    /// Settings > About's "Download and install" (the user's yes): fetches the installer, checks
    /// its SHA-256, and starts it.
    /// </summary>
    internal async Task DownloadAndInstallAsync()
    {
        if (State is UpdateState.Checking or UpdateState.Downloading || _assetUrl is null || _assetDigest is null)
        {
            return;
        }

        string url = _assetUrl;
        string digest = _assetDigest;
        string file = Path.Combine(UpdateFolder, AssetName);
        long size = AssetSize;
        var cancel = new CancellationTokenSource();
        _download = cancel;
        Problem = UpdateProblem.None;
        Progress = 0;
        Set(UpdateState.Downloading);
        Log.Info("Update: downloading " + AssetName + ".");

        var progress = new Progress<double>(done =>
        {
            if (State == UpdateState.Downloading)
            {
                Progress = done;
                Changed?.Invoke();
            }
        });

        UpdateProblem problem;
        try
        {
            problem = await Task.Run(() => DownloadAsync(url, file, digest, size, progress, cancel.Token));
        }
        catch (Exception ex)
        {
            Log.Error("Update: the download failed.", ex);
            problem = UpdateProblem.Other;
        }
        finally
        {
            _download = null;
            cancel.Dispose();
        }

        if (problem != UpdateProblem.None)
        {
            Problem = problem;
            Log.Info("Update: the download did not finish (" + problem + ").");
            Set(UpdateState.DownloadFailed);
            return;
        }

        Log.Info("Update: " + AssetName + " downloaded; it matches GitHub's SHA-256.");
        _setupFile = file;
        await StartSetupAsync();
    }

    /// <summary>Stops the download under way (its file goes).</summary>
    internal void CancelDownload()
    {
        _download?.Cancel();
    }

    /// <summary>Starts the downloaded Setup again (after Windows' question was answered no, or Setup was closed).</summary>
    internal async Task StartSetupAgainAsync()
    {
        if (State is UpdateState.Checking or UpdateState.Downloading)
        {
            return;
        }

        if (_setupFile is null || !File.Exists(_setupFile))
        {
            // Gone from the temporary folder: fetched again.
            await DownloadAndInstallAsync();
            return;
        }

        await StartSetupAsync();
    }

    /// <summary>Checks the downloaded installer once more (the temporary folder can change), then starts it.</summary>
    private async Task StartSetupAsync()
    {
        string? file = _setupFile;
        string? digest = _assetDigest;
        if (file is null || digest is null)
        {
            return;
        }

        try
        {
            string now = await Task.Run(() => HashOf(file));
            if (now != digest)
            {
                Log.Warn("Update: the downloaded installer changed since it was checked; it was deleted.");
                TryDelete(file);
                _setupFile = null;
                Problem = UpdateProblem.Broken;
                Set(UpdateState.DownloadFailed);
                return;
            }

            using Process? setup = Process.Start(new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file) ?? string.Empty,
            });
            Log.Info("Update: Setup for Machitan " + LatestText + " was started; it closes Machitan before it installs.");
            Problem = UpdateProblem.None;
            Set(UpdateState.SetupStarted);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Update: Setup was not started (Windows' question was answered no).");
            Problem = UpdateProblem.SetupDeclined;
            Set(UpdateState.SetupFailed);
        }
        catch (Exception ex)
        {
            Log.Error("Update: Setup could not be started.", ex);
            Problem = UpdateProblem.SetupDidNotStart;
            Set(UpdateState.SetupFailed);
        }
    }

    /// <summary>Off the UI thread: the installer into <paramref name="file"/>, its SHA-256 worked out as it comes.</summary>
    private static async Task<UpdateProblem> DownloadAsync(string url, string file, string digest, long size, IProgress<double> progress, CancellationToken cancel)
    {
        // Network trouble and disk trouble both throw IOException; where it happened tells them apart.
        bool writing = false;
        try
        {
            writing = true;
            ClearFolder(UpdateFolder);
            Directory.CreateDirectory(UpdateFolder);
            writing = false;

            using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Update: GitHub answered the download with " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    ? UpdateProblem.Refused
                    : UpdateProblem.NoConnection;
            }

            long total = response.Content.Headers.ContentLength ?? size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (Stream source = await response.Content.ReadAsStreamAsync(cancel))
            {
                writing = true;
                using var target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                writing = false;
                byte[] buffer = new byte[BufferSize];
                long done = 0;
                int shown = -1;
                while (true)
                {
                    int read;
                    using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        stall.CancelAfter(StallLimit);
                        read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    writing = true;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    writing = false;
                    done += read;
                    if (total > 0)
                    {
                        int percent = (int)Math.Min(100, done * 100 / total);
                        if (percent != shown)
                        {
                            shown = percent;
                            progress.Report(percent / 100.0);
                        }
                    }
                }
            }

            string got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (got != digest)
            {
                Log.Warn("Update: the download does not match GitHub's SHA-256; it was deleted.");
                TryDelete(file);
                return UpdateProblem.Broken;
            }

            return UpdateProblem.None;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDelete(file);
            return UpdateProblem.Cancelled;
        }
        catch (OperationCanceledException ex)
        {
            Log.Warn("Update: GitHub stopped sending the installer.", ex);
            TryDelete(file);
            return UpdateProblem.NoConnection;
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("Update: the download failed on the way.", ex);
            TryDelete(file);
            return UpdateProblem.NoConnection;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(writing ? "Update: the installer could not be saved." : "Update: the download failed on the way.", ex);
            TryDelete(file);
            return writing ? UpdateProblem.CannotSave : UpdateProblem.NoConnection;
        }
    }

    // ---------------------------------------------------------------- helpers

    private void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private static string HashOf(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Four numbers, a missing one as 0, so 0.9.2 and 0.9.2.0 are the same.</summary>
    private static Version Normalize(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    /// <summary>As the installers name it: a last ".0" left out (0.9.2.0 is "0.9.2").</summary>
    private static string VersionText(Version version)
    {
        return version.Revision == 0
            ? version.ToString(3)
            : version.ToString(4);
    }

    /// <summary>
    /// This Machitan's version: the numbers of the assembly (the csproj's Version), and the
    /// label of its informational version when the csproj's Version has one ("0.9.3-beta.1").
    /// </summary>
    private static ReleaseVersion InstalledVersion()
    {
        Assembly assembly = typeof(UpdateService).Assembly;
        Version numbers = Normalize(assembly.GetName().Version ?? new Version(0, 0));
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string label = informational is not null && ReleaseVersion.Parse(informational) is ReleaseVersion read && read.Numbers == numbers
            ? read.Label
            : string.Empty;
        return new ReleaseVersion(numbers, label);
    }

    private static string? ReadSetupFolder()
    {
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? setup = machine.OpenSubKey(SetupKey);
            return setup?.GetValue("InstallLocation") is string folder && folder.Length > 0 ? folder : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Update: Setup's install folder could not be read.", ex);
            return null;
        }
    }

    private static void ClearFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(folder))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Update: the folder of earlier downloads could not be cleared.", ex);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Update: a downloaded file could not be deleted.", ex);
        }
    }

    private static HttpClient MakeClient()
    {
        // The timeout covers asking (and the answer's start); a download's body is watched by StallLimit.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("Machitan/" + VersionText(Installed.Numbers));
        return client;
    }

    /// <summary>
    /// A version as releases are tagged: four numbers, and a label ("beta.1" of "0.9.3-beta.1",
    /// empty when there is none). Numbers decide first; with the same numbers a label comes
    /// before none (0.9.3-beta.1 before 0.9.3), and labels compare part by part (numbers as
    /// numbers: beta.2 before beta.10).
    /// </summary>
    internal readonly record struct ReleaseVersion(Version Numbers, string Label) : IComparable<ReleaseVersion>
    {
        internal string Text => VersionText(Numbers) + (Label.Length > 0 ? "-" + Label : string.Empty);

        /// <summary>The first "1.2.3" (up to four numbers) in the text, with its "-label"; null when there is none.</summary>
        internal static ReleaseVersion? Parse(string text)
        {
            Match match = Regex.Match(text, @"(\d+(?:\.\d+){1,3})(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?");
            return match.Success && Version.TryParse(match.Groups[1].Value, out Version? numbers)
                ? new ReleaseVersion(Normalize(numbers), match.Groups[2].Success ? match.Groups[2].Value : string.Empty)
                : null;
        }

        public int CompareTo(ReleaseVersion other)
        {
            int byNumbers = Numbers.CompareTo(other.Numbers);
            if (byNumbers != 0)
            {
                return byNumbers;
            }

            if (string.Equals(Label, other.Label, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (Label.Length == 0)
            {
                return 1;
            }

            if (other.Label.Length == 0)
            {
                return -1;
            }

            string[] mine = Label.Split('.');
            string[] theirs = other.Label.Split('.');
            for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
            {
                bool myNumber = long.TryParse(mine[i], NumberStyles.None, CultureInfo.InvariantCulture, out long myValue);
                bool theirNumber = long.TryParse(theirs[i], NumberStyles.None, CultureInfo.InvariantCulture, out long theirValue);
                int part = myNumber && theirNumber ? myValue.CompareTo(theirValue)
                    : myNumber ? -1
                    : theirNumber ? 1
                    : string.Compare(mine[i], theirs[i], StringComparison.OrdinalIgnoreCase);
                if (part != 0)
                {
                    return part;
                }
            }

            return mine.Length.CompareTo(theirs.Length);
        }
    }
}
