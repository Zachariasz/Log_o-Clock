using System.Windows;
using System.Windows.Threading;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Windows.Views;

namespace ProjectTimeTracker.Windows.Services;

public sealed class NotificationService(Dispatcher dispatcher) : INotificationService
{
    private Window? _active;
    private TargetReviewWindow? _targetReview;
    private bool _disposed;

    public async Task<ReminderResponse> ShowProjectReminderAsync(
        RecognitionCandidate candidate,
        IReadOnlyList<SavedTask> projectTasks,
        IReadOnlyList<TagDefinition> correlatedTags,
        IReadOnlyList<TagDefinition> availableTags,
        bool isProjectSwitch = false,
        Guid? suggestedTaskId = null,
        nint targetWindowHandle = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<ReminderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReminderWindow? window = null;
        await dispatcher.InvokeAsync(() =>
        {
            DismissActive();
            window = new ReminderWindow(
                candidate.Client.Name,
                candidate.Project.Name,
                candidate.Project.Color,
                projectTasks,
                correlatedTags,
                availableTags,
                isProjectSwitch,
                suggestedTaskId,
                targetWindowHandle);
            _active = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_active, window))
                {
                    _active = null;
                }

                completion.TrySetResult(new ReminderResponse(
                    window.Snoozed
                        ? ReminderResult.Snoozed
                        : window.Started
                            ? ReminderResult.Started
                            : ReminderResult.Dismissed,
                    window.Started ? window.SelectedTags : [],
                    window.Started ? window.SelectedTaskId : null,
                    window.Started ? window.TaskName : null,
                    window.Started ? window.Description : null));
            };
            window.Show();
        });
        using var registration = cancellationToken.Register(() => dispatcher.BeginInvoke(window!.Close));
        return await completion.Task;
    }

    public async Task<RecognitionCandidate?> ShowAmbiguousReminderAsync(
        IReadOnlyList<RecognitionCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<RecognitionCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProjectChooserWindow? window = null;
        await dispatcher.InvokeAsync(() =>
        {
            DismissActive();
            window = new ProjectChooserWindow(candidates);
            _active = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_active, window))
                {
                    _active = null;
                }

                completion.TrySetResult(window.SelectedCandidate);
            };
            window.Show();
        });
        using var registration = cancellationToken.Register(() => dispatcher.BeginInvoke(window!.Close));
        return await completion.Task;
    }

    public async Task ShowTargetReviewAsync(
        IReadOnlyList<TargetReviewItem> items,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (items.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (_targetReview is { IsVisible: true })
            {
                _targetReview.Activate();
                return;
            }

            var window = new TargetReviewWindow(items);
            _targetReview = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_targetReview, window))
                {
                    _targetReview = null;
                }
            };
            window.Show();
        });
    }

    public void DismissActive()
    {
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(DismissActive);
            return;
        }

        if (_active is null)
        {
            return;
        }

        var window = _active;
        _active = null;
        window.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DismissActive();
        if (dispatcher.CheckAccess())
        {
            _targetReview?.Close();
        }
        else
        {
            dispatcher.BeginInvoke(() => _targetReview?.Close());
        }
    }
}
