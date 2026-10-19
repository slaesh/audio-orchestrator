import { Component, OnInit, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideX,
  lucideCircleArrowUp,
  lucideArrowRight,
  lucideLoaderCircle,
  lucideExternalLink,
  lucideTag,
  lucideSparkles,
} from '@ng-icons/lucide';
import { ZardButtonComponent } from '../../../core/ui/button/button.component';
import { SpotifySourceDto, SpotifyReleaseDto } from '@api';
import { DataService } from '../../../core/services/data.service';

export interface SpotifyUpgradeDialogData {
  source: SpotifySourceDto;
}

@Component({
  selector: 'app-spotify-upgrade-dialog',
  standalone: true,
  imports: [
    CommonModule,
    NgIcon,
    ZardButtonComponent,
  ],
  templateUrl: './spotify-upgrade-dialog.component.html',
  styleUrl: './spotify-upgrade-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [
    provideIcons({
      lucideX,
      lucideCircleArrowUp,
      lucideArrowRight,
      lucideLoaderCircle,
      lucideExternalLink,
      lucideTag,
      lucideSparkles,
    }),
  ],
})
export class SpotifyUpgradeDialogComponent implements OnInit {
  private data = inject<SpotifyUpgradeDialogData>(DIALOG_DATA);
  public dialogRef = inject(DialogRef<boolean>);
  private dataService = inject(DataService);

  public source: SpotifySourceDto = this.data.source;
  public loading = signal<boolean>(true);
  public upgrading = signal<boolean>(false);
  public releases = signal<SpotifyReleaseDto[]>([]);

  public get targetVersion(): string {
    return this.source.latestCompatibleVersion || 'latest';
  }

  getReleaseUrl(rel: SpotifyReleaseDto): string {
    return `https://github.com/slaesh/librespot-server/releases/tag/${rel.tagName}`;
  }

  async ngOnInit(): Promise<void> {
    const list = await this.dataService.getSpotifySourceReleases(
      this.source.id,
      this.source.latestCompatibleVersion ?? undefined,
    );
    this.releases.set(list);
    this.loading.set(false);
  }

  close(): void {
    if (this.upgrading()) return;
    this.dialogRef.close(false);
  }

  async upgrade(): Promise<void> {
    if (this.upgrading()) return;

    this.upgrading.set(true);
    const success = await this.dataService.upgradeSpotifySource(
      this.source.id,
      this.source.latestCompatibleVersion ?? undefined,
    );
    this.upgrading.set(false);

    if (!success) return;
    this.dialogRef.close(true);
  }
}
