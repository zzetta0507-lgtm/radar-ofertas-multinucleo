using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Pipeline;

namespace RadarOfertasMultinucleo.Infrastructure.Replay;

public enum HttpSourceMode
{
    Live,
    Replay
}

public sealed record DownloadedPage(string Body, string ContentType, DateTimeOffset RetrievedAt);

public sealed class ReplayableHttpClient : IDisposable
{
    private const string UserAgent =
        "RadarOfertasMultinucleo/1.0 (+https://github.com/zzetta0507-lgtm/radar-ofertas-multinucleo)";
    private readonly HttpClient _client;
    private readonly PipelineOptions _options;
    private readonly HttpSourceMode _sourceMode;
    private readonly bool _recordReplay;
    private readonly PipelineRunMetrics? _metrics;
    private readonly string _replayDirectory;
    private readonly DomainRequestLimiter _limiter;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestLocks = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsRules>>> _robotsRules = new();
    private readonly ConcurrentDictionary<string, RecordedHttpResponse> _memoryResponses = new();

    public ReplayableHttpClient(
        PipelineOptions options,
        HttpSourceMode sourceMode,
        bool recordReplay,
        PipelineRunMetrics? metrics = null)
    {
        _options = options;
        _sourceMode = sourceMode;
        _recordReplay = recordReplay;
        _metrics = metrics;
        _replayDirectory = Path.GetFullPath(options.ReplayDirectory);
        _limiter = new DomainRequestLimiter(
            options.MaxConcurrentRequestsPerDomain,
            TimeSpan.FromMilliseconds(options.DomainMinIntervalMilliseconds));
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<string> GetStringAsync(
        Uri uri,
        CancellationToken cancellationToken) =>
        (await GetPageAsync(uri, cancellationToken)).Body;

    public async Task<DownloadedPage> GetPageAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Solo se permiten fuentes HTTPS: {uri}.");
        }

        var rules = await _robotsRules.GetOrAdd(
            uri.GetLeftPart(UriPartial.Authority),
            _ => new Lazy<Task<RobotsRules>>(
                () => LoadRobotsRulesAsync(uri, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        if (!rules.IsAllowed(uri.PathAndQuery))
        {
            throw new InvalidOperationException($"robots.txt no permite consultar {uri}.");
        }

        var recorded = await GetRecordedResponseAsync(uri, cancellationToken);
        if (recorded is not null)
        {
            RecordRequest(uri, recorded);
            await ApplyReplayLatencyAsync(uri, cancellationToken);
            return EnsureSuccessfulResponse(uri, recorded);
        }

        if (_sourceMode == HttpSourceMode.Replay)
        {
            throw new FileNotFoundException(
                $"No existe una respuesta grabada para {uri}. No se realizó ninguna solicitud de red.");
        }

        var requestLock = _requestLocks.GetOrAdd(uri.AbsoluteUri, _ => new SemaphoreSlim(1, 1));
        await requestLock.WaitAsync(cancellationToken);
        try
        {
            recorded = await GetRecordedResponseAsync(uri, cancellationToken);
            if (recorded is not null)
            {
                RecordRequest(uri, recorded);
                return EnsureSuccessfulResponse(uri, recorded);
            }

            var result = await SendRequestAsync(uri, cancellationToken);
            _memoryResponses[uri.AbsoluteUri] = result;

            if (_recordReplay)
            {
                await SaveRecordedResponseAsync(result, cancellationToken);
            }

            return EnsureSuccessfulResponse(uri, result);
        }
        finally
        {
            requestLock.Release();
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<RobotsRules> LoadRobotsRulesAsync(
        Uri requestedUri,
        CancellationToken cancellationToken)
    {
        var robotsUri = new Uri(requestedUri.GetLeftPart(UriPartial.Authority) + "/robots.txt");
        var response = await GetResponseAsync(robotsUri, cancellationToken);
        if (response.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return RobotsRules.AllowAll;
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            throw new HttpRequestException(
                $"No se pudo verificar robots.txt de {robotsUri}: HTTP {response.StatusCode}.");
        }

        return RobotsRules.Parse(response.Body);
    }

    private async Task<RecordedHttpResponse> GetResponseAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var cached = await GetRecordedResponseAsync(uri, cancellationToken);
        if (cached is not null)
        {
            RecordRequest(uri, cached);
            return cached;
        }

        if (_sourceMode == HttpSourceMode.Replay)
        {
            throw new FileNotFoundException(
                $"Falta robots.txt grabado para {uri}; el modo replay no usa la red.");
        }

        var result = await SendRequestAsync(uri, cancellationToken);
        _memoryResponses[uri.AbsoluteUri] = result;
        if (_recordReplay)
        {
            await SaveRecordedResponseAsync(result, cancellationToken);
        }

        return result;
    }

    private async Task<RecordedHttpResponse> SendRequestAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var permit = await _limiter.AcquireAsync(uri.Host, cancellationToken);
        try
        {
            using var response = await _client.GetAsync(uri, cancellationToken);
            var result = new RecordedHttpResponse(
                uri.AbsoluteUri,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "text/plain",
                await response.Content.ReadAsStringAsync(cancellationToken),
                DateTimeOffset.UtcNow);
            RecordRequest(uri, result);
            return result;
        }
        catch (HttpRequestException)
        {
            _metrics?.RecordHttpRequest(uri, successful: false);
            throw;
        }
        catch (IOException)
        {
            _metrics?.RecordHttpRequest(uri, successful: false);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _metrics?.RecordHttpRequest(uri, successful: false);
            throw;
        }
    }

    private void RecordRequest(Uri uri, RecordedHttpResponse response) =>
        _metrics?.RecordHttpRequest(
            uri,
            response.StatusCode is >= 200 and < 300);

    private static DownloadedPage EnsureSuccessfulResponse(Uri uri, RecordedHttpResponse response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new HttpRequestException(
                $"La fuente {uri} respondió HTTP {response.StatusCode}.");
        }

        return new DownloadedPage(response.Body, response.ContentType, response.RetrievedAt);
    }

    private async Task<RecordedHttpResponse?> GetRecordedResponseAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (_memoryResponses.TryGetValue(uri.AbsoluteUri, out var inMemory))
        {
            return inMemory;
        }

        var path = GetResponsePath(uri);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var response = await JsonSerializer.DeserializeAsync<RecordedHttpResponse>(
            stream,
            cancellationToken: cancellationToken);
        if (response is not null)
        {
            _memoryResponses[uri.AbsoluteUri] = response;
        }

        return response;
    }

    private async Task SaveRecordedResponseAsync(
        RecordedHttpResponse response,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_replayDirectory);
        var path = GetResponsePath(new Uri(response.RequestUri));
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, response, cancellationToken: cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetResponsePath(Uri uri)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        return Path.Combine(_replayDirectory, hash + ".json");
    }

    private async Task ApplyReplayLatencyAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (_sourceMode != HttpSourceMode.Replay)
        {
            return;
        }

        var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{_options.ReplayLatencySeed}:{uri.AbsoluteUri}"));
        var jitterRange = (uint)_options.ReplayLatencyJitterMilliseconds + 1;
        var jitter = BinaryPrimitives.ReadUInt32LittleEndian(seedBytes) % jitterRange;
        var delay = _options.ReplayLatencyFixedMilliseconds + (int)jitter;
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private sealed record RecordedHttpResponse(
        string RequestUri,
        int StatusCode,
        string ContentType,
        string Body,
        DateTimeOffset RetrievedAt);

    private sealed class RobotsRules
    {
        private readonly IReadOnlyList<(bool IsAllow, string Pattern)> _rules;

        private RobotsRules(IReadOnlyList<(bool IsAllow, string Pattern)> rules)
        {
            _rules = rules;
        }

        public static RobotsRules AllowAll { get; } = new(Array.Empty<(bool, string)>());

        public bool IsAllowed(string path)
        {
            var applicable = _rules
                .Where(rule => Matches(rule.Pattern, path))
                .OrderByDescending(rule => rule.Pattern.Length)
                .ToArray();

            if (applicable.Length == 0)
            {
                return true;
            }

            var longestMatch = applicable[0].Pattern.Length;
            return applicable
                .TakeWhile(rule => rule.Pattern.Length == longestMatch)
                .Any(rule => rule.IsAllow);
        }

        public static RobotsRules Parse(string content)
        {
            var groups = new List<(string[] Agents, (bool IsAllow, string Pattern)[] Rules)>();
            var agents = new List<string>();
            var groupRules = new List<(bool IsAllow, string Pattern)>();
            var groupHasDirective = false;

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Split('#', 2)[0].Trim();
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var directive = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (directive.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    if (groupHasDirective)
                    {
                        AddGroup();
                        agents.Clear();
                        groupRules.Clear();
                        groupHasDirective = false;
                    }

                    agents.Add(value);
                    continue;
                }

                groupHasDirective = true;
                if (directive.Equals("Disallow", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    groupRules.Add((false, value));
                }
                else if (directive.Equals("Allow", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    groupRules.Add((true, value));
                }
            }

            AddGroup();
            var specificAgentGroups = groups
                .SelectMany(group => group.Agents
                    .Where(agent => agent != "*" &&
                        UserAgent.Contains(agent, StringComparison.OrdinalIgnoreCase))
                    .Select(agent => (Agent: agent, Group: group)))
                .ToArray();
            var selectedGroups = specificAgentGroups.Length > 0
                ? specificAgentGroups
                    .Where(item => item.Agent.Length ==
                        specificAgentGroups.Max(candidate => candidate.Agent.Length))
                    .Select(item => item.Group)
                    .ToArray()
                : groups.Where(group => group.Agents.Contains("*", StringComparer.Ordinal)).ToArray();
            return new RobotsRules(selectedGroups.SelectMany(group => group.Rules).ToArray());

            void AddGroup()
            {
                if (agents.Count == 0)
                {
                    return;
                }

                groups.Add((agents.ToArray(), groupRules.ToArray()));
            }
        }

        private static bool Matches(string pattern, string path)
        {
            var anchored = pattern.EndsWith('$');
            var expression = Regex.Escape(anchored ? pattern[..^1] : pattern)
                .Replace("\\*", ".*", StringComparison.Ordinal);
            return Regex.IsMatch(
                path,
                "^" + expression + (anchored ? "$" : string.Empty),
                RegexOptions.CultureInvariant);
        }
    }
}

internal sealed class DomainRequestLimiter
{
    private readonly int _maximumConcurrentRequests;
    private readonly TimeSpan _minimumInterval;
    private readonly ConcurrentDictionary<string, DomainGate> _domains = new();

    public DomainRequestLimiter(int maximumConcurrentRequests, TimeSpan minimumInterval)
    {
        _maximumConcurrentRequests = maximumConcurrentRequests;
        _minimumInterval = minimumInterval;
    }

    public async Task<IDisposable> AcquireAsync(string domain, CancellationToken cancellationToken)
    {
        var gate = _domains.GetOrAdd(domain, _ => new DomainGate(_maximumConcurrentRequests));
        await gate.Concurrency.WaitAsync(cancellationToken);
        try
        {
            await gate.Pacing.WaitAsync(cancellationToken);
            try
            {
                var delay = gate.NextRequestAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                gate.NextRequestAt = DateTimeOffset.UtcNow + _minimumInterval;
            }
            finally
            {
                gate.Pacing.Release();
            }
        }
        catch
        {
            gate.Concurrency.Release();
            throw;
        }

        return new Permit(gate.Concurrency);
    }

    private sealed class DomainGate
    {
        public SemaphoreSlim Concurrency { get; }
        public SemaphoreSlim Pacing { get; } = new(1, 1);
        public DateTimeOffset NextRequestAt { get; set; }

        public DomainGate(int maximumConcurrentRequests)
        {
            Concurrency = new SemaphoreSlim(maximumConcurrentRequests, maximumConcurrentRequests);
        }
    }

    private sealed class Permit : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public Permit(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
        }
    }
}
