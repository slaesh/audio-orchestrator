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
  lucideCircleAlert,
  lucideCircleCheck,
  lucideRefreshCw,
} from '@ng-icons/lucide';
import { ZardButtonComponent } from '../../../core/ui/button/button.component';
import { NodeReleaseDto } from '@api';
import { DataService } from '../../../core/services/data.service';
import { MarkdownPipe } from '../../pipes/markdown.pipe';

export interface NodeUpgradeDialogData {
  type: 'spotify' | 'radio-plugin';
  id: string;
  name: string;
  currentVersion: string;
  targetVersion: string;
  repository?: string;
}

export type UpgradePhase = 'idle' | 'pulling' | 'restarting' | 'success' | 'failed';

@Component({
  selector: 'app-node-upgrade-dialog',
  standalone: true,
  imports: [
    CommonModule,
    NgIcon,
    ZardButtonComponent,
    MarkdownPipe,
  ],
  templateUrl: './node-upgrade-dialog.component.html',
  styleUrl: './node-upgrade-dialog.component.scss',
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
      lucideCircleAlert,
      lucideCircleCheck,
      lucideRefreshCw,
    }),
  ],
})
export class NodeUpgradeDialogComponent implements OnInit {
  private data = inject<NodeUpgradeDialogData>(DIALOG_DATA);
  public dialogRef = inject(DialogRef<boolean>);
  private dataService = inject(DataService);

  public node = this.data;
  public loading = signal<boolean>(true);
  public upgradePhase = signal<UpgradePhase>('idle');
  public errorMessage = signal<string>('');
  public releases = signal<NodeReleaseDto[]>([]);

  public get dialogTitle(): string {
    return this.node.type === 'spotify' ? 'Update Spotify Node' : 'Update Radio Plugin';
  }

  public get currentVersion(): string {
    return this.node.currentVersion || '0.1.0';
  }

  public get targetVersion(): string {
    return this.node.targetVersion || 'latest';
  }

  getReleaseUrl(rel: NodeReleaseDto): string {
    if (rel.url) return rel.url;
    const repo = this.node.repository || (this.node.type === 'spotify' ? 'slaesh/librespot-server' : '');
    return repo ? `https://github.com/${repo}/releases/tag/${rel.tagName}` : '#';
  }

  async ngOnInit(): Promise<void> {
    const list = this.node.type === 'spotify'
      ? await this.dataService.getSpotifySourceReleases(this.node.id, this.node.targetVersion)
      : await this.dataService.getRadioPluginReleases(this.node.id, this.node.targetVersion);

    this.releases.set(list);
    this.loading.set(false);
  }

  close(): void {
    this.dialogRef.close(this.upgradePhase() === 'success');
  }

  async upgrade(): Promise<void> {
    if (this.upgradePhase() === 'pulling' || this.upgradePhase() === 'restarting') return;

    this.upgradePhase.set('pulling');
    this.errorMessage.set('');

    const res = this.node.type === 'spotify'
      ? await this.dataService.upgradeSpotifySource(this.node.id, this.node.targetVersion)
      : await this.dataService.upgradeRadioPlugin(this.node.id, this.node.targetVersion);

    if (res.success) {
      this.upgradePhase.set('success');
    } else {
      this.upgradePhase.set('failed');
      this.errorMessage.set(res.error || 'Upgrade failed.');
    }
  }

  retry(): void {
    this.upgradePhase.set('idle');
    this.errorMessage.set('');
  }
}
