# Jellyfin MusicTags Plugin

<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/jyourstone/jellyfin-musictags-plugin/master/images/logo.jpg" height="350"/>
    </p>
</div>

A plugin for Jellyfin that extracts metadata tags from audio files (ID3v2, Vorbis comments, etc.) and adds them as Jellyfin tags for better organization and discovery.

This plugin automatically processes your music library to extract embedded tags and makes them available for filtering, searching, and organizing your music collection.

Tested and works with Jellyfin version `10.10.0` and newer.

## ✨ Features

- 🎵 **Multi-Format Support** - Extracts tags from ID3v2, Vorbis comments (FLAC/OGG), and other audio metadata formats
- 🏷️ **Flexible Tag Extraction** - Configure which specific tags to extract and add to Jellyfin
- 🧹 **Tag Cleanup** - Remove unwanted tags from your Jellyfin library
- ⚙️ **Configurable Options** - Choose whether to overwrite existing tags or preserve them
- 🔄 **Automatic Processing** - Scheduled task automatically processes new and updated audio files
- 🚀 **Manual Processing** - Trigger tag extraction immediately for all audio files
- 🎨 **Modern Web Interface** - Easy-to-use configuration page integrated into Jellyfin's plugin settings

## Configuration

MusicTags features a modern web-based configuration interface through the plugin settings page! Configure tag extraction settings without any manual file editing.

<div align="center">
    <p>
        <img alt="Configuration page" src="https://raw.githubusercontent.com/jyourstone/jellyfin-musictags-plugin/master/images/config_page.png" width="600"/>
    </p>
</div>

### Using the Web Interface

The configuration page allows you to:

1. **Tag Names to Extract**: Specify which tags to extract from your audio files
   - Comma-separated list of tag names (e.g., "BPM,AB:GENRE,AB:MOOD")
   - Supports standard ID3 tags, custom frames, and Vorbis comments
   - Tags are added to Jellyfin in the format "TagName:Value" (e.g., "BPM:141")

2. **Tag Names to Remove**: Remove unwanted tags from your Jellyfin library
   - Comma-separated list of tag names to remove (e.g., "BPM,AB:GENRE")
   - Removes ALL instances of these tag names from your library

3. **Processing Options**: Control how tags are processed
   - **Overwrite Existing Tags**: Replace existing Jellyfin tags with extracted audio file tags
   - **Manual Processing**: Trigger immediate processing of all audio files

### Supported Tag Formats

The plugin supports extraction from multiple audio metadata formats:

#### **Standard ID3 Tags**
- Artist, Album, Genre, Year, Composer, BPM, Publisher, Copyright, Comment
- Any standard ID3v1 or ID3v2 tag

#### **ID3v2 Custom Frames**
- Key, Mood, Language, ContentGroup
- Any 4-character frame ID (e.g., TXXX frames)

#### **Vorbis Comments (FLAC/OGG)**
- AB:GENRE, AB:MOOD, or any custom field name
- Standard Vorbis comment fields

#### **Dynamic Extraction**
- The plugin will attempt to extract any tag name you specify!
- Works with most audio formats that support metadata

## How to Install

### From Repository
Add this repository URL to your Jellyfin plugin catalog:
```
https://raw.githubusercontent.com/jyourstone/jellyfin-plugin-manifest/master/manifest.json
```

### Manual Installation
Download the latest release from the [Releases page](https://github.com/jyourstone/jellyfin-musictags-plugin/releases) and extract it to your Jellyfin plugins directory.

## Usage Examples

### Example 1: Extract BPM and Mood Tags
Configure the plugin to extract BPM and mood information:
- **Tag Names to Extract**: `BPM,AB:MOOD`
- This will add tags like "BPM:141" and "AB:MOOD:energetic" to your music

### Example 2: Extract Genre Information
Extract genre tags from various sources:
- **Tag Names to Extract**: `GENRE,AB:GENRE`
- This will extract both standard ID3 genre tags and custom Vorbis genre tags

### Example 3: Clean Up Old Tags
Remove unwanted tags from your library:
- **Tag Names to Remove**: `OLD_TAG,LEGACY_GENRE`
- This will remove all instances of these tags from your Jellyfin music library

## 🎯 Perfect Companion: SmartPlaylist Plugin

MusicTags works exceptionally well with the **[SmartPlaylist plugin](https://github.com/jyourstone/jellyfin-smartplaylist-plugin)**! Once you've extracted audio tags with MusicTags, you can use SmartPlaylist to create dynamic playlists based on those custom tags.

### SmartPlaylist Integration Examples

- **High-Energy Workout Mix**: Create a playlist with `BPM` tags between 140-160
- **Chill Vibes**: Filter by `AB:MOOD` tags like "chill", "relaxing", or "ambient"
- **Custom Genre Collections**: Build playlists based on your extracted `AB:GENRE` tags
- **Key-Based Playlists**: Group songs by musical key using extracted `KEY` tags
- **Complex Combinations**: Combine multiple audio tags (e.g., "BPM:140-160" AND "AB:MOOD:energetic")

This powerful combination allows you to leverage the rich metadata embedded in your audio files to create sophisticated, automatically-updating playlists that reflect the actual musical characteristics of your music!

## Automatic Processing

MusicTags automatically processes your audio files when:
- The "Process Music Tags" scheduled task runs
- New audio files are added to your library
- Existing audio files are updated

You can also manually trigger processing from the plugin configuration page.

## 🚀 Roadmap

Here are some of the planned features for future updates. Feel free to contribute or suggest new ideas!

- **Advanced Tag Mapping**: Map audio file tags to specific Jellyfin metadata fields
- **Tag Value Normalization**: Standardize tag values (e.g., normalize BPM ranges)
- **Batch Processing Options**: More granular control over processing behavior
- **Tag Statistics**: View statistics about extracted tags in your library
- **Export/Import**: Backup and restore tag extraction configurations

## Development

### Building Locally
For local development, see the [dev folder](https://github.com/jyourstone/jellyfin-musictags-plugin/tree/master/dev)

## Advanced Configuration

### Tag Extraction Examples

#### **BPM (Beats Per Minute)**
- Extract: `BPM`
- Result: Adds "BPM:141" tags to tracks with tempo information

#### **Custom Genre Tags**
- Extract: `AB:GENRE`
- Result: Adds "AB:GENRE:Rock" tags from Vorbis comments

#### **Mood Information**
- Extract: `AB:MOOD`
- Result: Adds "AB:MOOD:energetic" tags for mood classification

#### **Key Information**
- Extract: `TKEY` (ID3v2 key frame)
- Result: Adds "TKEY:C" tags for musical key information

#### **Multiple Sources**
- Extract: `GENRE,AB:GENRE,MOOD,AB:MOOD`
- Result: Extracts genre and mood from both ID3 and Vorbis sources

### File Format Support

The plugin supports tag extraction from:
- **MP3** - ID3v1 and ID3v2 tags
- **FLAC** - Vorbis comments and ID3v2 tags
- **OGG** - Vorbis comments
- **M4A** - iTunes-style metadata
- **WAV** - ID3v2 tags (when present)
- **Other formats** - Any format supported by the underlying audio library

### Performance Considerations

- Tag extraction is performed as a background task to avoid impacting Jellyfin performance
- Large libraries may take some time to process initially
- Subsequent runs only process new or modified files
- Processing can be scheduled during off-peak hours

## Credits

This project was created to enhance music organization in Jellyfin by leveraging the rich metadata often embedded in audio files.

## Disclaimer

This plugin extracts metadata directly from audio files and adds it to Jellyfin's tag system. The quality and accuracy of extracted tags depend on the metadata embedded in your audio files. Some audio files may have incomplete, inconsistent, or missing metadata.