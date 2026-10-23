# Project Ideas & Future Roadmap

Based on the current architecture and the recent stabilization of the Spotify node and metadata caching, here are high-impact features for future development:

## 1. In-App Spotify Controls
*Leveraging existing `spotify-node` capabilities.*

- **The Feature**: Add a sleek control bar (Play/Pause, Skip, Previous) to the `app-spotify-list` in the Angular UI.
- **Why it's cool**: Users don't have to switch back to the Spotify app for basic playback control.
- **Integration**: Uses the already defined `/pause`, `/resume`, `/next`, and `/previous` endpoints on the Rust nodes.

## 2. Smart Scheduling (Alarm Clock Mode)
- **The Feature**: A "Schedules" section to automate room/group assignments.
- **Example**: *"At 7:00 AM, move the 'Bedroom' room into the 'Morning' group and start playing the Sunshine Live radio stream."*
- **Technical Challenge**: Implementing a background worker in .NET (e.g., using `Cron` or `Quartz.NET`) to interact with `DockerManagerService` and `SnapcastService`.

## 3. Room-Level EQ and Delay Offsets
- **The Feature**: Advanced per-room settings for latency (ms) and volume trim.
- **Why it's cool**: Perfectly sync wireless speakers that have inherent hardware latency.
- **Implementation**: Exposes existing Snapcast API capabilities through a premium UI.

## 4. "Party Mode" (One-Click Master Sync)
- **The Feature**: A single "Party Mode" toggle that dissolves all existing groups and pulls **every** room into a single master group playing a specific source.
- **Why it's cool**: Instantly synchronizes the entire house for social gatherings without manual drag-and-drop.
