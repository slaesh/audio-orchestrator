import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DataService } from '../../../../core/services/data.service';
import { ConfirmService } from '../../../../core/ui/dialog/confirm.service';
import { firstValueFrom } from 'rxjs';
import { ZardButtonComponent } from '../../../../core/ui/button/button.component';
import { ZardBadgeComponent } from '../../../../core/ui/badge/badge.component';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucidePlus, lucideTrash2, lucideRadio, lucideGlobe, lucideSettings, lucideRefreshCcw, lucideMusic, lucideCircleAlert, lucideBoxes, lucideZap, lucideCircleArrowUp } from '@ng-icons/lucide';

import { Dialog } from '@angular/cdk/dialog';
import { RadioFormDialogComponent } from '../../../../shared/dialogs/radio-form-dialog.component';
import { RadioPluginsDialogComponent } from '../../../../shared/dialogs/radio-plugins-dialog.component';
import { NodeUpgradeDialogComponent, NodeUpgradeDialogData } from '../../../../shared/dialogs/node-upgrade-dialog/node-upgrade-dialog.component';
import { StreamDto, RadioPluginDto } from '@api';

@Component({
  selector: 'app-radio-list',
  standalone: true,
  imports: [CommonModule, ZardButtonComponent, ZardBadgeComponent, NgIcon],
  templateUrl: './radio-list.component.html',
  styleUrl: './radio-list.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [provideIcons({ lucidePlus, lucideTrash2, lucideRadio, lucideGlobe, lucideSettings, lucideRefreshCcw, lucideMusic, lucideCircleAlert, lucideBoxes, lucideZap, lucideCircleArrowUp })]
})
export class RadioListComponent {
  public data = inject(DataService);
  private confirm = inject(ConfirmService);
  private dialog = inject(Dialog);

  onManagePlugins() {
    this.dialog.open(RadioPluginsDialogComponent);
  }

  private regexCache = new Map<string, RegExp>();

  private getRegex(pattern: string): RegExp | null {
    let re = this.regexCache.get(pattern);
    if (!re) {
      try {
        re = new RegExp(pattern, 'i');
        this.regexCache.set(pattern, re);
      } catch {
        return null;
      }
    }
    return re;
  }

  getMatchedPlugin(streamUrl: string | null | undefined): RadioPluginDto | null {
    if (!streamUrl) return null;
    for (const p of this.data.radioPlugins()) {
      if (!p.enabled) continue;
      for (const pattern of p.urlPatterns || []) {
        if (!pattern) continue;
        const re = this.getRegex(pattern);
        if (re?.test(streamUrl)) return p;
      }
    }
    return null;
  }

  async onCreateRadio() {
    const dialogRef = this.dialog.open<any>(RadioFormDialogComponent, {
      data: { title: 'Add Radio Station', mode: 'create' }
    });

    const result = await firstValueFrom(dialogRef.closed) as { name: string; url: string } | undefined;
    if (result) {
      this.data.createRadio(result.name, result.url);
    }
  }

  async onEditRadio(stream: StreamDto) {
    const dialogRef = this.dialog.open(RadioFormDialogComponent, {
      data: { 
        title: 'Edit Radio Station', 
        mode: 'edit',
        name: stream.name,
        url: stream.url
      }
    });

    const result = await firstValueFrom(dialogRef.closed) as { name: string; url: string } | undefined;
    if (result) {
      this.data.updateRadio(stream.id, result.name, result.url);
    }
  }

   async onRestartRadio(id: string, name: string) {
     const ok = await this.confirm.confirm({
       title: 'Restart Radio Stream',
       message: `Are you sure you want to restart "${name}"? This will cause a brief audio interruption for all rooms listening to this station.`,
       confirmLabel: 'Restart',
       variant: 'default'
     });
     if (ok) {
       this.data.restartRadio(id);
     }
   }

  async onDeleteRadio(id: string, name: string) {
    const ok = await this.confirm.delete('Remove Radio Station', `Are you sure you want to remove "${name}"?`);
    if (ok) {
      this.data.deleteRadio(id);
    }
  }

  openPluginUpgrade(plugin: RadioPluginDto): void {
    const parts = (plugin.dockerImage || '').split(':');
    const imageWithoutTag = parts[0];
    const segments = imageWithoutTag.split('/');
    const repo = segments.length >= 2 ? `${segments.at(-2)}/${segments.at(-1)}` : undefined;

    this.dialog.open(NodeUpgradeDialogComponent, {
      data: {
        type: 'radio-plugin',
        id: plugin.id,
        name: plugin.name,
        currentVersion: plugin.currentVersion || '0.1.0',
        targetVersion: plugin.latestCompatibleVersion || 'latest',
        repository: repo,
      } satisfies NodeUpgradeDialogData,
    });
  }
}
