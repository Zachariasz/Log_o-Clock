using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed class TrelloSyncService : ITrelloSyncService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(15);
    private readonly ITrackerStore _store;
    private readonly ITrelloApiClient _apiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly Guid _profileId;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _periodicTask;

    public TrelloSyncService(
        ITrackerStore store,
        ITrelloApiClient apiClient,
        ICredentialStore credentialStore,
        Guid profileId,
        IClock clock)
    {
        _store = store;
        _apiClient = apiClient;
        _credentialStore = credentialStore;
        _profileId = profileId;
        _clock = clock;
    }

    public event EventHandler<TrelloSyncResult>? SyncCompleted;

    public Uri CreateAuthorizationUri(string apiKey) => _apiClient.CreateAuthorizationUri(apiKey);

    public Task<TrelloConnection?> GetConnectionAsync(CancellationToken cancellationToken = default) =>
        _store.GetTrelloConnectionAsync(cancellationToken);

    public async Task<TrelloMember> ConnectAsync(
        string apiKey,
        string token,
        CancellationToken cancellationToken = default)
    {
        var credentials = new TrelloCredentials(apiKey.Trim(), token.Trim());
        var member = await _apiClient.GetCurrentMemberAsync(credentials, cancellationToken);
        await _credentialStore.SetTrelloCredentialsAsync(_profileId, credentials, cancellationToken);
        try
        {
            await _store.SaveTrelloConnectionAsync(
                new TrelloConnection(member.Id, member.Username, member.DisplayName),
                cancellationToken);
        }
        catch
        {
            await _credentialStore.DeleteTrelloCredentialsAsync(_profileId, cancellationToken);
            throw;
        }

        return member;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _credentialStore.DeleteTrelloCredentialsAsync(_profileId, cancellationToken);
        await _store.ClearTrelloConnectionAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrelloBoard>> GetBoardsAsync(CancellationToken cancellationToken = default) =>
        await _apiClient.GetBoardsAsync(await RequireCredentialsAsync(cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<TrelloList>> GetListsAsync(
        string boardId,
        CancellationToken cancellationToken = default) =>
        await _apiClient.GetListsAsync(await RequireCredentialsAsync(cancellationToken), boardId, cancellationToken);

    public Task<IReadOnlyList<TrelloBoardMapping>> GetMappingsAsync(CancellationToken cancellationToken = default) =>
        _store.GetTrelloBoardMappingsAsync(cancellationToken);

    public async Task SaveMappingAsync(
        TrelloBoardMapping mapping,
        CancellationToken cancellationToken = default)
    {
        await _store.UpsertTrelloBoardMappingAsync(mapping, cancellationToken);
        _ = await SyncNowAsync(cancellationToken);
    }

    public Task RemoveMappingAsync(Guid mappingId, CancellationToken cancellationToken = default) =>
        _store.RemoveTrelloBoardMappingAsync(mappingId, cancellationToken);

    public async Task<TrelloSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await _store.GetTrelloConnectionAsync(cancellationToken)
                ?? throw new InvalidOperationException("Connect this profile to Trello first.");
            var credentials = await RequireCredentialsAsync(cancellationToken);
            var mappings = await _store.GetTrelloBoardMappingsAsync(cancellationToken);
            var total = new TrelloSyncResult(0, 0, 0, 0, 0, _clock.UtcNow);
            foreach (var mapping in mappings)
            {
                var selectedLists = mapping.Lists
                    .Select(list => list.ListId)
                    .ToHashSet(StringComparer.Ordinal);
                var cards = (await _apiClient.GetCardsAsync(credentials, mapping.BoardId, cancellationToken))
                    .Where(card => selectedLists.Contains(card.ListId))
                    .Where(card => card.MemberIds.Contains(connection.MemberId, StringComparer.Ordinal))
                    .ToArray();
                var result = await _store.ReconcileTrelloBoardAsync(
                    mapping.Id,
                    cards,
                    _clock.UtcNow,
                    cancellationToken);
                total = total with
                {
                    MappingCount = total.MappingCount + 1,
                    ImportedCount = total.ImportedCount + result.ImportedCount,
                    UpdatedCount = total.UpdatedCount + result.UpdatedCount,
                    DetachedCount = total.DetachedCount + result.DetachedCount,
                    DeletedCount = total.DeletedCount + result.DeletedCount,
                    CompletedUtc = result.CompletedUtc,
                };
            }

            var completed = _clock.UtcNow;
            total = total with { CompletedUtc = completed };
            await _store.UpdateTrelloSyncStatusAsync(completed, null, false, cancellationToken);
            SyncCompleted?.Invoke(this, total);
            return total;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var requiresReconnect = exception is TrelloAuthenticationException;
            await _store.UpdateTrelloSyncStatusAsync(
                null,
                SanitizeError(exception.Message),
                requiresReconnect,
                CancellationToken.None);
            throw;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public void Start()
    {
        if (_periodicTask is not null)
        {
            return;
        }

        _periodicTask = RunPeriodicAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_periodicTask is not null)
        {
            try
            {
                await _periodicTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        _syncGate.Dispose();
    }

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        await TryBackgroundSyncAsync(cancellationToken);
        using var timer = new PeriodicTimer(SyncInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await TryBackgroundSyncAsync(cancellationToken);
        }
    }

    private async Task TryBackgroundSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _store.GetTrelloConnectionAsync(cancellationToken) is not null)
            {
                _ = await SyncNowAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The status is persisted by SyncNowAsync. Background synchronization is intentionally quiet.
        }
    }

    private async Task<TrelloCredentials> RequireCredentialsAsync(CancellationToken cancellationToken) =>
        await _credentialStore.GetTrelloCredentialsAsync(_profileId, cancellationToken)
        ?? throw new TrelloAuthenticationException("Trello credentials are missing. Connect the profile again.");

    private static string SanitizeError(string message)
    {
        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine)
            ? "Trello synchronization failed."
            : firstLine.Length <= 240 ? firstLine : firstLine[..240];
    }
}
