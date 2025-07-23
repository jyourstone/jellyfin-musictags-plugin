using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MusicTags.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MusicTags.Api;

/// <summary>
/// API controller for MusicTags plugin.
/// </summary>
[ApiController]
[Route("MusicTags")]
[Authorize]
public class MusicTagsController(
    ILogger<MusicTagsController> logger,
    ILibraryManager libraryManager,
    ITaskManager taskManager,
    ILoggerFactory loggerFactory) : ControllerBase
{
    private readonly ILogger<MusicTagsController> _logger = logger;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ITaskManager _taskManager = taskManager;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    
    // Semaphore to handle tag removal concurrency
    private static readonly SemaphoreSlim _tagRemovalSemaphore = new(1, 1);

    /// <summary>
    /// Gets the plugin status and statistics.
    /// </summary>
    /// <returns>The plugin status.</returns>
    [HttpGet("Status")]
    public ActionResult<PluginStatus> GetStatus()
    {
        try
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            
            // Check if the MusicTags processing task is currently running
            var isTaskRunning = false;
            var refreshTask = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "ProcessMusicTags");
            if (refreshTask != null)
            {
                isTaskRunning = refreshTask.State == TaskState.Running;
            }

            var status = new PluginStatus
            {
                TagNames = configuration.TagNames,
                OverwriteExistingTags = configuration.OverwriteExistingTags,
                IsTaskRunning = isTaskRunning,
                IsTagRemovalInProgress = _tagRemovalSemaphore.CurrentCount == 0
            };

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plugin status");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Triggers the scheduled task to process ID3 tags for all audio items in the library.
    /// </summary>
    /// <returns>The processing result.</returns>
    [HttpPost("ProcessAll")]
    public ActionResult<ProcessingResult> ProcessAllAudioItems()
    {
        try
        {
            _logger.LogInformation("Manual bulk tag processing requested");

            var refreshTask = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "ProcessMusicTags");
            if (refreshTask != null)
            {
                // Check if the task is already running
                if (refreshTask.State == TaskState.Running)
                {
                    _logger.LogWarning("MusicTags processing task is already running");
                    var runningResult = new ProcessingResult
                    {
                        Success = false,
                        Message = "Music tag processing is already running. Check server logs for progress.",
                        Timestamp = DateTime.UtcNow
                    };

                    return Conflict(runningResult);
                }

                // Check if tag removal is already in progress
                if (_tagRemovalSemaphore.CurrentCount == 0)
                {
                    _logger.LogWarning("Tag removal is already in progress, cannot start processing task");
                    var runningResult = new ProcessingResult
                    {
                        Success = false,
                        Message = "Tag removal is already in progress. Check server logs for progress.",
                        Timestamp = DateTime.UtcNow
                    };

                    return Conflict(runningResult);
                }

                _logger.LogInformation("Triggering MusicTags processing task");
                _taskManager.Execute(refreshTask, new TaskOptions());
                
                var successResult = new ProcessingResult
                {
                    Success = true,
                    Message = "Tag processing started, this can take a while if you have a large music library.",
                    Timestamp = DateTime.UtcNow
                };

                return Ok(successResult);
            }
            else
            {
                _logger.LogWarning("MusicTags processing task not found");
                var result = new ProcessingResult
                {
                    Success = false,
                    Message = "MusicTags processing task not found",
                    Timestamp = DateTime.UtcNow
                };

                return StatusCode(500, result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering MusicTags processing task");
            
            var result = new ProcessingResult
            {
                Success = false,
                Message = $"Error triggering processing task: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };

            return StatusCode(500, result);
        }
    }

    /// <summary>
    /// Removes specified tags from all audio items in the library.
    /// </summary>
    /// <param name="request">The request containing tags to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The processing result.</returns>
    [HttpPost("RemoveTags")]
    public async Task<ActionResult<ProcessingResult>> RemoveTags([FromBody] RemoveTagsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.TagsToRemove))
            {
                return BadRequest(new ProcessingResult
                {
                    Success = false,
                    Message = "No tags specified for removal",
                    Timestamp = DateTime.UtcNow
                });
            }

            // Check if the MusicTags processing task is already running
            var refreshTask = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "ProcessMusicTags");
            if (refreshTask != null && refreshTask.State == TaskState.Running)
            {
                _logger.LogWarning("MusicTags processing task is already running, cannot start tag removal");
                var runningResult = new ProcessingResult
                {
                    Success = false,
                    Message = "Music tag processing is already running. Check server logs for progress.",
                    Timestamp = DateTime.UtcNow
                };

                return Conflict(runningResult);
            }

            // Try to acquire the semaphore to start tag removal
            if (!await _tagRemovalSemaphore.WaitAsync(0, cancellationToken))
            {
                _logger.LogWarning("Tag removal is already in progress, cannot start another removal operation");
                var runningResult = new ProcessingResult
                {
                    Success = false,
                    Message = "Tag removal is already in progress. Check server logs for progress.",
                    Timestamp = DateTime.UtcNow
                };

                return Conflict(runningResult);
            }

            try
            {
                
                _logger.LogInformation("Manual tag removal requested for tags: {TagsToRemove}", request.TagsToRemove);

                // Create the MusicTagService with the current configuration
                var serviceLogger = _loggerFactory.CreateLogger<MusicTagService>();
                var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                var musicTagService = new MusicTagService(serviceLogger, _libraryManager, configuration);

                // Remove the specified tags from all audio items
                await musicTagService.RemoveTagsFromAllAudioItemsAsync(request.TagsToRemove, cancellationToken).ConfigureAwait(false);
                
                var result = new ProcessingResult
                {
                    Success = true,
                    Message = $"Successfully removed tags: {request.TagsToRemove}",
                    Timestamp = DateTime.UtcNow
                };

                return Ok(result);
            }
            finally
            {
                // Always release the semaphore when the operation completes (success or failure)
                _tagRemovalSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing tags: {TagsToRemove}", request.TagsToRemove);
            
            var result = new ProcessingResult
            {
                Success = false,
                Message = $"Error removing tags: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };

            return StatusCode(500, result);
        }
    }


}

/// <summary>
/// Request model for removing tags.
/// </summary>
public class RemoveTagsRequest
{
    /// <summary>
    /// Gets or sets the comma-separated list of tag names to remove.
    /// </summary>
    public string TagsToRemove { get; set; } = string.Empty;
}

/// <summary>
/// Plugin status information.
/// </summary>
public class PluginStatus
{
    /// <summary>
    /// Gets or sets the comma-separated list of tag names to extract.
    /// </summary>
    public string TagNames { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether existing tags should be overwritten.
    /// </summary>
    public bool OverwriteExistingTags { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the MusicTags processing task is currently running.
    /// </summary>
    public bool IsTaskRunning { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether tag removal is currently in progress.
    /// </summary>
    public bool IsTagRemovalInProgress { get; set; }
}

/// <summary>
/// Processing result information.
/// </summary>
public class ProcessingResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the processing was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the processing timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }
} 