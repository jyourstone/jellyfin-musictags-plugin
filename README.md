# Jellyfin MusicTags Plugin

<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/jyourstone/jellyfin-musictags-plugin/master/images/logo.jpg" height="350"/></br>
        <a href="https://github.com/jyourstone/jellyfin-musictags-plugin/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/jyourstone/jellyfin-musictags-plugin/total"/></img></a> <a href="https://github.com/jyourstone/jellyfin-musictags-plugin/issues"><img alt="GitHub Issues or Pull Requests" src="https://img.shields.io/github/issues/jyourstone/jellyfin-musictags-plugin"/></img></a> <a href="https://github.com/jyourstone/jellyfin-musictags-plugin/releases"><img alt="Build and Release" src="https://github.com/jyourstone/jellyfin-musictags-plugin/actions/workflows/release.yml/badge.svg"/></img></a> <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11-rc5-blue.svg"/></img></a>
    </p>
</div>

A plugin for Jellyfin that extracts metadata tags from audio files (ID3v2, Vorbis comments, etc.) and adds them as Jellyfin tags for better organization and discovery.

This plugin automatically processes your music library to extract embedded tags and makes them available for filtering, and organizing your music collection.

Requires Jellyfin version `10.11.0-rc5`.

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
   - Comma-separated list of tag names (e.g., "BPM,KEY")
   - Supports standard ID3 tags, custom frames, and Vorbis comments
   - Tags are added to Jellyfin in the format "TagName:Value" (e.g., "BPM:141")

2. **Tag Names to Remove**: Remove unwanted tags from your Jellyfin library
   - Comma-separated list of tag names to remove (e.g., "BPM,KEY")
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

### Example 1: Extract BPM and KEY Tags
Configure the plugin to extract BPM and KEY information:
- **Tag Names to Extract**: `BPM,KEY`
- This will add tags like "BPM:141" and "KEY:D" to your music

### Example 2: Clean Up Old Tags
Remove unwanted tags from your library:
- **Tag Names to Remove**: `BPM`
- This will remove all instances of these tags from your Jellyfin music library

## 🎯 Perfect Companion: SmartPlaylist Plugin

MusicTags works exceptionally well with the **[SmartPlaylist Plugin](https://github.com/jyourstone/jellyfin-smartplaylist-plugin)**! Once you've extracted audio tags with MusicTags, you can use SmartPlaylist to create dynamic playlists based on those custom tags.

### SmartPlaylist Integration Examples

- **High-Energy Workout Mix**: Create a playlist with `BPM` tags between 130-150 (filter by tags and use this regex: `\bBPM:(13[0-9]|14[0-9]|150)\b`)
- **Chill Vibes**: Filter by `AB:MOOD` tags like "chill", "relaxing", or "ambient"
- **Key-Based Playlists**: Group songs by musical key using extracted `KEY` tags
- **Complex Combinations**: Combine multiple audio tags (e.g., "BPM:140-160" AND "AB:MOOD:energetic")

This powerful combination allows you to leverage the rich metadata embedded in your audio files to create sophisticated, automatically-updating playlists that reflect the actual musical characteristics of your music!

## Automatic Processing

MusicTags automatically processes your audio files when the "Process Music Tags" scheduled task runs.
You can also manually trigger processing from the plugin configuration page.

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
- Extract: `KEY` (ID3v2 key frame)
- Result: Adds "KEY:C" tags for musical key information

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
- Large libraries may take some time to process
- Processing can be scheduled during off-peak hours

## Disclaimer

The vast majority of this plugin, was developed by an AI assistant. While I do have some basic experience with C# from a long time ago, I'm essentially the project manager, guiding the AI, fixing its occasional goofs, and trying to keep it from becoming self-aware. Use at your own risk!
