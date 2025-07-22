using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MusicTags.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MusicTags;

/// <summary>
/// Service for extracting ID3 tags from audio files and adding them as Jellyfin tags.
/// </summary>
public class MusicTagService(
    ILogger<MusicTagService> logger,
    ILibraryManager libraryManager,
    PluginConfiguration configuration)
{
    private readonly ILogger<MusicTagService> _logger = logger;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly PluginConfiguration _configuration = configuration;

    /// <summary>
    /// Processes ID3 tags for a specific audio item.
    /// </summary>
    /// <param name="audioItem">The audio item to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ProcessAudioItemAsync(Audio audioItem, CancellationToken cancellationToken)
    {
        try
        {
            if (audioItem == null || string.IsNullOrEmpty(audioItem.Path))
            {
                _logger.LogWarning("Audio item or path is null, skipping tag extraction");
                return;
            }

            if (!File.Exists(audioItem.Path))
            {
                _logger.LogWarning("Audio file does not exist: {Path}", audioItem.Path);
                return;
            }

            _logger.LogDebug("Processing ID3 tags for: {Name} ({Path})", audioItem.Name, audioItem.Path);
        
                    // Enhanced debugging for specific track
            if (audioItem.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogWarning("=== DETAILED SHIVERS DEBUGGING ===");
                _logger.LogWarning("File path: {Path}", audioItem.Path);
            }

            using var file = TagLib.File.Create(audioItem.Path);
            if (file == null)
            {
                _logger.LogWarning("Could not create TagLib file for: {Path}", audioItem.Path);
                return;
            }
            
            // Enhanced debugging for Shivers to see what tags are actually available
            if (audioItem.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogWarning("File type: {FileType}", file.GetType().Name);
                _logger.LogWarning("Mime type: {MimeType}", file.MimeType);
                
                // Check what tag types are available
                if (file.GetTag(TagLib.TagTypes.Id3v2) != null)
                {
                    _logger.LogWarning("✓ ID3v2 tags are available");
                    
                    // Show all available ID3v2 frames
                    if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
                    {
                        var frameIds = new List<string>();
                        foreach (var frame in id3v2Tag)
                        {
                            if (frame != null)
                            {
                                frameIds.Add(frame.FrameId.ToString());
                            }
                        }
                        _logger.LogWarning("ID3v2 frames: [{Frames}]", string.Join(", ", frameIds.Distinct()));
                        
                        // Test specific AB frames
                        var testFrames = new[] { "AB", "AB:GENRE", "AB:MOOD", "ABGE", "ABMO" };
                        foreach (var testFrame in testFrames)
                        {
                            var frames = id3v2Tag.GetFrames(testFrame);
                            if (frames.Any())
                            {
                                _logger.LogWarning("Found ID3v2 frame '{Frame}': {Count} instances", testFrame, frames.Count());
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("✗ No ID3v2 tags");
                }
                
                if (file.GetTag(TagLib.TagTypes.Xiph) != null)
                {
                    _logger.LogWarning("✓ Vorbis comments are available");
                }
                else
                {
                    _logger.LogWarning("✗ No Vorbis comments");
                }
                
                _logger.LogWarning("General tag BPM: {BPM}", file.Tag.BeatsPerMinute);
                _logger.LogWarning("=== END SHIVERS DEBUGGING ===");
            }

            // No longer removing configured tags since we moved to one-time removal

            var extractedTags = new List<string>();

            // Extract all configured tags
            if (!string.IsNullOrWhiteSpace(_configuration.TagNames))
            {
                var tags = ExtractConfiguredTags(file);
                extractedTags.AddRange(tags);
            }

            // Add extracted tags to the audio item
            if (extractedTags.Count > 0)
            {
                await AddTagsToAudioItemAsync(audioItem, extractedTags, cancellationToken).ConfigureAwait(false);
                
                _logger.LogDebug("Extracted {Count} tags from {Name}: {Tags}",
                    extractedTags.Count, audioItem.Name, string.Join(", ", extractedTags));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ID3 tags for {Name}", audioItem?.Name ?? "unknown");
        }
    }

    /// <summary>
    /// Extracts all configured tags from the audio file.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <returns>A list of tag values in "TagName:Value" format.</returns>
    private List<string> ExtractConfiguredTags(TagLib.File file)
    {
        var tags = new List<string>();

        try
        {
            // Parse the comma-separated tag names from configuration
            // Handle case where the entire string might be quoted
            var rawTagNames = _configuration.TagNames;
            if (rawTagNames.StartsWith('"') && rawTagNames.EndsWith('"'))
            {
                rawTagNames = rawTagNames[1..^1];
            }
            
            var tagNames = rawTagNames
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim().Trim('"', '\''))  // Remove quotes from individual tag names
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            // Debug: Show what tag names we're looking for
            _logger.LogDebug("Looking for tags: [{TagNames}]", string.Join(", ", tagNames));
            _logger.LogDebug("Raw TagNames from config: '{RawTagNames}'", _configuration.TagNames);

            // Debug: List all available tags once per file
            ListAvailableTags(file);
            
            foreach (var tagName in tagNames)
            {
                var value = ExtractTagValue(file, tagName);
                if (!string.IsNullOrEmpty(value))
                {
                    tags.Add($"{tagName}:{value}");
                }
                else
                {
                    _logger.LogDebug("No value found for tag '{TagName}'", tagName);
                }
            }


        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting configured tags");
        }

        return tags;
    }

    /// <summary>
    /// Extracts a specific tag value by name from the audio file.
    /// Supports both standard ID3 tags and custom ID3v2 frames.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="tagName">The name of the tag to extract.</param>
    /// <returns>The tag value if found, otherwise null.</returns>
    private string? ExtractTagValue(TagLib.File file, string tagName)
    {
        try
        {
            _logger.LogDebug("ExtractTagValue called with tagName: '{TagName}'", tagName);
            
            // Clean the tag name by removing quotes and trimming
            var cleanTagName = tagName.Replace("\"", "").Replace("'", "").Trim().ToUpperInvariant();
            _logger.LogDebug("Clean tag name: '{CleanTagName}'", cleanTagName);
            
            return cleanTagName switch
            {
                // Standard ID3 tags
                "ARTIST" => !string.IsNullOrEmpty(file.Tag.FirstPerformer) ? file.Tag.FirstPerformer : null,
                "ALBUM" => !string.IsNullOrEmpty(file.Tag.Album) ? file.Tag.Album : null,
                "GENRE" => !string.IsNullOrEmpty(file.Tag.FirstGenre) ? file.Tag.FirstGenre : null,
                "YEAR" => file.Tag.Year > 0 ? file.Tag.Year.ToString(CultureInfo.InvariantCulture) : null,
                "COMPOSER" => !string.IsNullOrEmpty(file.Tag.FirstComposer) ? file.Tag.FirstComposer : null,
                "BPM" => file.Tag.BeatsPerMinute > 0 ? file.Tag.BeatsPerMinute.ToString() : null,
                "PUBLISHER" => !string.IsNullOrEmpty(file.Tag.Publisher) ? file.Tag.Publisher : null,
                "COPYRIGHT" => !string.IsNullOrEmpty(file.Tag.Copyright) ? file.Tag.Copyright : null,
                "COMMENT" => !string.IsNullOrEmpty(file.Tag.Comment) ? file.Tag.Comment : null,
                
                // ID3v2 custom frames
                "KEY" => ExtractKeyTag(file),
                "MOOD" => ExtractId3v2TextFrame(file, "TMOO"),
                "CONTENTGROUP" => ExtractId3v2TextFrame(file, "TIT1"),
                "LANGUAGE" => ExtractId3v2TextFrame(file, "TLAN"),
                
                // Try to extract as a generic ID3v2 frame if not found above
                _ => ExtractId3v2TextFrame(file, tagName) ?? ExtractVorbisComment(file, tagName) ?? ExtractGenericTag(file, tagName)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting tag value for {TagName}", tagName);
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract a generic tag value using reflection.
    /// This allows for completely dynamic tag extraction.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="tagName">The name of the tag to extract.</param>
    /// <returns>The tag value if found, otherwise null.</returns>
    private string? ExtractGenericTag(TagLib.File file, string tagName)
    {
        try
        {
            // Try to find a property on the tag with the given name
            var property = file.Tag.GetType().GetProperty(tagName, 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            
            if (property != null)
            {
                var value = property.GetValue(file.Tag);
                if (value != null)
                {
                    var stringValue = value.ToString();
                    return !string.IsNullOrEmpty(stringValue) ? stringValue : null;
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract generic tag {TagName}", tagName);
            return null;
        }
    }

    /// <summary>
    /// Extracts a text frame from ID3v2 tags, handling multiple instances.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="frameId">The ID3v2 frame identifier.</param>
    /// <returns>The frame text value(s) if found, multiple values combined with commas, otherwise null.</returns>
    private string? ExtractId3v2TextFrame(TagLib.File file, string frameId)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
            {
                var values = new List<string>();
                
                // Get all frames with this ID
                var frames = id3v2Tag.GetFrames(frameId);
                _logger.LogDebug("Found {Count} frames with ID '{FrameId}'", frames.Count(), frameId);
                
                foreach (var frame in frames)
                {
                    if (frame is TagLib.Id3v2.TextInformationFrame textFrame)
                    {
                        // TextInformationFrame can contain multiple text values
                        foreach (var text in textFrame.Text)
                        {
                            if (!string.IsNullOrEmpty(text))
                            {
                                values.Add(text);
                                _logger.LogDebug("Frame '{FrameId}' contains value: '{Value}'", frameId, text);
                            }
                        }
                    }
                    else if (frame != null)
                    {
                        _logger.LogDebug("Frame '{FrameId}' is not a TextInformationFrame, it's: {FrameType}", frameId, frame.GetType().Name);
                    }
                }
                
                if (values.Count > 0)
                {
                    var result = string.Join(",", values.Distinct());
                    _logger.LogDebug("Combined result for '{FrameId}': '{Result}'", frameId, result);
                    return result;
                }
            }
            else
            {
                _logger.LogDebug("No ID3v2 tag found in file");
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting ID3v2 frame {FrameId}", frameId);
            return null;
        }
    }

    /// <summary>
    /// Extracts the musical key from various possible ID3v2 frame formats.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <returns>The key value if found, otherwise null.</returns>
    private string? ExtractKeyTag(TagLib.File file)
    {
        try
        {
            // Enhanced debugging for Shivers
            if (file.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogWarning("=== SHIVERS KEY TAG EXTRACTION DEBUGGING ===");
            }
            
            // First, try to get the key from standard TagLib properties
            // This should work for most audio formats
            var standardKey = file.Tag.InitialKey;
            if (!string.IsNullOrEmpty(standardKey))
            {
                _logger.LogDebug("Found KEY in standard TagLib properties: '{Value}'", standardKey);
                return standardKey;
            }
            
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
            {
                // Enhanced debugging for Shivers to show all available ID3v2 frames
                if (file.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogWarning("=== SHIVERS ID3V2 FRAME DEBUGGING ===");
                    
                    // List all available ID3v2 frames
                    var allFrames = id3v2Tag.GetFrames();
                    _logger.LogWarning("Total ID3v2 frames found: {Count}", allFrames.Count());
                    
                    foreach (var frame in allFrames)
                    {
                        if (frame != null)
                        {
                            _logger.LogWarning("ID3v2 frame: {FrameId} - Type: {FrameType}", frame.FrameId, frame.GetType().Name);
                            
                            // Try to extract text content from various frame types
                            if (frame is TagLib.Id3v2.TextInformationFrame textFrame)
                            {
                                var text = textFrame.Text.FirstOrDefault();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    _logger.LogWarning("  Text frame content: '{Content}'", text);
                                }
                            }
                            else if (frame is TagLib.Id3v2.UserTextInformationFrame userFrame)
                            {
                                var text = userFrame.Text.FirstOrDefault();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    _logger.LogWarning("  User text frame content: '{Content}'", text);
                                }
                            }
                        }
                    }
                    _logger.LogWarning("=== END SHIVERS ID3V2 FRAME DEBUGGING ===");
                }
                
                // Try multiple possible frame IDs for key information
                var possibleFrameIds = new[] { "TKEY", "TXXX", "WXXX" };
                
                foreach (var frameId in possibleFrameIds)
                {
                    var frames = id3v2Tag.GetFrames(frameId);
                    _logger.LogDebug("Found {Count} frames with ID '{FrameId}' for key extraction", frames.Count(), frameId);
                    
                    foreach (var frame in frames)
                    {
                        if (frame is TagLib.Id3v2.TextInformationFrame textFrame)
                        {
                            // For TKEY frames, the text should be the key directly
                            if (frameId == "TKEY")
                            {
                                foreach (var text in textFrame.Text)
                                {
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        _logger.LogDebug("Found TKEY frame with value: '{Value}'", text);
                                        return text;
                                    }
                                }
                            }
                        }
                        else if (frame is TagLib.Id3v2.UserTextInformationFrame userTextFrame)
                        {
                            // For TXXX frames, check if the description contains "key" or similar
                            var description = userTextFrame.Description?.ToLowerInvariant();
                            if (description != null && (description.Contains("key") || description.Contains("musical key")))
                            {
                                foreach (var text in userTextFrame.Text)
                                {
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        _logger.LogDebug("Found TXXX frame with key description '{Description}' and value: '{Value}'", 
                                            userTextFrame.Description, text);
                                        return text;
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Frame '{FrameId}' is of type: {FrameType}", frameId, frame.GetType().Name);
                        }
                    }
                }
                
            }
            
            // Also try to extract from Vorbis comments if available
            if (file.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogWarning("Trying Vorbis comments for KEY tag...");
            }
            
            // Try multiple possible KEY field names in Vorbis comments (including custom AB: prefixed tags)
            var possibleKeyFields = new[] { "KEY", "TKEY", "MUSICAL_KEY", "KEY_SIGNATURE", "INITIAL_KEY", "INITIALKEY", "AB:KEY", "AB KEY", "AB_KEY" };
            
            foreach (var keyField in possibleKeyFields)
            {
                var vorbisKey = ExtractVorbisComment(file, keyField);
                if (!string.IsNullOrEmpty(vorbisKey))
                {
                    _logger.LogDebug("Found KEY in Vorbis comments under field '{Field}': '{Value}'", keyField, vorbisKey);
                    return vorbisKey;
                }
            }
            
            if (file.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogWarning("No KEY found in any Vorbis comment fields: [{Fields}]", string.Join(", ", possibleKeyFields));
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting key tag");
            return null;
        }
    }

    /// <summary>
    /// Adds tags to the audio item.
    /// </summary>
    /// <param name="audioItem">The audio item.</param>
    /// <param name="tags">The tags to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task AddTagsToAudioItemAsync(Audio audioItem, List<string> tags, CancellationToken cancellationToken)
    {
        try
        {
            var existingTags = audioItem.Tags?.ToList() ?? [];
            var newTags = new List<string>();

            foreach (var tag in tags)
            {
                // Extract tag name (everything before the last colon, which is the separator)
                var lastColonIndex = tag.LastIndexOf(':');
                if (lastColonIndex == -1)
                {
                    // No colon found, skip this tag
                    _logger.LogWarning("Invalid tag format (no colon separator): {Tag}", tag);
                    continue;
                }
                
                var tagName = tag[..lastColonIndex];
                
                // Check if a tag with the same name already exists
                var existingTagWithSameName = existingTags.FirstOrDefault(t => 
                {
                    var existingLastColonIndex = t.LastIndexOf(':');
                    if (existingLastColonIndex == -1) return false;
                    var existingTagName = t[..existingLastColonIndex];
                    return existingTagName.Equals(tagName, StringComparison.OrdinalIgnoreCase);
                });
                
                if (existingTagWithSameName == null)
                {
                    // Tag name doesn't exist, add it
                    newTags.Add(tag);
                }
                else if (_configuration.OverwriteExistingTags)
                {
                    // Overwrite is enabled, remove existing tag and add new one
                    existingTags.RemoveAll(t => 
                    {
                        var existingLastColonIndex = t.LastIndexOf(':');
                        if (existingLastColonIndex == -1) return false;
                        var existingTagName = t[..existingLastColonIndex];
                        return existingTagName.Equals(tagName, StringComparison.OrdinalIgnoreCase);
                    });
                    newTags.Add(tag);
                }
                else
                {
                    // Overwrite is disabled and tag name exists, skip it
                    _logger.LogDebug("Skipping tag '{Tag}' for {Name} (overwrite disabled)", tag, audioItem.Name);
                }
            }

            if (newTags.Count > 0)
            {
                existingTags.AddRange(newTags);
                audioItem.Tags = [..existingTags];
                
                // Update the item in the library
                await _libraryManager.UpdateItemAsync(audioItem, audioItem, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                
                _logger.LogDebug("Added {Count} new tags to {Name}", newTags.Count, audioItem.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding tags to audio item {Name}", audioItem.Name);
        }
    }

    /// <summary>
    /// Removes specified tags from all audio items in the library.
    /// </summary>
    /// <param name="tagsToRemove">Comma-separated list of tag names to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RemoveTagsFromAllAudioItemsAsync(string tagsToRemove, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting bulk tag removal for tags: {TagsToRemove}", tagsToRemove);

            // Create query for all audio items
            var query = new InternalItemsQuery(null) // null user means system-wide query
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            };
            
            _logger.LogDebug("Querying library for audio items using InternalItemsQuery");
            var audioItems = _libraryManager.GetItemsResult(query).Items.OfType<Audio>().ToList();

            _logger.LogInformation("Found {Count} audio items to process for tag removal", audioItems.Count);

            if (audioItems.Count == 0)
            {
                _logger.LogInformation("No audio items found, skipping tag removal");
                return;
            }

            var processedCount = 0;
            foreach (var audioItem in audioItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Tag removal cancelled by user");
                    break;
                }

                await RemoveTagsAsync(audioItem, tagsToRemove, cancellationToken).ConfigureAwait(false);
                processedCount++;

                if (processedCount % 100 == 0)
                {
                    _logger.LogInformation("Processed {Count} audio items for tag removal", processedCount);
                }
            }

            _logger.LogInformation("Completed bulk tag removal. Processed {Count} audio items", processedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during bulk tag removal");
        }
    }

    /// <summary>
    /// Processes all audio items in the library.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ProcessAllAudioItemsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting bulk ID3 tag processing for all audio items");

            // Create query for all audio items
            var query = new InternalItemsQuery(null) // null user means system-wide query
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true
            };
            
            _logger.LogDebug("Querying library for audio items using InternalItemsQuery");
            var audioItems = _libraryManager.GetItemsResult(query).Items.OfType<Audio>().ToList();

            _logger.LogInformation("Found {Count} audio items to process", audioItems.Count);

            if (audioItems.Count == 0)
            {
                _logger.LogInformation("No audio items found, skipping processing");
                return;
            }

            var processedCount = 0;
            foreach (var audioItem in audioItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Music tag processing cancelled by user");
                    break;
                }

                await ProcessAudioItemAsync(audioItem, cancellationToken).ConfigureAwait(false);
                processedCount++;

                if (processedCount % 100 == 0)
                {
                    _logger.LogInformation("Processed {Count} audio items", processedCount);
                }
            }

            _logger.LogInformation("Completed bulk ID3 tag processing. Processed {Count} audio items", processedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during bulk ID3 tag processing");
        }
    }

    /// <summary>
    /// Removes specified tags from an audio item.
    /// </summary>
    /// <param name="audioItem">The audio item to process.</param>
    /// <param name="tagsToRemove">Comma-separated list of tag names to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task RemoveTagsAsync(Audio audioItem, string tagsToRemove, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tagsToRemove))
            {
                return; // No tags to remove
            }

            var existingTags = audioItem.Tags?.ToList() ?? [];
            var tagNamesToRemove = tagsToRemove
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            if (tagNamesToRemove.Count == 0)
            {
                return; // No valid tag names to remove
            }

            var removedCount = 0;
            var tagsToKeep = new List<string>();

            foreach (var existingTag in existingTags)
            {
                // Extract tag name (everything before the last colon)
                var lastColonIndex = existingTag.LastIndexOf(':');
                if (lastColonIndex == -1)
                {
                    // Tag doesn't have a colon, keep it
                    tagsToKeep.Add(existingTag);
                    continue;
                }

                var existingTagName = existingTag[..lastColonIndex];
                
                // Check if this tag should be removed
                var shouldRemove = tagNamesToRemove.Any(tagToRemove => 
                    tagToRemove.Equals(existingTagName, StringComparison.OrdinalIgnoreCase));

                if (shouldRemove)
                {
                    removedCount++;
                    _logger.LogDebug("Removing tag '{Tag}' from {Name}", existingTag, audioItem.Name);
                }
                else
                {
                    tagsToKeep.Add(existingTag);
                }
            }

            if (removedCount > 0)
            {
                audioItem.Tags = [..tagsToKeep];
                
                // Update the item in the library
                await _libraryManager.UpdateItemAsync(audioItem, audioItem, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                
                _logger.LogDebug("Removed {Count} tags from {Name}", removedCount, audioItem.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing tags from audio item {Name}", audioItem.Name);
        }
    }

    /// <summary>
    /// Extracts a tag from Vorbis comments (used by FLAC, OGG, etc.).
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="tagName">The name of the tag to extract.</param>
    /// <returns>The tag value if found, otherwise null.</returns>
    private string? ExtractVorbisComment(TagLib.File file, string tagName)
    {
        try
        {
            // Check if the file supports Vorbis comments (FLAC, OGG, etc.)
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment vorbisTag)
            {
                // Enhanced debugging for Shivers to show all available Vorbis fields
                if (file.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogWarning("=== SHIVERS VORBIS COMMENT DEBUGGING ===");
                    _logger.LogWarning("Looking for tag: '{TagName}'", tagName);
                    
                    // Show all available Vorbis comment field names
                    try
                    {
                        var fieldCount = vorbisTag.FieldCount;
                        _logger.LogWarning("Total Vorbis comment fields: {Count}", fieldCount);
                        
                        // Try to enumerate all available fields (this is a bit of a hack since XiphComment doesn't expose field names directly)
                        var allFieldNames = new List<string>();
                        
                        // Test common field names to see what's available
                        var commonFields = new[] { 
                            "TITLE", "ARTIST", "ALBUM", "DATE", "GENRE", "COMPOSER", "PERFORMER",
                            "KEY", "TKEY", "MUSICAL_KEY", "KEY_SIGNATURE", "INITIAL_KEY", "INITIALKEY",
                            "AB:KEY", "AB KEY", "AB_KEY", "AB:GENRE", "AB:MOOD", "AB GENRE", "AB MOOD",
                            "BPM", "TEMPO", "DISCNUMBER", "TRACKNUMBER"
                        };
                        
                        foreach (var testField in commonFields)
                        {
                            var testValues = vorbisTag.GetField(testField);
                            if (testValues != null && testValues.Length > 0 && testValues.Any(v => !string.IsNullOrEmpty(v)))
                            {
                                allFieldNames.Add(testField);
                                _logger.LogWarning("Available field '{Field}' with values: [{Values}]", testField, string.Join(", ", testValues));
                            }
                        }
                        
                        if (allFieldNames.Count == 0)
                        {
                            _logger.LogWarning("No common Vorbis comment fields found - file might use unusual field names");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error enumerating Vorbis comment fields");
                    }
                    
                    _logger.LogWarning("=== END SHIVERS VORBIS COMMENT DEBUGGING ===");
                }
                
                var values = new List<string>();
                
                // Get all values for this tag name (Vorbis comments can have multiple values)
                var commentValues = vorbisTag.GetField(tagName);
                if (commentValues != null && commentValues.Length > 0)
                {
                    foreach (var value in commentValues)
                    {
                        if (!string.IsNullOrEmpty(value))
                        {
                            values.Add(value);
                            _logger.LogDebug("Vorbis comment '{TagName}' contains value: '{Value}'", tagName, value);
                        }
                    }
                }
                
                if (values.Count > 0)
                {
                    var result = string.Join(",", values.Distinct());
                    _logger.LogDebug("Combined Vorbis comment result for '{TagName}': '{Result}'", tagName, result);
                    return result;
                }
                else
                {
                    _logger.LogDebug("No Vorbis comment found for tag '{TagName}'", tagName);
                }
            }
            else
            {
                _logger.LogDebug("No Vorbis comments found in file");
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting Vorbis comment {TagName}", tagName);
            return null;
        }
    }

    /// <summary>
    /// Lists all available ID3v2 frames and Vorbis comments in the file for debugging purposes.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    private void ListAvailableTags(TagLib.File file)
    {
        try
        {
            // List ID3v2 frames
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
            {
                var frameIds = new List<string>();
                foreach (var frame in id3v2Tag)
                {
                    if (frame != null)
                    {
                        frameIds.Add(frame.FrameId.ToString());
                    }
                }
                
                if (frameIds.Count > 0)
                {
                    var uniqueFrameIds = frameIds.Distinct().OrderBy(x => x).ToList();
                    _logger.LogDebug("Available ID3v2 frames: [{FrameIds}]", string.Join(", ", uniqueFrameIds));
                    
                    // Enhanced debugging for Shivers track
                    if (file.Name?.Contains("Shivers", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _logger.LogWarning("=== SHIVERS FRAME DETAILS ===");
                        foreach (var frame in id3v2Tag)
                        {
                            if (frame != null)
                            {
                                _logger.LogWarning("Frame ID: {FrameId}, Type: {FrameType}", 
                                    frame.FrameId, frame.GetType().Name);
                                
                                // If it's a TXXX frame, show the description
                                if (frame is TagLib.Id3v2.UserTextInformationFrame userTextFrame)
                                {
                                    _logger.LogWarning("TXXX frame description: '{Description}'", userTextFrame.Description);
                                    foreach (var text in userTextFrame.Text)
                                    {
                                        _logger.LogWarning("TXXX frame value: '{Value}'", text);
                                    }
                                }
                                // If it's a text frame, show the text
                                else if (frame is TagLib.Id3v2.TextInformationFrame textFrame)
                                {
                                    foreach (var text in textFrame.Text)
                                    {
                                        _logger.LogWarning("Text frame value: '{Value}'", text);
                                    }
                                }
                            }
                        }
                        _logger.LogWarning("=== END SHIVERS FRAME DETAILS ===");
                    }
                }
                else
                {
                    _logger.LogDebug("No ID3v2 frames found");
                }
            }
            else
            {
                _logger.LogDebug("No ID3v2 tags found");
            }
            
            // List Vorbis comments (FLAC, OGG, etc.)
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment vorbisTag)
            {
                _logger.LogDebug("Vorbis comments found - will attempt to extract requested tags");
                
                // Test some common AB fields specifically
                var testFields = new[] { "AB", "AB:GENRE", "AB:MOOD", "AB GENRE", "AB MOOD", "AB [GENRE]", "AB [MOOD]" };
                foreach (var testField in testFields)
                {
                    var values = vorbisTag.GetField(testField);
                    if (values != null && values.Length > 0)
                    {
                        _logger.LogDebug("Found Vorbis field '{Field}' with {Count} values: [{Values}]", 
                            testField, values.Length, string.Join(", ", values));
                    }
                }
            }
            else
            {
                _logger.LogDebug("No Vorbis comments found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error listing available tags");
        }
    }
} 