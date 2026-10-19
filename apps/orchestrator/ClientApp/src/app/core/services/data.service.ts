import { Injectable, inject, signal, computed } from '@angular/core';
import {
  GroupsService,
  RoomsService,
  RadioStreamsService,
  FullStateService,
  SpotifySourcesService,
  RadioPluginsService,
  FullStateDto,
  RadioPluginDto,
  RadioPluginCreateRequest,
  RadioPluginUpdateRequest,
  GroupDto,
  RoomDto,
  StreamDto,
  SpotifySourceDto,
  SpotifyReleaseDto,
  NodeReleaseDto,
} from '@api';
import {
  asyncScheduler,
  distinctUntilChanged,
  firstValueFrom,
  groupBy,
  mergeMap,
  Subject,
  throttleTime,
} from 'rxjs';
import { SignalRService } from './signalr.service';
import { ToastService } from './toast.service';

@Injectable({ providedIn: 'root' })
export class DataService {
  // Global State Signals - Standardized on API DTO
  public stateSignal = signal<FullStateDto | null>(null);
  public loading = signal<boolean>(true);

  // Derived signals from FullState
  public groups = computed<GroupDto[]>(() => this.stateSignal()?.groups || []);
  public streams = computed<StreamDto[]>(
    () => this.stateSignal()?.radioStreams || [],
  );
  public spotifySources = computed<SpotifySourceDto[]>(
    () => this.stateSignal()?.spotifySources || [],
  );
  public unassignedRooms = computed<RoomDto[]>(
    () => this.stateSignal()?.unassignedRooms || [],
  );
  public radioPlugins = computed<RadioPluginDto[]>(
    () => this.stateSignal()?.radioPlugins || [],
  );

  public allListIds = computed(() => {
    const state = this.stateSignal();
    if (!state) return ['unassigned-list'];

    const ids = state.groups.map((g) => `list-${g.id}`);
    ids.push('unassigned-list');
    return ids;
  });

  private signalR = inject(SignalRService);
  private toast = inject(ToastService);
  private groupsApi = inject(GroupsService);
  private roomsApi = inject(RoomsService);
  private streamsApi = inject(RadioStreamsService);
  private fullStateApi = inject(FullStateService);
  private spotifyApi = inject(SpotifySourcesService);
  private radioPluginsApi = inject(RadioPluginsService);

  private volumeUpdate$ = new Subject<{
    id: string;
    percent: number;
    muted: boolean;
  }>();
  private groupVolumeUpdate$ = new Subject<{
    id: string;
    percent: number;
    muted: boolean;
  }>();
  private mixUpdate$ = new Subject<{ id: string; percent: number }>();

  constructor() {
    this.refresh();

    // Subscribe to full state updates from SignalR
    this.signalR.fullState$.subscribe((state) => {
      if (state) {
        this.stateSignal.set(state);
        this.loading.set(false);
      }
    });

    // Automatically re-synchronize state when SignalR reconnects
    this.signalR.reconnected$.subscribe(() => {
      this.refresh();
    });

    // Handle targeted volume updates
    this.signalR.roomVolume$.subscribe((update) => {
      this.patchRoomVolume(update.roomId, update.percent, update.muted);
    });

    this.signalR.groupVolume$.subscribe((update) => {
      this.patchGroupVolume(
        update.groupId,
        update.masterVolume,
        update.muted,
      );
    });

    // Throttled Room Volume updates
    this.volumeUpdate$
      .pipe(
        groupBy((update) => update.id),
        mergeMap((group$) =>
          group$.pipe(
            throttleTime(100, asyncScheduler, {
              leading: true,
              trailing: true,
            }),
            distinctUntilChanged(
              (prev, curr) =>
                prev.percent === curr.percent && prev.muted === curr.muted,
            ),
          ),
        ),
      )
      .subscribe((update) => {
        this.roomsApi
          .apiRoomsIdVolumePut(update.id, {
            percent: update.percent,
            muted: update.muted,
          })
          .subscribe({
            error: (err) => {
              console.error('Failed to set room volume', err);
              this.toast.error('Failed to set room volume');
              this.refresh();
            },
          });
      });

    // Throttled Group Volume updates
    this.groupVolumeUpdate$
      .pipe(
        groupBy((update) => update.id),
        mergeMap((group$) =>
          group$.pipe(
            throttleTime(100, asyncScheduler, {
              leading: true,
              trailing: true,
            }),
            distinctUntilChanged(
              (prev, curr) =>
                prev.percent === curr.percent && prev.muted === curr.muted,
            ),
          ),
        ),
      )
      .subscribe((update) => {
        this.groupsApi
          .apiGroupsIdVolumePut(update.id, {
            percent: update.percent,
            muted: update.muted,
          })
          .subscribe({
            error: (err) => {
              console.error('Failed to set group volume', err);
              this.toast.error('Failed to set group volume');
              this.refresh();
            },
          });
      });

    // Throttled Mix updates
    this.mixUpdate$
      .pipe(
        groupBy((update) => update.id),
        mergeMap((group$) =>
          group$.pipe(
            throttleTime(100, asyncScheduler, {
              leading: true,
              trailing: true,
            }),
            distinctUntilChanged((prev, curr) => prev.percent === curr.percent),
          ),
        ),
      )
      .subscribe((update) => {
        this.roomsApi
          .apiRoomsIdMixPut(update.id, { percent: update.percent })
          .subscribe({
            error: (err) => {
              console.error('Failed to set room mix', err);
              this.toast.error('Failed to set room mix');
              this.refresh();
            },
          });
      });
  }

  private patchRoomVolume(roomId: string, percent: number, muted: boolean) {
    const state = this.stateSignal();
    if (!state) return;

    // Immutable update of the entire state tree
    const newGroups = state.groups.map((group) => {
      const roomIndex = group.rooms.findIndex((r) => r.id === roomId);
      if (roomIndex === -1) return group;

      const newRooms = [...group.rooms];
      newRooms[roomIndex] = {
        ...newRooms[roomIndex],
        volumePercent: percent,
        volumeMuted: muted,
      };
      return { ...group, rooms: newRooms };
    });

    const newUnassigned = state.unassignedRooms.map((room) => {
      if (room.id !== roomId) return room;
      return { ...room, volumePercent: percent, volumeMuted: muted };
    });

    this.stateSignal.set({
      ...state,
      groups: newGroups,
      unassignedRooms: newUnassigned,
    });
  }

  private patchGroupVolume(
    groupId: string,
    masterVolume: number,
    muted: boolean,
  ) {
    const state = this.stateSignal();
    if (!state) return;

    const newGroups = state.groups.map((group) => {
      if (group.id !== groupId) return group;
      return { ...group, masterVolume, isMuted: muted };
    });

    this.stateSignal.set({
      ...state,
      groups: newGroups,
    });
  }

  private patchRoomMix(roomId: string, percent: number) {
    const state = this.stateSignal();
    if (!state) return;

    const newGroups = state.groups.map((group) => {
      const roomIndex = group.rooms.findIndex((r) => r.id === roomId);
      if (roomIndex === -1) return group;

      const newRooms = [...group.rooms];
      newRooms[roomIndex] = { ...newRooms[roomIndex], mixVolume: percent };
      return { ...group, rooms: newRooms };
    });

    const newUnassigned = state.unassignedRooms.map((room) => {
      if (room.id !== roomId) return room;
      return { ...room, mixVolume: percent };
    });

    this.stateSignal.set({
      ...state,
      groups: newGroups,
      unassignedRooms: newUnassigned,
    });
  }

  async refresh() {
    this.loading.set(true);
    try {
      const state = await firstValueFrom(this.fullStateApi.apiFullStateGet());
      this.stateSignal.set(state);
      this.signalR.setState(state);
      this.loading.set(false);
    } catch (e) {
      console.error('Failed to refresh data', e);
      this.loading.set(false);
    }
  }

  async createGroup(name: string) {
    try {
      await firstValueFrom(this.groupsApi.apiGroupsPost({ name }));
      this.toast.success(
        'Group created',
        `Successfully created group "${name}"`,
      );
    } catch (e) {
      this.toast.error('Failed to create group');
    }
  }

  async deleteGroup(id: string) {
    try {
      await firstValueFrom(this.groupsApi.apiGroupsIdDelete(id));
      this.toast.success('Group deleted');
    } catch (e) {
      this.toast.error('Failed to delete group');
    }
  }

  async setGroupStream(id: string, streamId: string) {
    try {
      await firstValueFrom(
        this.groupsApi.apiGroupsIdStreamPut(id, { streamId }),
      );
      this.toast.success('Source changed');
    } catch (e) {
      this.toast.error('Failed to change source');
    }
  }

  async createRadio(name: string, url: string) {
    try {
      await firstValueFrom(this.streamsApi.apiStreamsRadioPost({ name, url }));
      this.toast.success('Radio station added');
    } catch (e) {
      this.toast.error('Failed to add radio station');
    }
  }

  async deleteRadio(id: string) {
    try {
      await firstValueFrom(this.streamsApi.apiStreamsRadioIdDelete(id));
      this.toast.success('Radio station removed');
    } catch (e) {
      this.toast.error('Failed to remove radio station');
    }
  }

  async updateRadio(id: string, name: string, url: string) {
    try {
      await firstValueFrom(
        this.streamsApi.apiStreamsRadioIdPut(id, { name, url }),
      );
      this.toast.success('Radio station updated');
    } catch (e) {
      this.toast.error('Failed to update radio station');
    }
  }

  async restartRadio(id: string) {
    try {
      await firstValueFrom(this.streamsApi.apiStreamsRadioIdRestartPost(id));
      this.toast.success('Restarting radio stream...');
    } catch (e) {
      this.toast.error('Failed to restart radio stream');
    }
  }

  async createSpotifySource(name: string, maxVolume: number) {
    try {
      await firstValueFrom(
        this.spotifyApi.apiSpotifySourcesPost({ name, maxVolume }),
      );
      this.toast.success('Spotify source created');
    } catch (e) {
      this.toast.error('Failed to create Spotify source');
    }
  }

  async deleteSpotifySource(id: string) {
    try {
      await firstValueFrom(this.spotifyApi.apiSpotifySourcesIdDelete(id));
      this.toast.success('Spotify source removed');
    } catch (e) {
      this.toast.error('Failed to remove Spotify source');
    }
  }

  async updateSpotifySource(id: string, name: string, maxVolume: number) {
    try {
      await firstValueFrom(
        this.spotifyApi.apiSpotifySourcesIdPut(id, { name, maxVolume }),
      );
      this.toast.success('Spotify source updated');
    } catch (e) {
      this.toast.error('Failed to update Spotify source');
    }
  }

  async restartSpotifySource(id: string) {
    try {
      await firstValueFrom(this.spotifyApi.apiSpotifySourcesIdRestartPost(id));
      this.toast.success('Restarting Spotify source...');
    } catch (e) {
      this.toast.error('Failed to restart Spotify source');
    }
  }

  async getSpotifySourceReleases(
    id: string,
    targetVersion?: string,
  ): Promise<SpotifyReleaseDto[]> {
    try {
      return await firstValueFrom(
        this.spotifyApi.apiSpotifySourcesIdReleasesGet(id, targetVersion),
      );
    } catch {
      this.toast.error('Failed to fetch release information');
      return [];
    }
  }

  async upgradeSpotifySource(
    id: string,
    targetVersion?: string,
  ): Promise<boolean> {
    try {
      await firstValueFrom(
        this.spotifyApi.apiSpotifySourcesIdUpgradePost(id, { targetVersion }),
      );
      this.toast.success('Spotify source upgrade started');
      return true;
    } catch {
      this.toast.error('Failed to upgrade Spotify source');
      return false;
    }
  }

  async pauseSpotify(id: string) {
    try {
      await firstValueFrom(this.spotifyApi.apiSpotifySourcesIdPausePost(id));
    } catch {}
  }

  async resumeSpotify(id: string) {
    try {
      await firstValueFrom(this.spotifyApi.apiSpotifySourcesIdResumePost(id));
    } catch {}
  }

  async nextSpotify(id: string) {
    try {
      await firstValueFrom(this.spotifyApi.apiSpotifySourcesIdNextPost(id));
    } catch {}
  }

  async previousSpotify(id: string) {
    try {
      await firstValueFrom(this.spotifyApi.apiSpotifySourcesIdPreviousPost(id));
    } catch {}
  }

  async setRoomVolume(id: string, percent: number, muted: boolean) {
    this.patchRoomVolume(id, percent, muted);
    this.volumeUpdate$.next({ id, percent, muted });
  }

  async setGroupVolume(id: string, percent: number, muted: boolean) {
    this.patchGroupVolume(id, percent, muted);
    this.groupVolumeUpdate$.next({ id, percent, muted });
  }

  async setRoomMix(id: string, percent: number) {
    this.patchRoomMix(id, percent);
    this.mixUpdate$.next({ id, percent });
  }

  async renameRoom(id: string, name: string) {
    try {
      await firstValueFrom(this.roomsApi.apiRoomsIdNamePut(id, { name }));
      this.toast.success('Room renamed');
    } catch (e) {
      this.toast.error('Failed to rename room');
    }
  }

  async renameGroup(id: string, name: string) {
    try {
      await firstValueFrom(this.groupsApi.apiGroupsIdNamePut(id, { name }));
      this.toast.success('Group renamed');
    } catch (e) {
      this.toast.error('Failed to rename group');
    }
  }

  async moveRoomToGroup(roomId: string, groupId: string) {
    try {
      await firstValueFrom(
        this.groupsApi.apiGroupsIdClientsPut(groupId, [roomId]),
      );
      this.toast.success('Room assigned');
    } catch (e) {
      this.toast.error('Failed to assign room');
    }
  }

  async unassignRoom(roomId: string) {
    try {
      await firstValueFrom(this.roomsApi.apiRoomsIdUnassignPut(roomId));
      this.toast.success('Room unassigned');
    } catch (e) {
      this.toast.error('Failed to unassign room');
    }
  }

  async deleteRoom(id: string) {
    try {
      await firstValueFrom(this.roomsApi.apiRoomsIdDelete(id));
      this.toast.success('Device removed');
    } catch (e) {
      this.toast.error('Failed to remove device');
    }
  }

  async playSpotifyContext(id: string, contextUri: string) {
    try {
      await firstValueFrom(
        this.spotifyApi.apiSpotifySourcesIdPlayContextPost(id, { contextUri })
      );
      this.toast.success('Playback started');
    } catch (e) {
      this.toast.error('Failed to start playback');
    }
  }

  async createRadioPlugin(req: RadioPluginCreateRequest) {
    try {
      await firstValueFrom(this.radioPluginsApi.apiRadioPluginsPost(req));
      this.toast.success('Radio plugin created');
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Failed to create radio plugin';
      this.toast.error(msg);
    }
  }

  async updateRadioPlugin(id: string, req: RadioPluginUpdateRequest) {
    try {
      await firstValueFrom(this.radioPluginsApi.apiRadioPluginsIdPut(id, req));
      this.toast.success('Radio plugin updated');
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Failed to update radio plugin';
      this.toast.error(msg);
    }
  }

  async deleteRadioPlugin(id: string) {
    try {
      await firstValueFrom(this.radioPluginsApi.apiRadioPluginsIdDelete(id));
      this.toast.success('Radio plugin deleted');
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Failed to delete radio plugin';
      this.toast.error(msg);
    }
  }

  async toggleRadioPlugin(plugin: RadioPluginDto) {
    const req: RadioPluginUpdateRequest = {
      name: plugin.name,
      dockerImage: plugin.dockerImage,
      urlPatterns: plugin.urlPatterns,
      enabled: !plugin.enabled,
    };
    await this.updateRadioPlugin(plugin.id, req);
  }

  async getRadioPluginReleases(
    id: string,
    targetVersion?: string,
  ): Promise<NodeReleaseDto[]> {
    try {
      return await firstValueFrom(
        this.radioPluginsApi.apiRadioPluginsIdReleasesGet(id, targetVersion),
      );
    } catch {
      this.toast.error('Failed to fetch release information');
      return [];
    }
  }

  async upgradeRadioPlugin(
    id: string,
    targetVersion?: string,
  ): Promise<boolean> {
    try {
      await firstValueFrom(
        this.radioPluginsApi.apiRadioPluginsIdUpgradePost(id, { targetVersion }),
      );
      this.toast.success('Radio plugin upgrade started');
      return true;
    } catch {
      this.toast.error('Failed to upgrade radio plugin');
      return false;
    }
  }
}
