import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { DataService } from '../../../../core/services/data.service';
import { ConfirmService } from '../../../../core/ui/dialog/confirm.service';
import { ZardButtonComponent } from '../../../../core/ui/button/button.component';
import { ZardBadgeComponent } from '../../../../core/ui/badge/badge.component';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucidePlus, lucideTrash2, lucideMusic, lucideSpeaker, lucideSettings, lucideRefreshCw, lucideChevronDown, lucideChevronUp, lucideClock, lucidePlay, lucidePause, lucideSkipForward, lucideSkipBack, lucideCircleAlert, lucideListMusic, lucideDisc3, lucideCircleArrowUp } from '@ng-icons/lucide';
import { Dialog } from '@angular/cdk/dialog';
import { SpotifySourceDto } from '@api';
import { SpotifySourceFormDialogComponent } from '../../../../shared/dialogs/spotify-source-form-dialog.component';
import { SpotifyUpgradeDialogComponent } from '../../../../shared/dialogs/spotify-upgrade-dialog/spotify-upgrade-dialog.component';
import { SpotifyProgressBarComponent } from '../../../../shared/components/spotify-progress-bar/spotify-progress-bar.component';

@Component({
  selector: 'app-spotify-list',
  standalone: true,
  imports: [CommonModule, ZardButtonComponent, ZardBadgeComponent, NgIcon, SpotifyProgressBarComponent],
  templateUrl: './spotify-list.component.html',
  styleUrl: './spotify-list.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [provideIcons({ lucidePlus, lucideTrash2, lucideMusic, lucideSpeaker, lucideSettings, lucideRefreshCw, lucideChevronDown, lucideChevronUp, lucideClock, lucidePlay, lucidePause, lucideSkipForward, lucideSkipBack, lucideCircleAlert, lucideListMusic, lucideDisc3, lucideCircleArrowUp })]
})
export class SpotifyListComponent {
  public data = inject(DataService);
  private confirm = inject(ConfirmService);
  private dialog = inject(Dialog);

  playContext(source: SpotifySourceDto, contextUri: string) {
    if (source.isActive && source.contextUri === contextUri) return;
    this.data.playSpotifyContext(source.id, contextUri);
  }

  sources = this.data.spotifySources;
  collapsedSources = signal<Set<string>>(new Set());
  toggleExpand(id: string) {
    const set = new Set(this.collapsedSources());
    if (set.has(id)) {
      set.delete(id);
    } else {
      set.add(id);
    }
    this.collapsedSources.set(set);
  }

  isExpanded(id: string): boolean {
    return !this.collapsedSources().has(id);
  }

  async addSource() {
    const dialogRef = this.dialog.open<any>(SpotifySourceFormDialogComponent, {
      width: '400px',
      data: { mode: 'create' }
    });

    const result = (await firstValueFrom(dialogRef.closed)) as { name: string; maxVolume: number } | undefined;
    if (result) {
      await this.data.createSpotifySource(result.name, result.maxVolume);
    }
  }

  async editSource(source: SpotifySourceDto) {
    const dialogRef = this.dialog.open<any>(SpotifySourceFormDialogComponent, {
      width: '400px',
      data: { mode: 'edit', source }
    });

    const result = (await firstValueFrom(dialogRef.closed)) as { name: string; maxVolume: number } | undefined;
    if (result) {
      await this.data.updateSpotifySource(source.id, result.name, result.maxVolume);
    }
  }

  async deleteSource(id: string, name: string) {
    const ok = await this.confirm.delete('Delete Spotify Source', `Are you sure you want to delete "${name}"?`);
    if (ok) {
      this.data.deleteSpotifySource(id);
    }
  }

  async restartSource(id: string, name: string) {
    const ok = await this.confirm.confirm({
      title: 'Restart Spotify Source',
      message: `Are you sure you want to restart "${name}"? This will terminate the current playback session and restart the Librespot container.`,
      confirmLabel: 'Restart',
      variant: 'default'
    });
    if (ok) {
      this.data.restartSpotifySource(id);
    }
  }

  openUpgradeDialog(source: SpotifySourceDto): void {
    this.dialog.open(SpotifyUpgradeDialogComponent, {
      data: { source }
    });
  }
}
