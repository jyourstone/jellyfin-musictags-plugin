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
    /// Comprehensive mapping of friendly tag names to their ID3v2 frame identifiers.
    /// This allows users to use intuitive names (e.g., "FILETYPE") that work consistently
    /// across file formats, automatically translating to the correct ID3v2 frame ID (e.g., "TFLT").
    /// 
    /// Note: Some entries are intentional aliases (e.g., "KEY" and "INITIALKEY" both map to "TKEY")
    /// to provide flexibility and support different tagging conventions.
    /// </summary>
    private static readonly Dictionary<string, string> TagNameToFrameId = new(StringComparer.OrdinalIgnoreCase)
    {
        // Audio file and technical information
        { "FILETYPE", "TFLT" },
        { "MEDIATYPE", "TMED" },
        { "ENCODEDBY", "TENC" },
        { "ENCODERSETTINGS", "TSSE" },
        { "LENGTH", "TLEN" },
        
        // Content descriptors
        { "CONTENTTYPE", "TCON" },  // Genre
        { "CONTENTGROUP", "TIT1" },  // Content group description
        { "SUBTITLE", "TIT3" },  // Subtitle/Description refinement
        { "LANGUAGE", "TLAN" },
        { "MOOD", "TMOO" },
        
        // Musical key and tempo
        { "KEY", "TKEY" },
        { "INITIALKEY", "TKEY" },
        { "BPM", "TBPM" },
        
        // People and organizations
        { "ORIGINALARTIST", "TOPE" },
        { "LYRICIST", "TEXT" },
        { "COMPOSER", "TCOM" },
        { "CONDUCTOR", "TPE3" },
        { "REMIXER", "TPE4" },
        { "INVOLVEDPEOPLE", "TIPL" },
        { "MUSICIANCREDITS", "TMCL" },
        { "BAND", "TPE2" },  // Band/Orchestra/Accompaniment
        
        // Original release information
        { "ORIGINALALBUM", "TOAL" },
        { "ORIGINALFILENAME", "TOFN" },
        { "ORIGINALYEAR", "TORY" },
        { "ORIGINALDATE", "TDOR" },
        { "ORIGINALLYRICIST", "TOLY" },
        
        // Rights and legal
        { "COPYRIGHT", "TCOP" },
        { "PUBLISHER", "TPUB" },
        { "OWNER", "TOWN" },
        { "PRODUCEDNOTICE", "TPRO" },
        
        // Identifiers
        { "ISRC", "TSRC" },  // International Standard Recording Code
        { "RADIOSTATION", "TRSN" },
        { "RADIOSTATIONOWNER", "TRSO" },
        
        // Sorting and organization
        { "ALBUMSORTORDER", "TSOA" },
        { "PERFORMERSORTORDER", "TSOP" },
        { "TITLESORTORDER", "TSOT" },
        { "ALBUMARTISTSORT", "TSO2" },
        { "COMPOSERSORT", "TSOC" },
        
        // File ownership and licensing
        { "FILEOWNER", "TOWN" },
        { "INTERNETRADIOSTATION", "TRSN" },
        
        // Set information (for multi-disc sets)
        { "SETSUBTITLE", "TSST" },
        
        // Playlist delay
        { "PLAYLISTDELAY", "TDLY" },
    };

    /// <summary>
    /// Removes surrounding quotes from a string only if both ends have matching quotes.
    /// This prevents accidental data loss from values that intentionally start or end with quotes.
    /// </summary>
    /// <param name="value">The string to process.</param>
    /// <returns>The string with matching surrounding quotes removed, or the original string if no matching quotes.</returns>
    private static string RemoveSurroundingQuotes(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2)
        {
            return value;
        }

        // Check for matching double quotes
        if (value.StartsWith("\"") && value.EndsWith("\""))
        {
            return value[1..^1];
        }

        // Check for matching single quotes
        if (value.StartsWith("'") && value.EndsWith("'"))
        {
            return value[1..^1];
        }

        // No matching quotes, return as-is
        return value;
    }

    /// <summary>
    /// Calculates the maximum concurrency level based on processor count.
    /// </summary>
    /// <returns>Max concurrency: 1 if ProcessorCount &lt;= 3, otherwise ProcessorCount - 3, capped at 32.</returns>
    private static int CalculateMaxConcurrency()
    {
        var processorCount = Environment.ProcessorCount;
        return Math.Min(32, Math.Max(1, processorCount - 3));
    }

    /// <summary>
    /// Processes a collection of items in parallel with semaphore-based concurrency control and progress reporting.
    /// </summary>
    /// <typeparam name="T">The type of items to process.</typeparam>
    /// <param name="items">The collection of items to process.</param>
    /// <param name="itemName">The name of the item type for logging (e.g., "albums", "artists").</param>
    /// <param name="processAction">The action to perform on each item. Returns the item if it was modified, null otherwise.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessItemsInParallelAsync<T>(
        IReadOnlyList<T> items,
        string itemName,
        Func<T, CancellationToken, Task<T?>> processAction,
        CancellationToken cancellationToken) where T : BaseItem
    {
        if (items.Count == 0)
        {
            return;
        }

        var maxConcurrency = CalculateMaxConcurrency();
        var processorCount = Environment.ProcessorCount;
        
        _logger.LogInformation("Processing {ItemName} with {MaxConcurrency} concurrent operations (ProcessorCount: {ProcessorCount})", 
            itemName, maxConcurrency, processorCount);

        SemaphoreSlim? semaphore = null;
        try
        {
            semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var processedCount = 0;
            var itemsToUpdate = new System.Collections.Concurrent.ConcurrentBag<T>();

            var processingTasks = items.Select(async item =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var semaphoreAcquired = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    semaphoreAcquired = true;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Process the item
                    var modifiedItem = await processAction(item, cancellationToken).ConfigureAwait(false);
                    if (modifiedItem != null)
                    {
                        itemsToUpdate.Add(modifiedItem);
                    }

                    // Thread-safe progress reporting using Interlocked
                    var currentCount = Interlocked.Increment(ref processedCount);
                    if (currentCount % 100 == 0)
                    {
                        _logger.LogInformation("Processed {Count}/{Total} {ItemName} ({Percentage:F1}%)",
                            currentCount, items.Count, currentCount * 100.0 / items.Count, itemName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing {ItemName} '{Name}'", itemName, item.Name);
                }
                finally
                {
                    if (semaphoreAcquired)
                    {
                        semaphore.Release();
                    }
                }
            });

            await Task.WhenAll(processingTasks).ConfigureAwait(false);

            // Batch update all modified items
            if (itemsToUpdate.Count > 0)
            {
                _logger.LogInformation("Performing batch database update for {Count} modified {ItemName}", itemsToUpdate.Count, itemName);

                var updateTasks = itemsToUpdate.Select(async item =>
                {
                    try
                    {
                        await _libraryManager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating {ItemName} {Name}", itemName, item.Name);
                    }
                });

                await Task.WhenAll(updateTasks).ConfigureAwait(false);
                _logger.LogInformation("Completed batch database update for {ItemName}", itemName);
            }

            _logger.LogInformation("Completed processing {ItemName}. Processed {Count} items, updated {UpdatedCount} items", 
                itemName, processedCount, itemsToUpdate.Count);
        }
        finally
        {
            semaphore?.Dispose();
        }
    }

    /// <summary>
    /// Processes ID3 tags for a specific audio item (backwards compatibility method).
    /// </summary>
    /// <param name="audioItem">The audio item to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ProcessAudioItemAsync(Audio audioItem, CancellationToken cancellationToken)
    {
        var wasModified = ProcessAudioItemInternal(audioItem);
        if (wasModified)
        {
            await _libraryManager.UpdateItemAsync(audioItem, audioItem, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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
                    // Split the value based on configured delimiters if any
                    var splitValues = SplitTagValue(value);
                    
                    // Add each split value as a separate tag with the same tag name
                    foreach (var splitValue in splitValues)
                    {
                        if (!string.IsNullOrWhiteSpace(splitValue))
                        {
                            tags.Add($"{tagName}:{splitValue}");
                        }
                    }
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
    /// Splits a tag value based on configured delimiters.
    /// </summary>
    /// <param name="value">The tag value to split.</param>
    /// <returns>A list of split values. If no delimiters are configured, returns the original value in a list.</returns>
    private List<string> SplitTagValue(string value)
    {
        try
        {
            // If no delimiters are configured, return the original value
            if (string.IsNullOrWhiteSpace(_configuration.TagDelimiters))
            {
                return [value];
            }

            // Get individual delimiter characters from the configuration string
            var delimiters = _configuration.TagDelimiters.ToCharArray();
            
            _logger.LogDebug("Splitting tag value '{Value}' using delimiters: [{Delimiters}]", 
                value, string.Join(", ", delimiters.Select(d => $"'{d}'")));

            // Split the value on any of the configured delimiters
            var splitValues = value
                .Split(delimiters, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (splitValues.Count > 1)
            {
                _logger.LogDebug("Split '{Value}' into {Count} values: [{SplitValues}]", 
                    value, splitValues.Count, string.Join(", ", splitValues));
            }

            return splitValues;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error splitting tag value '{Value}', returning original value", value);
            return [value];
        }
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
                
                // Try to extract from different tag types based on tag name format
                _ => ExtractCustomTag(file, cleanTagName)
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
                    if (!string.IsNullOrEmpty(stringValue))
                    {
                        return RemoveSurroundingQuotes(stringValue);
                    }
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
                                // Remove surrounding quotes only if both ends have matching quotes
                                var cleanText = RemoveSurroundingQuotes(text);
                                values.Add(cleanText);
                                _logger.LogDebug("Frame '{FrameId}' contains value: '{Value}'", frameId, cleanText);
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

            
            // First, try to get the key from standard TagLib properties
            // This should work for most audio formats
            var standardKey = file.Tag.InitialKey;
            if (!string.IsNullOrEmpty(standardKey))
            {
                var cleanKey = RemoveSurroundingQuotes(standardKey);
                _logger.LogDebug("Found KEY in standard TagLib properties: '{Value}'", cleanKey);
                return cleanKey;
            }
            
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
            {

                
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
                                        var cleanText = RemoveSurroundingQuotes(text);
                                        _logger.LogDebug("Found TKEY frame with value: '{Value}'", cleanText);
                                        return cleanText;
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
                                        var cleanText = RemoveSurroundingQuotes(text);
                                        _logger.LogDebug("Found TXXX frame with key description '{Description}' and value: '{Value}'", 
                                            userTextFrame.Description, cleanText);
                                        return cleanText;
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
            

            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting key tag");
            return null;
        }
    }

    /// <summary>
    /// Extracts custom tags by trying different extraction methods based on tag name format.
    /// Tries multiple sources: Vorbis comments (FLAC/OGG), Apple/iTunes tags (M4A/MP4),
    /// ASF tags (WMA), TXXX frames (custom MP3 tags), mapped standard frames (via dictionary),
    /// direct frame IDs, and generic extraction.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="tagName">The name of the tag to extract.</param>
    /// <returns>The tag value if found, otherwise null.</returns>
    private string? ExtractCustomTag(TagLib.File file, string tagName)
    {
        try
        {
            // First try Vorbis comments (for FLAC, OGG, OPUS files)
            // Custom tags and standard tags both work the same way in Vorbis
            var vorbisResult = ExtractVorbisComment(file, tagName);
            if (!string.IsNullOrEmpty(vorbisResult))
            {
                return vorbisResult;
            }

            // Try Apple/iTunes tags (for M4A, MP4, AAC files)
            var appleResult = ExtractAppleTag(file, tagName);
            if (!string.IsNullOrEmpty(appleResult))
            {
                return appleResult;
            }

            // Try ASF tags (for WMA files)
            var asfResult = ExtractAsfTag(file, tagName);
            if (!string.IsNullOrEmpty(asfResult))
            {
                return asfResult;
            }

            // Try TXXX frames (User Text Information) for custom tags in MP3/WAV files
            // This is where MP3Tag stores custom tags that don't have standard frame IDs
            var userTextResult = ExtractId3v2UserTextFrame(file, tagName);
            if (!string.IsNullOrEmpty(userTextResult))
            {
                return userTextResult;
            }

            // Check if this tag name has a known ID3v2 frame mapping
            // This allows friendly names like "FILETYPE" to map to standard frames like "TFLT"
            if (TagNameToFrameId.TryGetValue(tagName, out var frameId))
            {
                var mappedResult = ExtractId3v2TextFrame(file, frameId);
                if (!string.IsNullOrEmpty(mappedResult))
                {
                    return mappedResult;
                }
            }

            // Try the tag name itself if it looks like an ID3v2 frame ID (4 characters)
            // This allows users to use frame IDs directly (e.g., "TFLT", "TMOO")
            // Ensure frame ID is uppercase as required by ID3v2 specification
            if (tagName.Length == 4 && tagName.All(char.IsLetterOrDigit))
            {
                var frameIdUpper = tagName.ToUpperInvariant();
                var id3Result = ExtractId3v2TextFrame(file, frameIdUpper);
                if (!string.IsNullOrEmpty(id3Result))
                {
                    return id3Result;
                }
            }

            // Finally try generic extraction via reflection
            return ExtractGenericTag(file, tagName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error extracting custom tag {TagName}", tagName);
            return null;
        }
    }

    /// <summary>
    /// Extracts a tag from Apple/iTunes tags (used by M4A, MP4, AAC files).
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="tagName">The name of the tag to extract.</param>
    /// <returns>The tag value if found, otherwise null.</returns>
    private string? ExtractAppleTag(TagLib.File file, string tagName)
    {
        try
        {
            // Check if the file supports Apple tags (M4A, MP4, AAC, etc.)
            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag)
            {
                var values = new List<string>();
                
                // Try to get the field by name via DataBoxes
                var dataBoxes = appleTag.DataBoxes(tagName);
                if (dataBoxes != null)
                {
                    foreach (var box in dataBoxes)
                    {
                        if (box != null)
                        {
                            var text = box.Text;
                            if (!string.IsNullOrEmpty(text))
                            {
                                var cleanValue = RemoveSurroundingQuotes(text);
                                values.Add(cleanValue);
                                _logger.LogDebug("Apple tag '{TagName}' contains value: '{Value}'", tagName, cleanValue);
                            }
                        }
                    }
                }
                
                if (values.Count > 0)
                {
                    var result = string.Join(",", values.Distinct());
                    _logger.LogDebug("Combined Apple tag result for '{TagName}': '{Result}'", tagName, result);
                    return result;
                }
                
                // Try common Apple freeform tag format (used by some tagging applications)
                var freeformKey = $"----:com.apple.iTunes:{tagName}";
                var freeformBoxes = appleTag.DataBoxes(freeformKey);
                if (freeformBoxes != null && freeformBoxes.Any())
                {
                    foreach (var box in freeformBoxes)
                    {
                        if (box != null)
                        {
                            var text = box.Text;
                            if (!string.IsNullOrEmpty(text))
                            {
                                var cleanValue = RemoveSurroundingQuotes(text);
                                values.Add(cleanValue);
                                _logger.LogDebug("Apple freeform tag '{Key}' contains value: '{Value}'", freeformKey, cleanValue);
                            }
                        }
                    }
                }
                
                if (values.Count > 0)
                {
                    var result = string.Join(",", values.Distinct());
                    _logger.LogDebug("Combined Apple freeform tag result for '{TagName}': '{Result}'", tagName, result);
                    return result;
                }
            }
            else
            {
                _logger.LogDebug("No Apple tags found in file");
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting Apple tag {TagName}", tagName);
            return null;
        }
    }

    /// <summary>
    /// Extracts a tag from ASF tags (used by WMA files).
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="tagName">The name of the tag to extract.</param>
    /// <returns>The tag value if found, otherwise null.</returns>
    private string? ExtractAsfTag(TagLib.File file, string tagName)
    {
        try
        {
            // Check if the file supports ASF tags (WMA files)
            if (file.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asfTag)
            {
                var values = new List<string>();
                
                // Try to get the attribute value
                var descriptors = asfTag.GetDescriptors(tagName);
                if (descriptors != null && descriptors.Any())
                {
                    foreach (var descriptor in descriptors)
                    {
                        if (descriptor != null)
                        {
                            var text = descriptor.ToString();
                            if (text != null && !string.IsNullOrEmpty(text))
                            {
                                var cleanValue = RemoveSurroundingQuotes(text);
                                values.Add(cleanValue);
                                _logger.LogDebug("ASF tag '{TagName}' contains value: '{Value}'", tagName, cleanValue);
                            }
                        }
                    }
                }
                
                if (values.Count > 0)
                {
                    var result = string.Join(",", values.Distinct());
                    _logger.LogDebug("Combined ASF tag result for '{TagName}': '{Result}'", tagName, result);
                    return result;
                }
                else
                {
                    _logger.LogDebug("No ASF tag found for '{TagName}'", tagName);
                }
            }
            else
            {
                _logger.LogDebug("No ASF tags found in file");
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting ASF tag {TagName}", tagName);
            return null;
        }
    }

    /// <summary>
    /// Extracts a TXXX (User Text Information) frame from ID3v2 tags by description.
    /// This is used for custom tags in MP3/WAV files that don't have standard frame IDs.
    /// MP3Tag stores custom tags as TXXX frames with a description field matching the tag name.
    /// </summary>
    /// <param name="file">The TagLib file.</param>
    /// <param name="description">The description of the TXXX frame to extract (case-insensitive).</param>
    /// <returns>The frame text value(s) if found, multiple values combined with commas, otherwise null.</returns>
    private string? ExtractId3v2UserTextFrame(TagLib.File file, string description)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
            {
                var values = new List<string>();
                
                // Get all TXXX frames
                var frames = id3v2Tag.GetFrames("TXXX");
                
                foreach (var frame in frames)
                {
                    if (frame is TagLib.Id3v2.UserTextInformationFrame userTextFrame)
                    {
                        var frameDescription = userTextFrame.Description ?? string.Empty;
                        
                        // Some tagging applications include quotes in the description field
                        // Remove surrounding quotes only if both ends have matching quotes
                        var strippedDescription = RemoveSurroundingQuotes(frameDescription);
                        
                        // Check if the description matches (case-insensitive)
                        if (string.Equals(frameDescription, description, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(strippedDescription, description, StringComparison.OrdinalIgnoreCase))
                        {
                            // UserTextInformationFrame can contain multiple text values
                            foreach (var text in userTextFrame.Text)
                            {
                                if (!string.IsNullOrEmpty(text))
                                {
                                    // Remove surrounding quotes only if both ends have matching quotes
                                    var cleanText = RemoveSurroundingQuotes(text);
                                    values.Add(cleanText);
                                }
                            }
                        }
                    }
                }
                
                if (values.Count > 0)
                {
                    return string.Join(",", values.Distinct());
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting TXXX frame with description {Description}", description);
            return null;
        }
    }



    /// <summary>
    /// Removes specified tags from all audio items in the library.
    /// </summary>
    /// <param name="tagsToRemove">Comma-separated list of tag names to remove.</param>
    /// <param name="removeFromParents">Whether to also remove tags from parent albums and artists.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RemoveTagsFromAllAudioItemsAsync(string tagsToRemove, bool removeFromParents, CancellationToken cancellationToken)
    {
        SemaphoreSlim? semaphore = null;
        try
        {
            _logger.LogInformation("Starting bulk tag removal for tags: {TagsToRemove}. This can take a while if you have a large music library.", tagsToRemove);

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
            var batchSize = Math.Min(Environment.ProcessorCount * 2, 10); // Limit concurrent operations
            semaphore = new SemaphoreSlim(batchSize, batchSize);
            var itemsToUpdate = new System.Collections.Concurrent.ConcurrentBag<Audio>();

            _logger.LogInformation("Processing tag removal with {BatchSize} concurrent operations", batchSize);

            var processingTasks = audioItems.Select(async audioItem =>
            {
                // Check cancellation before acquiring semaphore to avoid unnecessary waiting
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var semaphoreAcquired = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    semaphoreAcquired = true;
                    
                    // Double-check cancellation after acquiring semaphore
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var wasModified = RemoveTagsInternal(audioItem, tagsToRemove);
                    if (wasModified)
                    {
                        itemsToUpdate.Add(audioItem);
                    }
                    
                    // Thread-safe progress reporting using Interlocked
                    var currentCount = Interlocked.Increment(ref processedCount);
                    if (currentCount % 100 == 0)
                    {
                        _logger.LogInformation("Processed {Count}/{Total} audio items for tag removal ({Percentage:F1}%)", 
                            currentCount, audioItems.Count, 
                            currentCount * 100.0 / audioItems.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing tags from audio item {Name}", audioItem.Name);
                }
                finally
                {
                    if (semaphoreAcquired)
                    {
                        semaphore.Release();
                    }
                }
            });

            await Task.WhenAll(processingTasks).ConfigureAwait(false);

            // Batch update all modified items
            if (itemsToUpdate.Count > 0)
            {
                _logger.LogInformation("Performing batch database update for {Count} modified items", itemsToUpdate.Count);
                
                var updateTasks = itemsToUpdate.Select(async item =>
                {
                    try
                    {
                        await _libraryManager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating item {Name}", item.Name);
                    }
                });

                await Task.WhenAll(updateTasks).ConfigureAwait(false);
                _logger.LogInformation("Completed batch database update for tag removal");
            }

            _logger.LogInformation("Completed bulk tag removal. Processed {Count} audio items, updated {UpdatedCount} items", 
                processedCount, itemsToUpdate.Count);

            // Remove from parent albums and artists if requested
            if (removeFromParents)
            {
                await RemoveTagsFromAlbumsAsync(tagsToRemove, cancellationToken).ConfigureAwait(false);
                await RemoveTagsFromArtistsAsync(tagsToRemove, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during bulk tag removal");
            throw;
        }
        finally
        {
            // Ensure proper disposal of SemaphoreSlim to prevent memory leaks
            semaphore?.Dispose();
        }
    }

    /// <summary>
    /// Processes all audio items in the library.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ProcessAllAudioItemsAsync(CancellationToken cancellationToken)
    {
        SemaphoreSlim? semaphore = null;
        try
        {
            _logger.LogInformation("Starting bulk ID3 tag processing for all audio items. This can take a while if you have a large music library.");

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
            var batchSize = Math.Min(Environment.ProcessorCount * 2, 10); // Limit concurrent operations
            semaphore = new SemaphoreSlim(batchSize, batchSize);
            var itemsToUpdate = new System.Collections.Concurrent.ConcurrentBag<Audio>();

            _logger.LogInformation("Processing with {BatchSize} concurrent operations", batchSize);

            var processingTasks = audioItems.Select(async audioItem =>
            {
                // Check cancellation before acquiring semaphore to avoid unnecessary waiting
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var semaphoreAcquired = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    semaphoreAcquired = true;
                    
                    // Double-check cancellation after acquiring semaphore
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var wasUpdated = ProcessAudioItemInternal(audioItem);
                    if (wasUpdated)
                    {
                        itemsToUpdate.Add(audioItem);
                    }
                    
                    // Thread-safe progress reporting using Interlocked
                    var currentCount = Interlocked.Increment(ref processedCount);
                    if (currentCount % 100 == 0)
                    {
                        _logger.LogInformation("Processed {Count}/{Total} audio items ({Percentage:F1}%)", 
                            currentCount, audioItems.Count, 
                            currentCount * 100.0 / audioItems.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing audio item {Name}", audioItem.Name);
                }
                finally
                {
                    if (semaphoreAcquired)
                    {
                        semaphore.Release();
                    }
                }
            });

            await Task.WhenAll(processingTasks).ConfigureAwait(false);

            // Batch update all modified items
            if (itemsToUpdate.Count > 0)
            {
                _logger.LogInformation("Performing batch database update for {Count} modified items", itemsToUpdate.Count);
                
                var updateTasks = itemsToUpdate.Select(async item =>
                {
                    try
                    {
                        await _libraryManager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating item {Name}", item.Name);
                    }
                });

                await Task.WhenAll(updateTasks).ConfigureAwait(false);
                _logger.LogInformation("Completed batch database update");
            }

            _logger.LogInformation("Completed bulk ID3 tag processing. Processed {Count} audio items, updated {UpdatedCount} items", 
                processedCount, itemsToUpdate.Count);

            // Propagate tags to parent albums and artists if configured
            if (_configuration.PropagateTagsToParents)
            {
                await PropagateTagsToParentsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during bulk ID3 tag processing");
            throw;
        }
        finally
        {
            // Ensure proper disposal of SemaphoreSlim to prevent memory leaks
            semaphore?.Dispose();
        }
    }

    /// <summary>
    /// Internal method to process audio item without database updates.
    /// </summary>
    /// <param name="audioItem">The audio item to process.</param>
    /// <returns>True if the item was modified and needs database update, false otherwise.</returns>
    private bool ProcessAudioItemInternal(Audio audioItem)
    {
        TagLib.File? file = null;
        try
        {
            if (audioItem == null || string.IsNullOrEmpty(audioItem.Path))
            {
                _logger.LogWarning("Audio item or path is null, skipping tag extraction");
                return false;
            }

            if (!File.Exists(audioItem.Path))
            {
                _logger.LogWarning("Audio file does not exist: {Path}", audioItem.Path);
                return false;
            }

            _logger.LogDebug("Processing ID3 tags for: {Name} ({Path})", audioItem.Name, audioItem.Path);

            file = TagLib.File.Create(audioItem.Path);
            if (file == null)
            {
                _logger.LogWarning("Could not create TagLib file for: {Path}", audioItem.Path);
                return false;
            }

            var extractedTags = new List<string>();

            // Extract all configured tags
            if (!string.IsNullOrWhiteSpace(_configuration.TagNames))
            {
                var tags = ExtractConfiguredTags(file);
                extractedTags.AddRange(tags);
            }

            // Add extracted tags to the audio item (without database update)
            if (extractedTags.Count > 0)
            {
                var wasModified = AddTagsToAudioItemInternal(audioItem, extractedTags);
                
                if (wasModified)
                {
                    _logger.LogDebug("Extracted {Count} tags from {Name}: {Tags}",
                        extractedTags.Count, audioItem.Name, string.Join(", ", extractedTags));
                }
                
                return wasModified;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ID3 tags for {Name}", audioItem?.Name ?? "unknown");
            return false;
        }
        finally
        {
            // Ensure proper disposal of TagLib.File resources
            file?.Dispose();
        }
    }

    /// <summary>
    /// Adds tags to the audio item without database update.
    /// </summary>
    /// <param name="audioItem">The audio item.</param>
    /// <param name="tags">The tags to add.</param>
    /// <returns>True if the item was modified, false otherwise.</returns>
    private bool AddTagsToAudioItemInternal(Audio audioItem, List<string> tags)
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
                
                _logger.LogDebug("Added {Count} new tags to {Name}", newTags.Count, audioItem.Name);
                return true; // Item was modified
            }
            
            return false; // No modifications made
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding tags to audio item {Name}", audioItem.Name);
            return false;
        }
    }

    /// <summary>
    /// Removes specified tags from an item without database update.
    /// </summary>
    /// <param name="item">The item to process (Audio, MusicAlbum, or MusicArtist).</param>
    /// <param name="tagsToRemove">Comma-separated list of tag names to remove.</param>
    /// <returns>True if the item was modified, false otherwise.</returns>
    private bool RemoveTagsInternal(BaseItem item, string tagsToRemove)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tagsToRemove))
            {
                return false; // No tags to remove
            }

            var existingTags = item.Tags?.ToList() ?? [];
            
            // Use HashSet for O(1) lookups instead of O(n) with Any()
            var tagNamesToRemove = new HashSet<string>(
                tagsToRemove
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => name.Trim())
                    .Where(name => !string.IsNullOrEmpty(name)),
                StringComparer.OrdinalIgnoreCase);

            if (tagNamesToRemove.Count == 0)
            {
                return false; // No valid tag names to remove
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
                
                // Check if this tag should be removed (O(1) HashSet lookup)
                var shouldRemove = tagNamesToRemove.Contains(existingTagName);

                if (shouldRemove)
                {
                    removedCount++;
                    _logger.LogDebug("Removing tag '{Tag}' from {Name}", existingTag, item.Name);
                }
                else
                {
                    tagsToKeep.Add(existingTag);
                }
            }

            if (removedCount > 0)
            {
                item.Tags = [..tagsToKeep];
                _logger.LogDebug("Removed {Count} tags from {Name}", removedCount, item.Name);
                return true; // Item was modified
            }
            
            return false; // No modifications made
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing tags from item {Name}", item.Name);
            return false;
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

                
                var values = new List<string>();
                
                // Get all values for this tag name (Vorbis comments can have multiple values)
                var commentValues = vorbisTag.GetField(tagName);
                if (commentValues != null && commentValues.Length > 0)
                {
                    foreach (var value in commentValues)
                    {
                        if (!string.IsNullOrEmpty(value))
                        {
                            // Remove surrounding quotes only if both ends have matching quotes
                            var cleanValue = RemoveSurroundingQuotes(value);
                            values.Add(cleanValue);
                            _logger.LogDebug("Vorbis comment '{TagName}' contains value: '{Value}'", tagName, cleanValue);
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

    /// <summary>
    /// Propagates tags from songs to their parent albums and artists.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task PropagateTagsToParentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting tag propagation to parent albums and artists");

            // First, propagate tags to albums
            await PropagateTagsToAlbumsAsync(cancellationToken).ConfigureAwait(false);

            // Then, propagate tags to artists
            await PropagateTagsToArtistsAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Completed tag propagation to parent albums and artists");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during tag propagation to parents");
            throw;
        }
    }

    /// <summary>
    /// Propagates tags from songs to their parent albums.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task PropagateTagsToAlbumsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Propagating tags to albums");

        // Get all music albums
        var albumQuery = new InternalItemsQuery(null)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true
        };

        var albums = _libraryManager.GetItemsResult(albumQuery).Items.ToList();
        _logger.LogInformation("Found {Count} albums to update", albums.Count);

        await ProcessItemsInParallelAsync(
            albums,
            "albums for tag propagation",
            (album, ct) =>
            {
                // Get all songs in this album
                var songsQuery = new InternalItemsQuery(null)
                {
                    IncludeItemTypes = [BaseItemKind.Audio],
                    Parent = album,
                    Recursive = false
                };

                var songs = _libraryManager.GetItemsResult(songsQuery).Items.OfType<Audio>().ToList();

                if (songs.Count > 0)
                {
                    // Aggregate all unique tags from songs
                    var aggregatedTags = songs
                        .SelectMany(song => song.Tags ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(tag => tag)
                        .ToArray();

                    // Only update if there are tags to add (using HashSet for O(1) lookups)
                    if (aggregatedTags.Length > 0)
                    {
                        var existingSet = new HashSet<string>(album.Tags ?? [], StringComparer.OrdinalIgnoreCase);
                        var initialCount = existingSet.Count;

                        // Add all aggregated tags (HashSet handles duplicates automatically)
                        foreach (var tag in aggregatedTags)
                        {
                            existingSet.Add(tag);
                        }

                        if (existingSet.Count > initialCount) // New tags were added
                        {
                            album.Tags = [.. existingSet];
                            
                            _logger.LogDebug("Added {Count} tags to album '{Album}'",
                                existingSet.Count - initialCount, album.Name);
                            
                            return Task.FromResult<BaseItem?>(album); // Return modified album
                        }
                    }
                }

                return Task.FromResult<BaseItem?>(null); // No changes
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Propagates tags from songs to their parent artists.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task PropagateTagsToArtistsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Propagating tags to artists");

        // Get all music artists
        var artistQuery = new InternalItemsQuery(null)
        {
            IncludeItemTypes = [BaseItemKind.MusicArtist],
            Recursive = true
        };

        var artists = _libraryManager.GetItemsResult(artistQuery).Items.ToList();
        _logger.LogInformation("Found {Count} artists to update", artists.Count);

        await ProcessItemsInParallelAsync(
            artists,
            "artists for tag propagation",
            (artist, ct) =>
            {
                // Get all songs by this artist
                var songsQuery = new InternalItemsQuery(null)
                {
                    IncludeItemTypes = [BaseItemKind.Audio],
                    ArtistIds = [artist.Id],
                    Recursive = true
                };

                var songs = _libraryManager.GetItemsResult(songsQuery).Items.OfType<Audio>().ToList();

                if (songs.Count > 0)
                {
                    // Aggregate all unique tags from songs
                    var aggregatedTags = songs
                        .SelectMany(song => song.Tags ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(tag => tag)
                        .ToArray();

                    // Only update if there are tags to add (using HashSet for O(1) lookups)
                    if (aggregatedTags.Length > 0)
                    {
                        var existingSet = new HashSet<string>(artist.Tags ?? [], StringComparer.OrdinalIgnoreCase);
                        var initialCount = existingSet.Count;

                        // Add all aggregated tags (HashSet handles duplicates automatically)
                        foreach (var tag in aggregatedTags)
                        {
                            existingSet.Add(tag);
                        }

                        if (existingSet.Count > initialCount) // New tags were added
                        {
                            artist.Tags = [.. existingSet];
                            
                            _logger.LogDebug("Added {Count} tags to artist '{Artist}'",
                                existingSet.Count - initialCount, artist.Name);
                            
                            return Task.FromResult<BaseItem?>(artist); // Return modified artist
                        }
                    }
                }

                return Task.FromResult<BaseItem?>(null); // No changes
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes specified tags from all albums in the library.
    /// </summary>
    /// <param name="tagsToRemove">Comma-separated list of tag names to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task RemoveTagsFromAlbumsAsync(string tagsToRemove, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Removing tags from albums: {TagsToRemove}", tagsToRemove);

        // Get all music albums
        var albumQuery = new InternalItemsQuery(null)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true
        };

        var albums = _libraryManager.GetItemsResult(albumQuery).Items.ToList();
        _logger.LogInformation("Found {Count} albums to process for tag removal", albums.Count);

        await ProcessItemsInParallelAsync(
            albums,
            "albums for tag removal",
            (album, ct) =>
            {
                var wasModified = RemoveTagsInternal(album, tagsToRemove);
                return Task.FromResult(wasModified ? album : null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes specified tags from all artists in the library.
    /// </summary>
    /// <param name="tagsToRemove">Comma-separated list of tag names to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task RemoveTagsFromArtistsAsync(string tagsToRemove, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Removing tags from artists: {TagsToRemove}", tagsToRemove);

        // Get all music artists
        var artistQuery = new InternalItemsQuery(null)
        {
            IncludeItemTypes = [BaseItemKind.MusicArtist],
            Recursive = true
        };

        var artists = _libraryManager.GetItemsResult(artistQuery).Items.ToList();
        _logger.LogInformation("Found {Count} artists to process for tag removal", artists.Count);

        await ProcessItemsInParallelAsync(
            artists,
            "artists for tag removal",
            (artist, ct) =>
            {
                var wasModified = RemoveTagsInternal(artist, tagsToRemove);
                return Task.FromResult(wasModified ? artist : null);
            },
            cancellationToken).ConfigureAwait(false);
    }
} 