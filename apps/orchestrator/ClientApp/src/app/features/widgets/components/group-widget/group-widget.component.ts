import { Component, inject, computed, input, signal, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { DataService } from '../../../../core/services/data.service';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { 
  lucideChevronLeft, lucideVolume2, lucideVolumeX, 
  lucidePlay, lucidePause, lucideSkipForward, lucideSkipBack,
  lucideMusic, lucideRadio, lucideLayers, lucideCircleAlert
} from '@ng-icons/lucide';
import { ZardSliderComponent } from '../../../../core/ui/slider/slider.component';
import { ZardButtonComponent } from '../../../../core/ui/button/button.component';
import { SpotifyProgressBarComponent } from '../../../../shared/components/spotify-progress-bar/spotify-progress-bar.component';
import { GroupDto, StreamDto, SpotifySourceDto } from '@api';

@Component({
  selector: 'app-group-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, RouterLink, NgIcon, ZardSliderComponent, ZardButtonComponent, SpotifyProgressBarComponent],
  templateUrl: './group-widget.component.html',
  styleUrl: './group-widget.component.css',
  viewProviders: [provideIcons({ 
    lucideChevronLeft, lucideVolume2, lucideVolumeX, 
    lucidePlay, lucidePause, lucideSkipForward, lucideSkipBack,
    lucideMusic, lucideRadio, lucideLayers, lucideCircleAlert
  })]
})
export class GroupWidgetComponent {
  public data = inject(DataService);

  readonly id = input<string | null>(null);

  public group = computed(() => {
    const groupId = this.id();
    return this.data.groups().find((g) => g.id === groupId);
  });

  public currentStream = computed(() => {
    const g = this.group();
    if (!g || !g.streamId || g.streamId === 'default') return null;
    
    // Check Radio
    const radio = this.data.streams().find((s) => s.id === g.streamId);
    if (radio) return { type: 'radio' as const, data: radio };

    // Check Spotify
    const spotifyId = g.streamId?.replace('spotify_', '');
    if (!spotifyId) return null;
    
    const spotify = this.data.spotifySources().find((s) => s.id === spotifyId);
    if (spotify) return { type: 'spotify' as const, data: spotify };

    return null;
  });

  public isSourcePanelOpen = signal(false);

  setVolume(percent: number) {
    const g = this.group();
    if (!g) return;
    this.data.setGroupVolume(g.id, percent, g.isMuted);
  }

  toggleMute() {
    const g = this.group();
    if (!g) return;
    this.data.setGroupVolume(g.id, g.masterVolume, !g.isMuted);
  }

  toggleSourcePanel() {
    this.isSourcePanelOpen.update(v => !v);
  }

  selectSource(streamId: string) {
    const g = this.group();
    if (!g) return;
    this.data.setGroupStream(g.id, streamId);
    this.isSourcePanelOpen.set(false);
  }

  getSpotifyContextName(streamData: StreamDto | SpotifySourceDto): string | null | undefined {
    // Type guard based on the presence of maxVolume (unique to SpotifySourceDto) or just casting
    if ('contextName' in streamData) {
      return (streamData as SpotifySourceDto).contextName;
    }
    return null;
  }
}
