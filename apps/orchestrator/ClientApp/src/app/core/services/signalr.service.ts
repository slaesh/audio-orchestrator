import { Injectable, inject, DestroyRef } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
} from '@microsoft/signalr';
import { BehaviorSubject, Subject } from 'rxjs';
import { 
  FullStateDto, 
  GroupDto, 
  RoomDto, 
  StreamDto, 
  SpotifySourceDto,
  SpotifyEventDto 
} from '@api';
import { getReceiverRegister } from '../signalr/TypedSignalR.Client';
import { IAudioClient } from '../signalr/TypedSignalR.Client/AudioOrchestrator.Contracts';

const SUPPORTED_SPOTIFY_EVENTS = new Set([
  'heartbeat',
  'playing',
  'seeked',
  'position_correction',
  'position_changed',
  'connected',
  'session_connected',
  'disconnected',
  'session_disconnected',
  'volume_set',
  'volume_changed',
  'changed',
]);

@Injectable({
  providedIn: 'root',
})
export class SignalRService implements IAudioClient {
  private connection: HubConnection;
  private destroyRef = inject(DestroyRef);
  private retryTimer: ReturnType<typeof setTimeout> | null = null;

  public fullState$ = new BehaviorSubject<FullStateDto | null>(null);
  public reconnected$ = new Subject<void>();
  public roomVolume$ = new Subject<{
    roomId: string;
    percent: number;
    muted: boolean;
  }>();
  public groupVolume$ = new Subject<{
    groupId: string;
    masterVolume: number;
    muted: boolean;
  }>();

  constructor() {
    this.connection = new HubConnectionBuilder()
      .withUrl('/hubs/audio')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build();

    this.connection.onreconnected(() => {
      console.log('SignalR Reconnected: notifying listeners for resync');
      this.reconnected$.next();
    });

    // Use the generated register logic to bind this service as the receiver.
    getReceiverRegister('IAudioClient').register(this.connection, this);

    this.start();

    this.destroyRef.onDestroy(() => {
      if (this.retryTimer) clearTimeout(this.retryTimer);
      this.connection.stop();
    });
  }

  // Implementation of IAudioClient (generated with camelCase signatures)

  public async onFullStateUpdate(state: FullStateDto): Promise<void> {
    this.fullState$.next(state);
  }

  public setState(state: FullStateDto): void {
    this.fullState$.next(state);
  }

  public async onRoomVolumeChanged(
    roomId: string,
    percent: number,
    muted: boolean,
  ): Promise<void> {
    this.roomVolume$.next({ roomId, percent, muted });
  }

  public async onGroupVolumeChanged(
    groupId: string,
    masterVolume: number,
    muted: boolean,
  ): Promise<void> {
    this.groupVolume$.next({ groupId, masterVolume, muted });
  }

  public async onSpotifyEvent(spotifyEvent: SpotifyEventDto): Promise<void> {
    const currentState = this.fullState$.value;
    if (!currentState) return;

    const sourceIndex = currentState.spotifySources.findIndex(
      (s) => s.id === spotifyEvent.groupId,
    );
    if (sourceIndex === -1) return;

    const source = { ...currentState.spotifySources[sourceIndex] };
    const newSources = [...currentState.spotifySources];

    if (spotifyEvent.event === 'metadata_update') {
      if (
        spotifyEvent.trackId &&
        (!source.currentTrackId || source.currentTrackId === spotifyEvent.trackId)
      ) {
        source.currentTrackId = spotifyEvent.trackId;
        source.currentTitle = spotifyEvent.title;
        source.currentArtist = spotifyEvent.artist;
        source.currentAlbum = spotifyEvent.album;
        source.currentDurationMs = spotifyEvent.durationMs;
        source.currentCoverUrl = spotifyEvent.coverUrl;

        newSources[sourceIndex] = source;
        this.fullState$.next({ ...currentState, spotifySources: newSources });
      }
      return;
    }

    if (!SUPPORTED_SPOTIFY_EVENTS.has(spotifyEvent.event ?? '')) return;

    if (spotifyEvent.trackId !== undefined) {
      if (spotifyEvent.trackId === '') {
        source.currentTrackId = '';
        source.currentTitle = undefined;
        source.currentArtist = undefined;
        source.currentAlbum = undefined;
        source.currentCoverUrl = undefined;
        source.currentDurationMs = 0;
      } else {
        source.currentTrackId = spotifyEvent.trackId;
      }
    }

    if (
      spotifyEvent.positionMs !== undefined &&
      spotifyEvent.positionMs !== null
    ) {
      source.currentPositionMs = spotifyEvent.positionMs;
    } else if (spotifyEvent.event === 'changed') {
      source.currentPositionMs = 0;
    }

    if (
      spotifyEvent.intervalMs !== undefined &&
      spotifyEvent.intervalMs !== null
    ) {
      source.heartbeatIntervalMs = spotifyEvent.intervalMs;
    }

    if (
      spotifyEvent.volume !== undefined &&
      spotifyEvent.volume !== null
    ) {
      source.masterVolume =
        spotifyEvent.volume > 100
          ? Math.round((spotifyEvent.volume / 65535) * 100)
          : spotifyEvent.volume;
    }
    
    source.lastUpdateTimestamp = Date.now();
    
    if (spotifyEvent.event === 'connected' || spotifyEvent.event === 'session_connected') {
      source.isConnected = true;
    } else if (spotifyEvent.event === 'disconnected' || spotifyEvent.event === 'session_disconnected') {
      source.isConnected = false;
      source.isActive = false;
    } else if (spotifyEvent.event === 'changed') {
      source.isActive = true;
    } else if (spotifyEvent.isConnected !== undefined && spotifyEvent.isConnected !== null) {
      // If the heartbeat provides the absolute connection state, trust it
      source.isConnected = spotifyEvent.isConnected;
      if (spotifyEvent.isPlaying !== undefined && spotifyEvent.isPlaying !== null) {
        source.isActive = spotifyEvent.isPlaying;
      }
    } else if (spotifyEvent.isPlaying !== undefined && spotifyEvent.isPlaying !== null) {
      source.isActive = spotifyEvent.isPlaying;
    }

    // Apply enriched metadata if present in the event
    if (spotifyEvent.title) source.currentTitle = spotifyEvent.title;
    if (spotifyEvent.artist) source.currentArtist = spotifyEvent.artist;
    if (spotifyEvent.album) source.currentAlbum = spotifyEvent.album;
    if (spotifyEvent.durationMs) source.currentDurationMs = spotifyEvent.durationMs;
    if (spotifyEvent.coverUrl) source.currentCoverUrl = spotifyEvent.coverUrl;
    if (spotifyEvent.contextUri !== undefined) source.contextUri = spotifyEvent.contextUri;
    if (spotifyEvent.contextName !== undefined) source.contextName = spotifyEvent.contextName;
    if (spotifyEvent.recentContexts) source.recentContexts = spotifyEvent.recentContexts;

    newSources[sourceIndex] = source;
    this.fullState$.next({ ...currentState, spotifySources: newSources });
  }

  public async onRadioMetadataChanged(
    streamId: string,
    title: string,
    artist: string,
    coverUrl?: string,
  ): Promise<void> {
    const currentState = this.fullState$.value;
    if (!currentState) return;

    let stateChanged = false;
    const newState = { ...currentState };

    // Update Radio Stream
    if (newState.radioStreams) {
      const radioIndex = newState.radioStreams.findIndex((r) => r.id === streamId);
      if (radioIndex !== -1) {
        newState.radioStreams = [...newState.radioStreams];
        newState.radioStreams[radioIndex] = {
          ...newState.radioStreams[radioIndex],
          currentTitle: title,
          currentArtist: artist,
          currentCoverUrl: coverUrl,
        };
        stateChanged = true;
      }
    }

    // Update Snapcast Stream if it matches
    if (newState.snapcastStreams) {
      const snapIndex = newState.snapcastStreams.findIndex(
        (s) => s.id === streamId,
      );
      if (snapIndex !== -1) {
        newState.snapcastStreams = [...newState.snapcastStreams];
        newState.snapcastStreams[snapIndex] = {
          ...newState.snapcastStreams[snapIndex],
          currentTitle: title,
          currentArtist: artist,
          currentCoverUrl: coverUrl,
        };
        stateChanged = true;
      }
    }

    if (stateChanged) {
      this.fullState$.next(newState);
    }
  }

  private async start() {
    try {
      await this.connection.start();
      console.log('SignalR Connected');
    } catch (err) {
      console.error('SignalR Connection Error: ', err);
      this.retryTimer = setTimeout(() => this.start(), 5000);
    }
  }
}
