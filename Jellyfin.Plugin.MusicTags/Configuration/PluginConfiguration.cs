using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MusicTags.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // Set default options
        TagNames = string.Empty;
        OverwriteExistingTags = false;
        TagDelimiters = string.Empty;
    }

    /// <summary>
    /// Gets or sets the comma-separated list of tag names to extract from ID3 tags.
    /// These will be added to Jellyfin as tags in the format "TagName:Value".
    /// </summary>
    public string TagNames { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to overwrite existing tags.
    /// </summary>
    public bool OverwriteExistingTags { get; set; }

    /// <summary>
    /// Gets or sets the custom delimiters for splitting tag values into multiple tags.
    /// Multiple delimiters can be specified in sequence (e.g., ";|\").
    /// When a tag value contains any of these delimiters, it will be split into separate tags.
    /// </summary>
    public string TagDelimiters { get; set; } = string.Empty;
}
