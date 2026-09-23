using System.Diagnostics;

namespace Netdb.Observability;

/// <summary>
/// Record the exception on the span AND set its status to Error.
/// </summary>
/// <remarks>
/// Both are required. Recording alone leaves the status Unset, so the span is
/// not counted as a failure and "error rate per endpoint" or "per query"
/// silently under-reports on the shared dashboard. One helper is the only way
/// this stays consistent across services.
/// </remarks>
public static class ActivityErrors
{
    /// <summary>Records the exception and marks the activity as failed.</summary>
    public static void RecordError(this Activity? activity, Exception exception)
    {
        if (activity is null) return;

        activity.AddException(exception);
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <summary>Records against <see cref="Activity.Current"/>.</summary>
    public static void RecordErrorOnCurrentActivity(Exception exception) =>
        Activity.Current.RecordError(exception);

    /// <summary>
    /// Runs <paramref name="action"/>, recording any exception with the correct
    /// status before rethrowing it unchanged.
    /// </summary>
    public static async Task WithErrorRecordingAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            RecordErrorOnCurrentActivity(ex);
            throw;
        }
    }

    /// <inheritdoc cref="WithErrorRecordingAsync(Func{Task})"/>
    public static async Task<T> WithErrorRecordingAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            RecordErrorOnCurrentActivity(ex);
            throw;
        }
    }
}
