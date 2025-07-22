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
}
