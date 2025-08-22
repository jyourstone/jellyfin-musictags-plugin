using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MusicTags.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MusicTags.ScheduleTasks;

/// <summary>
/// Scheduled task to process music tags for all audio items.
/// </summary>
public class ProcessMusicTagsTask(
    ILogger<ProcessMusicTagsTask> logger,
    ILibraryManager libraryManager,
    ILoggerFactory loggerFactory) : IScheduledTask
{
    private readonly ILogger<ProcessMusicTagsTask> _logger = logger;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    /// <inheritdoc />
    public string Name => "Process Music Tags";

    /// <inheritdoc />
    public string Key => "ProcessMusicTags";

    /// <inheritdoc />
    public string Description => "Extracts ID3 tags from audio files and adds them as Jellyfin tags";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting scheduled music tag processing task");

            // Create the MusicTagService manually with the correct logger type
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var serviceLogger = _loggerFactory.CreateLogger<MusicTagService>();
            var musicTagService = new MusicTagService(serviceLogger, _libraryManager, configuration);

            // Use the service to process all audio items
            await musicTagService.ProcessAllAudioItemsAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Completed scheduled music tag processing task");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scheduled music tag processing");
            throw;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks // Run at 2 AM
            }
        ];
    }
} 