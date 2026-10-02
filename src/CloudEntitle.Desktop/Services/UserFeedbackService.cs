using System.IO;
using System.Text.Json;

namespace CloudEntitle.Desktop.Services;

public interface IUserFeedbackService
{
    Task SubmitAsync(string feedback, string suggestions, CancellationToken cancellationToken = default);
}

public sealed record UserFeedbackSubmission(DateTime SubmittedAt, string Feedback, string Suggestions);

public sealed class LocalUserFeedbackService : IUserFeedbackService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LocalUserFeedbackService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM",
            "feedback",
            "submissions.jsonl");
    }

    public async Task SubmitAsync(string feedback, string suggestions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestions);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var submission = new UserFeedbackSubmission(DateTime.UtcNow, feedback.Trim(), suggestions.Trim());
        var serialized = JsonSerializer.Serialize(submission) + Environment.NewLine;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_filePath, serialized, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
